using System.ClientModel;
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

    AgentVersion effectiveVersion;
    string reconcileStatus;

    if (latestVersion is null)
    {
        effectiveVersion = await projectClient.Agents.CreateAgentVersionAsync(
            agentName: agentName,
            options: new AgentVersionCreationOptions(desiredDefinition));
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
            effectiveVersion = await projectClient.Agents.CreateAgentVersionAsync(
                agentName: agentName,
                options: new AgentVersionCreationOptions(desiredDefinition));
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
    McpTool mcpTool = ResponseTool.CreateMcpTool(
        serverLabel: mcpServerLabel,
        serverUri: mcpServerUri,
        toolCallApprovalPolicy: new McpToolCallApprovalPolicy(
            GlobalMcpToolCallApprovalPolicy.NeverRequireApproval));

    SetAllowedToolsByReflection(mcpTool, allowedTools);

    return new PromptAgentDefinition(modelDeploymentName)
    {
        Instructions = instructions,
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
    using JsonDocument document = JsonDocument.Parse(BinaryData.FromObjectAsJson(definition).ToString());

    string model = ReadString(document.RootElement, "model", "Model");
    string instructions = ReadString(document.RootElement, "instructions", "Instructions");
    string serverLabel = "";
    string serverUrl = "";
    string requireApproval = "";
    List<string> toolNames = [];

    JsonElement tools = document.RootElement.TryGetProperty("tools", out JsonElement t1)
        ? t1
        : document.RootElement.TryGetProperty("Tools", out JsonElement t2)
            ? t2
            : default;

    if (tools.ValueKind == JsonValueKind.Array)
    {
        foreach (JsonElement tool in tools.EnumerateArray())
        {
            string toolType = ReadString(tool, "type", "Type");
            if (!string.Equals(toolType, "mcp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            serverLabel = ReadString(tool, "server_label", "serverLabel", "ServerLabel");
            serverUrl = ReadString(tool, "server_url", "serverUrl", "ServerUrl");

            JsonElement allowed = tool.TryGetProperty("allowed_tools", out JsonElement a1)
                ? a1
                : tool.TryGetProperty("allowedTools", out JsonElement a2)
                    ? a2
                    : tool.TryGetProperty("AllowedTools", out JsonElement a3)
                        ? a3
                        : default;

            JsonElement names = allowed.TryGetProperty("tool_names", out JsonElement n1)
                ? n1
                : allowed.TryGetProperty("toolNames", out JsonElement n2)
                    ? n2
                    : allowed.TryGetProperty("ToolNames", out JsonElement n3)
                        ? n3
                        : default;

            if (names.ValueKind == JsonValueKind.Array)
            {
                toolNames = [.. names.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(v => !string.IsNullOrWhiteSpace(v))];
            }

            JsonElement approval = tool.TryGetProperty("require_approval", out JsonElement r1)
                ? r1
                : tool.TryGetProperty("requireApproval", out JsonElement r2)
                    ? r2
                    : tool.TryGetProperty("RequireApproval", out JsonElement r3)
                        ? r3
                        : default;

            if (approval.ValueKind == JsonValueKind.Object)
            {
                if (approval.TryGetProperty("always", out _) || approval.TryGetProperty("Always", out _))
                {
                    requireApproval = "always";
                }
                else if (approval.TryGetProperty("never", out _) || approval.TryGetProperty("Never", out _))
                {
                    requireApproval = "never";
                }
            }
        }
    }

    toolNames = [.. toolNames.Order(StringComparer.Ordinal)];

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
    using JsonDocument document = JsonDocument.Parse(BinaryData.FromObjectAsJson(response));

    if (!document.RootElement.TryGetProperty("output", out JsonElement outputItems) ||
        outputItems.ValueKind != JsonValueKind.Array)
    {
        throw new Exception($"E2E validation failed. Response '{response.Id}' does not contain an output array.");
    }

    List<(string Name, string Arguments, string Output, string Error)> mcpCalls = [];

    foreach (JsonElement item in outputItems.EnumerateArray())
    {
        string itemType = ReadString(item, "type", "Type");
        if (!string.Equals(itemType, "mcp_call", StringComparison.Ordinal))
        {
            continue;
        }

        string toolName = ReadString(item, "name", "Name");
        string arguments = ReadRawJsonOrText(item, "arguments", "Arguments");
        string result = ReadRawJsonOrText(item, "output", "Output");
        string error = ReadRawJsonOrText(item, "error", "Error");

        mcpCalls.Add((toolName, arguments, result, error));
    }

    if (mcpCalls.Count == 0)
    {
        throw new Exception(
            $"E2E validation failed. Response '{response.Id}' did not invoke any MCP tool. OutputText: {outputText}");
    }

    if (mcpCalls.Count > 1)
    {
        throw new Exception(
            $"E2E validation failed. Response '{response.Id}' invoked {mcpCalls.Count} MCP tools; expected exactly one.");
    }

    (string Name, string Arguments, string Output, string Error) mcpCall = mcpCalls[0];

    if (string.IsNullOrWhiteSpace(mcpCall.Name))
    {
        throw new Exception($"E2E validation failed. Response '{response.Id}' contains an MCP call without a tool name.");
    }

    if (!allowedTools.Contains(mcpCall.Name, StringComparer.Ordinal))
    {
        throw new Exception(
            $"E2E validation failed. Response '{response.Id}' invoked disallowed tool '{mcpCall.Name}'.");
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
        if (element.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String)
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
        if (!element.TryGetProperty(name, out JsonElement property))
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
