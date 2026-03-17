using System.ClientModel;
using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using OpenAI.Responses;

IConfiguration config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .Build();

string endpointRaw = GetRequiredSetting(config, "AZURE_OPENAI_ENDPOINT");
string apiKey = GetRequiredSetting(config, "API_KEY");
string modelDeploymentName = GetRequiredSetting(config, "MODEL_DEPLOYMENT_NAME");
string mcpServerUrlRaw = GetRequiredSetting(config, "MCP_SERVER_URL");
string mcpServerLabel = GetRequiredSetting(config, "MCP_SERVER_LABEL");
string instructions = GetRequiredSetting(config, "AGENT_INSTRUCTIONS");
List<string> allowedTools = ParseCsv(config["ALLOWED_TOOLS"]);

WarnIfObsoleteSettingIsConfigured(config, "PROJECT_ENDPOINT");
WarnIfObsoleteSettingIsConfigured(config, "AGENT_NAME");

Uri endpoint = ValidateAzureOpenAIEndpoint(endpointRaw);
Uri mcpServerUri = ValidateMcpServerUrl(mcpServerUrlRaw);
ValidateAllowedTools(allowedTools);

AzureOpenAIClient client = new(endpoint, new ApiKeyCredential(apiKey));
var responsesClient = client.GetResponsesClient(modelDeploymentName);

try
{
    ResponseResult response = await RunE2EValidationAsync(
        responsesClient,
        instructions,
        mcpServerLabel,
        mcpServerUri,
        allowedTools);

    Console.WriteLine("ExecutionMode: api-key-responses");
    Console.WriteLine($"AzureOpenAIEndpoint: {endpoint}");
    Console.WriteLine($"ModelDeploymentName: {modelDeploymentName}");
    Console.WriteLine($"McpServerLabel: {mcpServerLabel}");
    Console.WriteLine($"McpServerUrl: {mcpServerUri}");
    Console.WriteLine($"AllowedTools: {string.Join(',', allowedTools)}");
    Console.WriteLine("E2EValidation: completed");
    Console.WriteLine($"E2EResponseId: {response.Id}");
    Console.WriteLine($"E2EOutput: {response.GetOutputText()}");
    Console.WriteLine($"APIM Checklist: verify invoked operation is one of [{string.Join(", ", allowedTools)}], with HTTP 200 and latency in APIM logs.");
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

static async Task<ResponseResult> RunE2EValidationAsync(
    ResponsesClient responsesClient,
    string instructions,
    string mcpServerLabel,
    Uri mcpServerUri,
    IReadOnlyList<string> allowedTools)
{
    string toolList = string.Join(", ", allowedTools);
    McpTool mcpTool = ResponseTool.CreateMcpTool(
        serverLabel: mcpServerLabel,
        serverUri: mcpServerUri,
        toolCallApprovalPolicy: new McpToolCallApprovalPolicy(
            GlobalMcpToolCallApprovalPolicy.NeverRequireApproval));

    SetAllowedToolsByReflection(mcpTool, allowedTools);

    CreateResponseOptions options = new(
    [
        ResponseItem.CreateUserMessageItem(
            $"Usa una tool MCP apropiada de esta lista [{toolList}] y retorna un JSON breve con el resultado.")
    ])
    {
        Instructions = instructions,
        ToolChoice = ResponseToolChoice.CreateRequiredChoice()
    };

    options.Tools.Add(mcpTool);

    return await responsesClient.CreateResponseAsync(options);
}

static void SetAllowedToolsByReflection(McpTool mcpTool, IReadOnlyList<string> toolNames)
{
    var property = mcpTool.GetType().GetProperty("AllowedTools");
    if (property is null || !property.CanWrite)
    {
        throw new InvalidOperationException("MCP tool does not expose writable AllowedTools.");
    }

    string json = JsonSerializer.Serialize(new { tool_names = toolNames });
    object? value = JsonSerializer.Deserialize(json, property.PropertyType)
        ?? throw new InvalidOperationException("Unable to assign AllowedTools for MCP tool.");
    property.SetValue(mcpTool, value);
}

static Uri ValidateAzureOpenAIEndpoint(string rawEndpoint)
{
    if (!Uri.TryCreate(rawEndpoint, UriKind.Absolute, out Uri? endpoint) ||
        !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT must be an absolute HTTPS URL.");
    }

    if (!endpoint.Host.Contains(".openai.azure.com", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT must be an Azure OpenAI resource endpoint like 'https://<resource>.openai.azure.com/'.");
    }

    if (rawEndpoint.Contains("/api/projects/", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT cannot be a Foundry project endpoint. Use the Azure OpenAI resource endpoint.");
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

static void WarnIfObsoleteSettingIsConfigured(IConfiguration config, string name)
{
    if (!string.IsNullOrWhiteSpace(config[name]))
    {
        Console.WriteLine($"ObsoleteSettingWarning: {name} is ignored in ApiKey mode.");
    }
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
    ];
}

static void WriteClientError(ClientResultException ex)
{
    Console.Error.WriteLine("Azure OpenAI request failed.");
    Console.Error.WriteLine($"Status: {ex.Status}");
    Console.Error.WriteLine($"Message: {ex.Message}");

    if (ex.Status is 401 or 403)
    {
        Console.Error.WriteLine("Authentication failed. Verify API_KEY and confirm the deployment is accessible from the configured Azure OpenAI resource.");
    }

    if (ex.Status == 404)
    {
        Console.Error.WriteLine("Resource not found. Verify AZURE_OPENAI_ENDPOINT and MODEL_DEPLOYMENT_NAME.");
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
