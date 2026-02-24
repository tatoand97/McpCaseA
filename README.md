# Orders MCP Agent (Foundry Agents + Responses)

Console app que crea/actualiza un **Agent de la nueva experiencia de Foundry** usando:
- `Azure.AI.Projects` (gestion de agente/versiones)
- `Azure.AI.Projects.OpenAI` (Responses + MCP tool)
- autenticacion **Entra ID** (sin API key)

## Que hace
- Valida configuracion de forma fail-fast:
  - `PROJECT_ENDPOINT` debe ser de proyecto (`.../api/projects/...`)
  - `MCP_SERVER_URL` debe terminar en `/mcp`
  - `MCP_SERVER_LABEL` solo alfanumerico + `_` (sin guiones)
  - `ALLOWED_TOOLS` debe resolver exactamente a `getOrderStatus`
- Aplica reconciliacion idempotente por nombre (`AGENT_NAME`):
  - si no existe: `created`
  - si existe y coincide config: `unchanged`
  - si existe con drift: `updated` (nueva version)
- Configura MCP tool con:
  - `server_label`
  - `server_url`
  - `allowed_tools = [getOrderStatus]`
  - `require_approval = always`
- Ejecuta validacion E2E automatica via Responses:
  - envia prompt de prueba
  - atiende `mcp_approval_request`
  - aprueba solo si el `server_label` coincide

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
  "MCP_SERVER_LABEL": "orders_mcp",
  "AGENT_NAME": "OrderAgent",
  "AGENT_INSTRUCTIONS": "You are an agent that retrieves order status using the approved tool only.",
  "ALLOWED_TOOLS": "getOrderStatus"
}
```

## Ejecutar
```powershell
dotnet run --project OrdersMcpAgent.csproj
```

## Salida esperada
- `ReconciliationStatus: created|updated|unchanged`
- `AgentId`, `AgentName`, `AgentVersion`
- Resultado E2E y `E2EOutput`

## Verificacion en Foundry y APIM
1. Foundry UI: `Build and customize -> Agents`
2. Confirmar que aparece `AGENT_NAME` como agente nuevo.
3. APIM logs:
   - operacion invocada: `getOrderStatus`
   - latencia registrada
   - HTTP `200`
