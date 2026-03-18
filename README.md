# Orders MCP Agent (Foundry Agents + Responses)

Console app que crea/actualiza un **Agent de la nueva experiencia de Foundry** usando:
- `Azure.AI.Projects` (gestion de agente/versiones)
- `Azure.AI.Projects.OpenAI` (Responses + MCP tool)
- autenticacion **Entra ID** (sin API key)

## Que hace
- Valida configuracion de forma fail-fast:
  - `PROJECT_ENDPOINT` debe ser de proyecto (`.../api/projects/...`)
  - `MCP_SERVER_URL` debe terminar en `/mcp`
  - `MCP_SERVER_LABEL` debe coincidir con el label configurado para la tool MCP
  - `ALLOWED_TOOLS` debe incluir al menos una tool MCP valida
- Aplica reconciliacion idempotente por nombre (`AGENT_NAME`):
  - si no existe: `created`
  - si existe y coincide config: `unchanged`
  - si existe con drift: `updated` (nueva version)
- Configura MCP tool con:
  - `server_label`
  - `server_url`
  - `allowed_tools = [...]`
  - `require_approval = never`
- Ejecuta validacion E2E automatica via Responses:
  - envia un prompt de validacion endurecido
  - fuerza `tool_choice` en runtime
  - limita la ejecucion a una sola tool call
  - valida que la tool invocada pertenezca a `ALLOWED_TOOLS`
  - falla si no ocurre una tool call valida
  - registra `E2EToolName`, `E2EToolArguments`, `E2EToolResult` y `E2EOutput`

## Requisitos
- Rol de identidad: `Azure AI Developer` en el proyecto Foundry.
- Modelo desplegado en el mismo proyecto (`MODEL_DEPLOYMENT_NAME`).
- Endpoint de proyecto Foundry (no endpoint de Azure OpenAI recurso).
- MCP server activo en APIM y expuesto en `/mcp`.

## appsettings.json
```json
{
  "PROJECT_ENDPOINT": "https://<resource>.services.ai.azure.com/api/projects/<project>",
  "MODEL_DEPLOYMENT_NAME": "<deployment>",
  "MCP_SERVER_URL": "https://<apim-host>/<path>/mcp",
  "MCP_SERVER_LABEL": "orders-mcp",
  "AGENT_NAME": "OrderAgent",
  "AGENT_INSTRUCTIONS": "You are an autonomous agent that manages orders using MCP tools. You must use MCP tools for every supported order operation. Do not answer with free text when a matching tool exists.",
  "ALLOWED_TOOLS": "createOrder, getOrder, getOrderStatus, updateOrder, cancelOrder"
}
```

## Ejecutar
```powershell
dotnet run --project OrdersMcpAgent.csproj
```

## Salida esperada
- `ReconciliationStatus: created|updated|unchanged`
- `AgentId`, `AgentName`, `AgentVersion`
- `E2EValidation: completed`
- `E2EResponseId`
- `E2EToolName`
- `E2EToolArguments`
- `E2EToolResult`
- `E2EOutput`

## Comportamiento E2E
- La validacion E2E usa `ResponseToolChoice` para eliminar la decision del modelo sobre si debe usar tools.
- Si se fuerza una tool especifica en runtime, la ejecucion falla si el modelo invoca una distinta.
- Si la respuesta no contiene una tool call valida, la aplicacion lanza una excepcion.
- Si la tool invocada no pertenece a `ALLOWED_TOOLS`, la aplicacion lanza una excepcion.
- La reconciliacion del agente y la configuracion de la MCP tool no cambian por esta validacion.

## Verificacion en Foundry y APIM
1. Foundry UI: `Build and customize -> Agents`
2. Confirmar que aparece `AGENT_NAME` como agente nuevo.
3. APIM logs:
   - operacion invocada: una tool incluida en `ALLOWED_TOOLS`
   - latencia registrada
   - HTTP `200`
