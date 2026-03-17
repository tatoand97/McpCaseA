# Orders MCP Client (Azure OpenAI Responses + ApiKey)

Console app que ejecuta un flujo **data-plane** con `Responses` usando:
- `Azure.AI.OpenAI`
- autenticacion por **ApiKey**
- tool MCP declarada en cada request

## Que hace
- Valida configuracion de forma fail-fast:
  - `AZURE_OPENAI_ENDPOINT` debe ser un endpoint de recurso Azure OpenAI (`https://<resource>.openai.azure.com/`)
  - `API_KEY` es obligatoria
  - `MCP_SERVER_URL` debe terminar en `/mcp`
  - `ALLOWED_TOOLS` debe incluir al menos una tool MCP valida
- Ejecuta una llamada `Responses` contra `MODEL_DEPLOYMENT_NAME`
- Inyecta en el request:
  - `AGENT_INSTRUCTIONS`
  - `server_label`
  - `server_url`
  - `allowed_tools = [...]`
  - `require_approval = never`
- Ejecuta una validacion E2E automatica que obliga el uso de tool MCP

## Cambio funcional relevante
- Ya no crea ni actualiza agentes de Foundry.
- Ya no usa `AIProjectClient`, `PROJECT_ENDPOINT` ni `Managed Identity`.
- `PROJECT_ENDPOINT` y `AGENT_NAME` quedan obsoletos y, si se configuran, solo generan advertencia.

## Requisitos
- Recurso Azure OpenAI con API key valida.
- Deployment existente en ese recurso (`MODEL_DEPLOYMENT_NAME`).
- MCP server activo en APIM y expuesto en `/mcp`.

## appsettings.json
```json
{
  "AZURE_OPENAI_ENDPOINT": "https://<resource>.openai.azure.com/",
  "API_KEY": "<api-key>",
  "MODEL_DEPLOYMENT_NAME": "<deployment>",
  "MCP_SERVER_URL": "https://<apim-host>/<path>/mcp",
  "MCP_SERVER_LABEL": "orders-mcp",
  "AGENT_INSTRUCTIONS": "You are an autonomous agent that manages orders using MCP tools. Use tools whenever a tool matches the user request and choose the correct tool from the decision rules.",
  "ALLOWED_TOOLS": "createOrder, getOrder, getOrderStatus, updateOrder, cancelOrder",
  "PROJECT_ENDPOINT": "",
  "AGENT_NAME": ""
}
```

## Ejecutar
```powershell
dotnet run --project OrdersMcpAgent.csproj
```

## Salida esperada
- `ExecutionMode: api-key-responses`
- `AzureOpenAIEndpoint`
- `ModelDeploymentName`
- `E2EValidation: completed`
- `E2EResponseId`
- `E2EOutput`

## Verificacion en APIM
1. Confirmar que se invoca una operacion incluida en `ALLOWED_TOOLS`.
2. Confirmar latencia registrada.
3. Confirmar HTTP `200`.
