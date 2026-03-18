using System.ClientModel;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using OpenAI.Responses;

IConfiguration config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .Build();

string projectEndpointRaw = GetRequiredSetting(config, "PROJECT_ENDPOINT");
string modelDeploymentName = GetRequiredSetting(config, "MODEL_DEPLOYMENT_NAME");
string mcpServerUrlRaw = GetRequiredSetting(config, "MCP_SERVER_URL");
string mcpServerLabel = GetRequiredSetting(config, "MCP_SERVER_LABEL");
string agentName = GetRequiredSetting(config, "AGENT_NAME");
string agentInstructions = GetRequiredSetting(config, "AGENT_INSTRUCTIONS");
List<string> allowedTools = ParseCsv(config["ALLOWED_TOOLS"]);

Uri projectEndpoint = ValidateProjectEndpoint(projectEndpointRaw);
Uri mcpServerUri = ValidateMcpServerUrl(mcpServerUrlRaw);
ValidateAllowedTools(allowedTools);

DefaultAzureCredential credential = new(new DefaultAzureCredentialOptions
{
    ExcludeInteractiveBrowserCredential = true
});

AIProjectClient projectClient = new(projectEndpoint, credential);

try
{
    await ValidateIdentityCanAccessProjectAsync(projectClient);

    PromptAgentDefinition desiredDefinition = BuildDesiredDefinition(
        modelDeploymentName,
        agentInstructions,
        mcpServerLabel,
        mcpServerUri,
        allowedTools);

    AgentVersion? latestVersion = await TryGetLatestAgentVersionAsync(projectClient, agentName);
    string desiredSignature = BuildDefinitionSignature(desiredDefinition);
    string currentInstructions = latestVersion is null ? string.Empty : ReadDefinitionInstructions(latestVersion.Definition);

    Console.WriteLine($"DesiredInstructions: {agentInstructions}");
    Console.WriteLine($"CurrentInstructions: {currentInstructions}");

    AgentVersion effectiveVersion;
    string reconcileStatus;

    if (latestVersion is null)
    {
        effectiveVersion = await CreateAndValidateAgentVersionAsync(
            projectClient,
            agentName,
            desiredDefinition,
            latestVersion,
            agentInstructions);
        reconcileStatus = "created";
    }
    else
    {
        string currentSignature = BuildDefinitionSignature(latestVersion.Definition);
        if (string.Equals(currentSignature, desiredSignature, StringComparison.Ordinal))
        {
            effectiveVersion = latestVersion;
            reconcileStatus = "unchanged";
        }
        else
        {
            effectiveVersion = await CreateAndValidateAgentVersionAsync(
                projectClient,
                agentName,
                desiredDefinition,
                latestVersion,
                agentInstructions);
            reconcileStatus = "updated";
        }
    }

    Console.WriteLine($"ReconciliationStatus: {reconcileStatus}");
    Console.WriteLine($"AgentId: {effectiveVersion.Id}");
    Console.WriteLine($"AgentName: {effectiveVersion.Name}");
    Console.WriteLine($"AgentVersion: {effectiveVersion.Version}");
    Console.WriteLine($"ModelDeploymentName: {modelDeploymentName}");
    Console.WriteLine($"McpServerLabel: {mcpServerLabel}");
    Console.WriteLine($"McpServerUrl: {mcpServerUri}");
    Console.WriteLine($"AllowedTools: {string.Join(',', allowedTools)}");

    RunE2EValidation(projectClient, agentName, allowedTools);
}
catch (ClientResultException ex)
{
    WriteClientError(ex);
    Environment.ExitCode = 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Unexpected failure.");
    Console.Error.WriteLine(ex.ToString());
    Environment.ExitCode = 1;
}

static PromptAgentDefinition BuildDesiredDefinition(
    string modelDeploymentName,
    string instructions,
    string mcpServerLabel,
    Uri mcpServerUri,
    IReadOnlyList<string> allowedTools)
{
    string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
    string versionedInstructions = $"{instructions}\n\n#version:{timestamp}";

    McpTool mcpTool = ResponseTool.CreateMcpTool(
        serverLabel: mcpServerLabel,
        serverUri: mcpServerUri,
        toolCallApprovalPolicy: new McpToolCallApprovalPolicy(
            GlobalMcpToolCallApprovalPolicy.NeverRequireApproval));

    SetAllowedToolsByReflection(mcpTool, allowedTools);

    return new PromptAgentDefinition(modelDeploymentName)
    {
        Instructions = versionedInstructions,
        Tools = { mcpTool }
    };
}

static void SetAllowedToolsByReflection(McpTool mcpTool, IReadOnlyList<string> toolNames)
{
    var property = mcpTool.GetType().GetProperty("AllowedTools");
    if (property is null || !property.CanWrite)
    {
        throw new InvalidOperationException("MCP tool does not expose writable AllowedTools.");
    }

    string json = JsonSerializer.Serialize(new { tool_names = toolNames });
    object? value = JsonSerializer.Deserialize(json, property.PropertyType) ?? throw new InvalidOperationException("Unable to assign AllowedTools for MCP tool.");
	property.SetValue(mcpTool, value);
}

static string BuildDefinitionSignature(AgentDefinition definition)
{
    string model = NormalizeSignatureValue(ReadStringProperty(definition, "Model"));
    string instructions = NormalizeInstructionsForSignature(ReadDefinitionInstructions(definition));
    string serverLabel = "";
    string serverUrl = "";
    string requireApproval = "";
    List<string> toolNames = [];

    foreach (object tool in ReadTools(definition))
    {
        string toolType = NormalizeSignatureValue(ReadStringProperty(tool, "Type"));
        bool isMcpTool =
            string.Equals(toolType, "mcp", StringComparison.OrdinalIgnoreCase) ||
            tool.GetType().Name.Contains("Mcp", StringComparison.OrdinalIgnoreCase);

        if (!isMcpTool)
        {
            continue;
        }

        serverLabel = NormalizeSignatureValue(ReadStringProperty(tool, "ServerLabel"));
        serverUrl = NormalizeSignatureValue(ReadStringProperty(tool, "ServerUrl"));
        requireApproval = NormalizeRequireApproval(ReadProperty(tool, "RequireApproval"));
        toolNames = [.. ReadAllowedToolNames(tool)
            .Select(NormalizeSignatureValue)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
        break;
    }

    return JsonSerializer.Serialize(new
    {
        model,
        instructions,
        mcp = new
        {
            label = serverLabel,
            url = serverUrl,
            allowedTools = toolNames,
            requireApproval
        }
    });
}

static async Task<AgentVersion> CreateAndValidateAgentVersionAsync(
    AIProjectClient projectClient,
    string agentName,
    PromptAgentDefinition desiredDefinition,
    AgentVersion? latestVersion,
    string desiredInstructions)
{
    AgentVersion createdVersion = await projectClient.Agents.CreateAgentVersionAsync(
        agentName: agentName,
        options: new AgentVersionCreationOptions(desiredDefinition));

    AgentVersion fetchedVersion = await projectClient.Agents.GetAgentVersionAsync(
        agentName: agentName,
        agentVersion: createdVersion.Version);

    string latestInstructions = latestVersion is null ? string.Empty : ReadDefinitionInstructions(latestVersion.Definition);
    string createdInstructions = ReadDefinitionInstructions(fetchedVersion.Definition);
    string expectedInstructions = desiredDefinition.Instructions ?? string.Empty;

    Console.WriteLine($"DesiredInstructions: {desiredInstructions}");
    Console.WriteLine($"CurrentInstructions: {latestInstructions}");
    Console.WriteLine($"CreatedVersionInstructions: {createdInstructions}");

    if (!string.Equals(
        NormalizeInstructionsForComparison(createdInstructions),
        NormalizeInstructionsForComparison(expectedInstructions),
        StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"The created agent version '{fetchedVersion.Version}' did not persist the expected instructions.");
    }

    return fetchedVersion;
}

static string ReadDefinitionInstructions(AgentDefinition definition)
{
    if (definition is PromptAgentDefinition promptDefinition)
    {
        return promptDefinition.Instructions ?? string.Empty;
    }

    return ReadStringProperty(definition, "Instructions");
}

static IEnumerable<object> ReadTools(AgentDefinition definition)
{
    if (ReadProperty(definition, "Tools") is not IEnumerable tools)
    {
        yield break;
    }

    foreach (object? tool in tools)
    {
        if (tool is not null)
        {
            yield return tool;
        }
    }
}

static IReadOnlyList<string> ReadAllowedToolNames(object tool)
{
    object? allowedTools = ReadProperty(tool, "AllowedTools");
    if (allowedTools is null)
    {
        return [];
    }

    if (ReadProperty(allowedTools, "ToolNames") is IEnumerable names)
    {
        return [.. names.Cast<object>()
            .Select(name => name?.ToString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)];
    }

    return [];
}

static object? ReadProperty(object source, params string[] propertyNames)
{
    Type type = source.GetType();
    foreach (string propertyName in propertyNames)
    {
        PropertyInfo? property = type.GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);

        if (property is not null)
        {
            return property.GetValue(source);
        }
    }

    return null;
}

static string ReadStringProperty(object source, params string[] propertyNames)
{
    return ReadProperty(source, propertyNames)?.ToString() ?? string.Empty;
}

static string NormalizeInstructionsForSignature(string instructions)
{
    string withoutVersion = StripVersionSuffix(NormalizeLineEndings(instructions));
    string[] lines = withoutVersion
        .Split('\n')
        .Select(line => Regex.Replace(line.Trim(), @"[ \t]+", " "))
        .ToArray();

    return string.Join('\n', lines).Trim();
}

static string NormalizeInstructionsForComparison(string instructions)
{
    return NormalizeLineEndings(instructions).Trim();
}

static string NormalizeSignatureValue(string value)
{
    return Regex.Replace(NormalizeLineEndings(value).Trim(), @"[ \t]+", " ");
}

static string NormalizeLineEndings(string value)
{
    return (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
}

static string StripVersionSuffix(string instructions)
{
    return Regex.Replace(
        instructions,
        @"(?:\n){2}#version:[^\n]+\s*$",
        string.Empty,
        RegexOptions.CultureInvariant);
}

static string NormalizeRequireApproval(object? approval)
{
    if (approval is null)
    {
        return string.Empty;
    }

    if (ReadProperty(approval, "Always") is not null)
    {
        return "always";
    }

    if (ReadProperty(approval, "Never") is not null)
    {
        return "never";
    }

    string value = approval.ToString() ?? string.Empty;
    if (value.Contains("Always", StringComparison.OrdinalIgnoreCase))
    {
        return "always";
    }

    if (value.Contains("Never", StringComparison.OrdinalIgnoreCase))
    {
        return "never";
    }

    return NormalizeSignatureValue(value);
}

static async Task<AgentVersion?> TryGetLatestAgentVersionAsync(AIProjectClient projectClient, string agentName)
{
    try
    {
        _ = await projectClient.Agents.GetAgentAsync(agentName);
    }
    catch (ClientResultException ex) when (ex.Status == 404)
    {
        return null;
    }

    await foreach (AgentVersion version in projectClient.Agents.GetAgentVersionsAsync(
        agentName: agentName,
        limit: 1,
        order: AgentListOrder.Descending))
    {
        return version;
    }

    return null;
}

static async Task ValidateIdentityCanAccessProjectAsync(AIProjectClient projectClient)
{
    await foreach (AgentRecord _ in projectClient.Agents.GetAgentsAsync(limit: 1))
    {
        break;
    }
}

static void RunE2EValidation(
    AIProjectClient projectClient,
    string agentName,
    IReadOnlyList<string> allowedTools,
    string? forcedTool = null)
{
    if (!string.IsNullOrWhiteSpace(forcedTool) &&
        !allowedTools.Contains(forcedTool, StringComparer.Ordinal))
    {
        throw new InvalidOperationException($"Forced tool '{forcedTool}' is not present in ALLOWED_TOOLS.");
    }

    ProjectResponsesClient client = projectClient.OpenAI.GetProjectResponsesClientForAgent(agentName);
    string toolList = string.Join(", ", allowedTools);

    CreateResponseOptions options = new(
    [
        ResponseItem.CreateUserMessageItem(
            $"Call exactly one MCP tool from this list [{toolList}] and return only the tool result.")
    ])
    {
        ToolChoice = string.IsNullOrWhiteSpace(forcedTool)
            ? ResponseToolChoice.CreateRequiredChoice()
            : ResponseToolChoice.CreateFunctionChoice(forcedTool),
        MaxToolCallCount = 1,
        ParallelToolCallsEnabled = false
    };

    ResponseResult response = client.CreateResponse(options);
    string outputText = response.GetOutputText();
    (string Name, string Arguments, string Output, string Error) mcpCall =
        ExtractSingleMcpToolCall(response, outputText, allowedTools, forcedTool);

    Console.WriteLine("E2EValidation: completed");
    Console.WriteLine($"E2EResponseId: {response.Id}");
    Console.WriteLine($"E2EToolName: {mcpCall.Name}");
    Console.WriteLine($"E2EToolArguments: {mcpCall.Arguments}");
    Console.WriteLine($"E2EToolResult: {mcpCall.Output}");
    if (!string.IsNullOrWhiteSpace(mcpCall.Error))
    {
        Console.WriteLine($"E2EToolError: {mcpCall.Error}");
    }
    Console.WriteLine($"E2EOutput: {outputText}");
    Console.WriteLine($"APIM Checklist: verify invoked operation is one of [{toolList}], with HTTP 200 and latency in APIM logs.");
}

static (string Name, string Arguments, string Output, string Error) ExtractSingleMcpToolCall(
    ResponseResult response,
    string outputText,
    IReadOnlyList<string> allowedTools,
    string? forcedTool)
{
    List<(string Name, string Arguments, string Output, string Error)> mcpCalls = [];

    CollectMcpCallsFromResponseItems(response, mcpCalls);

    if (mcpCalls.Count == 0)
    {
        using JsonDocument document = JsonDocument.Parse(BinaryData.FromObjectAsJson(response));
        CollectMcpCalls(document.RootElement, mcpCalls);

        if (mcpCalls.Count == 0)
        {
            string payloadPreview = CreateJsonPreview(document.RootElement.GetRawText());
            string outputItemsSummary = DescribeOutputItems(response);
            throw new Exception(
                $"E2E validation failed. Response '{response.Id}' did not invoke any MCP tool. OutputText: {outputText}. OutputItems: {outputItemsSummary}. SerializedResponse: {payloadPreview}");
        }
    }

    if (mcpCalls.Count > 1)
    {
        string outputItemsSummary = DescribeOutputItems(response);
        throw new Exception(
            $"E2E validation failed. Response '{response.Id}' invoked {mcpCalls.Count} MCP tools; expected exactly one. OutputItems: {outputItemsSummary}");
    }

    (string Name, string Arguments, string Output, string Error) mcpCall = mcpCalls[0];

    if (string.IsNullOrWhiteSpace(mcpCall.Name))
    {
        string outputItemsSummary = DescribeOutputItems(response);
        throw new Exception(
            $"E2E validation failed. Response '{response.Id}' contains an MCP call without a tool name. OutputItems: {outputItemsSummary}");
    }

    if (!allowedTools.Contains(mcpCall.Name, StringComparer.Ordinal))
    {
        string availableTools = DescribeAdvertisedMcpTools(response);
        throw new Exception(
            $"E2E validation failed. Response '{response.Id}' invoked disallowed tool '{mcpCall.Name}'. AdvertisedMcpTools: {availableTools}");
    }

    if (!string.IsNullOrWhiteSpace(forcedTool) &&
        !string.Equals(mcpCall.Name, forcedTool, StringComparison.Ordinal))
    {
        throw new Exception(
            $"E2E validation failed. Response '{response.Id}' invoked '{mcpCall.Name}' instead of forced tool '{forcedTool}'.");
    }

    if (!string.IsNullOrWhiteSpace(mcpCall.Error))
    {
        throw new Exception(
            $"E2E validation failed. Response '{response.Id}' returned MCP error for tool '{mcpCall.Name}': {mcpCall.Error}");
    }

    return mcpCall;
}

static Uri ValidateProjectEndpoint(string rawEndpoint)
{
    if (!Uri.TryCreate(rawEndpoint, UriKind.Absolute, out Uri? endpoint) ||
        !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("PROJECT_ENDPOINT must be an absolute HTTPS URL.");
    }

    if (!rawEndpoint.Contains("/api/projects/", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("PROJECT_ENDPOINT must be a Foundry Project Endpoint containing '/api/projects/'.");
    }

    if (rawEndpoint.Contains(".openai.azure.com", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("PROJECT_ENDPOINT cannot be an Azure OpenAI resource endpoint. Use the Foundry project endpoint.");
    }

    return endpoint;
}

static Uri ValidateMcpServerUrl(string rawUrl)
{
    if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? mcpUri) ||
        !string.Equals(mcpUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("MCP_SERVER_URL must be an absolute HTTPS URL.");
    }

    if (!mcpUri.AbsolutePath.EndsWith("/mcp", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("MCP_SERVER_URL must end with '/mcp'.");
    }

    return mcpUri;
}


static void ValidateAllowedTools(IReadOnlyCollection<string> allowedTools)
{
    // Admitir múltiples herramientas, pero exigir que exista exactamente el nombre requerido (sensible a mayúsculas/minúsculas)
    if (allowedTools.Count == 0)
    {
        throw new InvalidOperationException("ALLOWED_TOOLS must contain at least one MCP tool name.");
    }
}

static string GetRequiredSetting(IConfiguration config, string name)
{
    string? value = config[name];
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"Missing required setting in appsettings.json: {name}");
    }

    return value;
}

static List<string> ParseCsv(string? csv)
{
    if (string.IsNullOrWhiteSpace(csv))
    {
        return [];
    }

    return
    [
        .. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .ToList()
    ];
}

static string ReadString(JsonElement element, params string[] candidates)
{
    foreach (string name in candidates)
    {
        if (TryGetPropertyInsensitive(element, name, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String)
        {
            return property.GetString() ?? "";
        }
    }

    return "";
}

static string ReadRawJsonOrText(JsonElement element, params string[] candidates)
{
    foreach (string name in candidates)
    {
        if (!TryGetPropertyInsensitive(element, name, out JsonElement property))
        {
            continue;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? "",
            JsonValueKind.Undefined or JsonValueKind.Null => "",
            _ => property.GetRawText()
        };
    }

    return "";
}

static void CollectMcpCalls(
    JsonElement element,
    List<(string Name, string Arguments, string Output, string Error)> mcpCalls)
{
    switch (element.ValueKind)
    {
        case JsonValueKind.Object:
            if (IsMcpCallElement(element))
            {
                string toolName = ReadString(element, "name", "Name");
                string arguments = ReadRawJsonOrText(element, "arguments", "Arguments");
                string result = ReadRawJsonOrText(element, "output", "Output", "result", "Result");
                string error = ReadRawJsonOrText(element, "error", "Error");
                mcpCalls.Add((toolName, arguments, result, error));
                return;
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                CollectMcpCalls(property.Value, mcpCalls);
            }
            break;

        case JsonValueKind.Array:
            foreach (JsonElement item in element.EnumerateArray())
            {
                CollectMcpCalls(item, mcpCalls);
            }
            break;
    }
}

static void CollectMcpCallsFromResponseItems(
    ResponseResult response,
    List<(string Name, string Arguments, string Output, string Error)> mcpCalls)
{
    PropertyInfo? outputItemsProperty = response.GetType().GetProperty("OutputItems");
    if (outputItemsProperty?.GetValue(response) is not System.Collections.IEnumerable outputItems)
    {
        return;
    }

    foreach (object? item in outputItems)
    {
        if (item is null)
        {
            continue;
        }

        string typeName = item.GetType().Name;
        string itemType = ReadPropertyString(item, "Type");
        bool typeLooksLikeCall = typeName.Contains("Call", StringComparison.OrdinalIgnoreCase);

        bool looksLikeMcpCall =
            (typeName.Contains("Mcp", StringComparison.OrdinalIgnoreCase) && typeLooksLikeCall) ||
            string.Equals(itemType, "mcp_call", StringComparison.OrdinalIgnoreCase);

        bool looksLikeFunctionCall =
            typeName.Contains("FunctionCall", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(itemType, "function_call", StringComparison.OrdinalIgnoreCase);

        if (!looksLikeMcpCall && !looksLikeFunctionCall)
        {
            continue;
        }

        string toolName = ReadPropertyString(item, "Name", "FunctionName", "ToolName", "ActionName");
        string arguments = ReadPropertyValue(item, "ToolArguments", "Arguments", "FunctionArguments", "Parameters");
        string result = ReadPropertyValue(item, "ToolOutput", "Output", "Result", "Content");
        string error = ReadPropertyValue(item, "Error", "Failure");

        mcpCalls.Add((toolName, arguments, result, error));
    }
}

static bool IsMcpCallElement(JsonElement element)
{
    string itemType = ReadString(element, "type", "Type");
    if (string.Equals(itemType, "mcp_call", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    if (string.Equals(itemType, "function_call", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(ReadString(element, "name", "Name")))
    {
        return true;
    }

    return false;
}

static bool TryGetPropertyInsensitive(JsonElement element, string candidate, out JsonElement value)
{
    if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(candidate, out value))
    {
        return true;
    }

    if (element.ValueKind == JsonValueKind.Object)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, candidate, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
    }

    value = default;
    return false;
}

static string CreateJsonPreview(string json, int maxLength = 2000)
{
    if (json.Length <= maxLength)
    {
        return json;
    }

    return json[..maxLength] + "...";
}

static string DescribeOutputItems(ResponseResult response)
{
    PropertyInfo? outputItemsProperty = response.GetType().GetProperty("OutputItems");
    if (outputItemsProperty?.GetValue(response) is not System.Collections.IEnumerable outputItems)
    {
        return "(unavailable)";
    }

    List<string> summaries = [];

    foreach (object? item in outputItems)
    {
        if (item is null)
        {
            continue;
        }

        string typeName = item.GetType().FullName ?? item.GetType().Name;
        string id = ReadPropertyString(item, "Id");
        string itemType = ReadPropertyString(item, "Type");
        string name = ReadPropertyString(item, "Name", "FunctionName", "ToolName", "ActionName");
        string detail = DescribeObjectProperties(item);
        summaries.Add($"{typeName}(Id={id},Type={itemType},Name={name},Properties={detail})");
    }

    return summaries.Count == 0 ? "(empty)" : string.Join("; ", summaries);
}

static string DescribeAdvertisedMcpTools(ResponseResult response)
{
    PropertyInfo? outputItemsProperty = response.GetType().GetProperty("OutputItems");
    if (outputItemsProperty?.GetValue(response) is not System.Collections.IEnumerable outputItems)
    {
        return "(unavailable)";
    }

    List<string> toolNames = [];

    foreach (object? item in outputItems)
    {
        if (item is null)
        {
            continue;
        }

        if (!item.GetType().Name.Contains("McpToolDefinitionListItem", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        PropertyInfo? toolDefinitionsProperty = item.GetType().GetProperty("ToolDefinitions");
        if (toolDefinitionsProperty?.GetValue(item) is not System.Collections.IEnumerable toolDefinitions)
        {
            continue;
        }

        foreach (object? toolDefinition in toolDefinitions)
        {
            if (toolDefinition is null)
            {
                continue;
            }

            string toolName = ReadPropertyString(toolDefinition, "Name", "ToolName");
            if (!string.IsNullOrWhiteSpace(toolName))
            {
                toolNames.Add(toolName);
            }
        }
    }

    if (toolNames.Count == 0)
    {
        return "(none)";
    }

    return string.Join(", ", toolNames.Distinct(StringComparer.Ordinal));
}

static string ReadPropertyString(object instance, params string[] propertyNames)
{
    foreach (string propertyName in propertyNames)
    {
        PropertyInfo? property = instance.GetType().GetProperty(propertyName);
        if (property?.GetValue(instance) is string stringValue)
        {
            return stringValue;
        }

        if (property?.GetValue(instance) is BinaryData binaryData)
        {
            return binaryData.ToString();
        }
    }

    return "";
}

static string ReadPropertyValue(object instance, params string[] propertyNames)
{
    foreach (string propertyName in propertyNames)
    {
        PropertyInfo? property = instance.GetType().GetProperty(propertyName);
        if (property is null)
        {
            continue;
        }

        object? value = property.GetValue(instance);
        if (value is null)
        {
            return "";
        }

        if (value is string stringValue)
        {
            return stringValue;
        }

        if (value is BinaryData binaryData)
        {
            return binaryData.ToString();
        }

        return JsonSerializer.Serialize(value);
    }

    return "";
}

static string DescribeObjectProperties(object instance)
{
    List<string> properties = [];

    foreach (PropertyInfo property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
        object? value;
        try
        {
            value = property.GetValue(instance);
        }
        catch
        {
            continue;
        }

        string renderedValue = value switch
        {
            null => "null",
            string s => s,
            BinaryData b => b.ToString(),
            _ when value is System.Collections.IEnumerable && value is not string => value.GetType().Name,
            _ => value.ToString() ?? ""
        };

        properties.Add($"{property.Name}={renderedValue}");
    }

    return string.Join(", ", properties);
}

static void WriteClientError(ClientResultException ex)
{
    Console.Error.WriteLine("Azure request failed.");
    Console.Error.WriteLine($"Status: {ex.Status}");
    Console.Error.WriteLine($"Message: {ex.Message}");

    if (ex.Status is 401 or 403)
    {
        Console.Error.WriteLine("Identity authorization check failed. Ensure the principal has 'Azure AI Developer' role in the project.");
    }

    if (ex.Status == 404)
    {
        Console.Error.WriteLine("Resource not found. Verify model deployment exists in the same Foundry project.");
    }

    if (ex.GetRawResponse() is { } rawResponse)
    {
        string requestId = rawResponse.Headers.TryGetValue("x-request-id", out string? rid)
            ? rid ?? "(unavailable)"
            : "(unavailable)";
        string clientRequestId = rawResponse.Headers.TryGetValue("x-ms-client-request-id", out string? crid)
            ? crid ?? "(unavailable)"
            : "(unavailable)";
        Console.Error.WriteLine($"RequestId: {requestId}");
        Console.Error.WriteLine($"ClientRequestId: {clientRequestId}");
    }
}
