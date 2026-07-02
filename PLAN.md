# AFClaude — Build Plan

Origin: this plan captures and structures a design chat about wrapping an Azure AI
Foundry model for local use from Claude via Microsoft Agent Framework, .NET 10, `dnx`,
and `az`-based auth. Nothing below is implemented yet — this is the plan, not a status
report.

## Open decisions

These were left unresolved in the original design chat. Resolved ones are marked with
the actual finding; the rest still need a decision before the phase that touches them.

1. **MCP transport for .NET — RESOLVED.** Official SDK is NuGet package
   `ModelContextProtocol`, stable at `1.4.0` (also `2.0.0-preview.1` exists — stayed on
   the stable 1.x line). Added to `src/AFClaude/AFClaude.csproj` in Phase 1. Stdio
   server wiring (`WithStdioServerTransport()`, `McpServerTool` attributes) is Phase 3
   work, not yet implemented.
2. **Foundry endpoint style.** Still open — Azure OpenAI resource endpoints
   (`https://<resource>.openai.azure.com/`) and Azure AI Foundry *project* endpoints
   (`https://<project>.services.ai.azure.com/`) are different shapes with possibly
   different SDK entry points (`AzureOpenAIClient` vs a Foundry project client). Phase
   1 code assumes the resource-endpoint shape (`AzureOpenAIClient(Uri,
   TokenCredential)`). Confirm which one applies to the actual target deployment
   before relying on this against a real resource.
3. **Entra scope / token audience.** Still open, but likely moot: `AzureOpenAIClient`
   sets its own token scope internally when given a `TokenCredential` — no code in
   Phase 1 hand-rolls a scope. Revisit only if auth fails against a real resource in a
   way that suggests a scope mismatch.
4. **OpenAI .NET SDK message construction — RESOLVED (partially).** Confirmed via
   `dotnet build` against the real `OpenAI` 2.1.0 package: `OpenAI.Chat.ChatMessage`
   is indeed abstract. The concrete agent-wrapping surface actually used in Phase 1 is
   `OpenAI.Chat.OpenAIChatClientExtensions.AsAIAgent(this ChatClient, ...)` →
   `ChatClientAgent` (from `Microsoft.Agents.AI.OpenAI`, requires `using OpenAI.Chat;`
   for the extension method to resolve) — not the manual `new ChatMessage(...)` +
   `ChatClientAgent` constructor sketched in the original chat. Phase 2's HTTP request
   parsing into concrete `UserChatMessage`/`SystemChatMessage`/`AssistantChatMessage`
   is still unverified — confirm when Phase 2 is implemented.
5. **Streaming.** Neither mode streams yet. Decide whether Claude-side tool calls need
   streaming responses, or whether a single blocking response per tool call is
   acceptable.
6. **Tool name & description exposed to Claude.** Working name `ask_foundry`. Needs an
   input schema (prompt, optional system/context) and a description Claude can use to
   decide when to invoke it.

## Phase 1 — Project scaffold — DONE

- `dotnet new sln -n AFClaude` → .NET 10 defaults to the `.slnx` format
  (`AFClaude.slnx`), not `.sln`
- `src/AFClaude/` — the single .NET 10 project hosting both integration modes
  (`dotnet new web -n AFClaude -f net10.0`), added to the solution
- Packages added at these resolved versions: `Azure.AI.OpenAI` 2.1.0, `Azure.Identity`
  1.21.0, `Microsoft.Agents.AI` 1.12.0, `Microsoft.Agents.AI.OpenAI` 1.12.0,
  `ModelContextProtocol` 1.4.0
- `Program.cs` wires `AzureCliCredential` → `AzureOpenAIClient` → `ChatClient` →
  `AsAIAgent(...)` (see decision 4), gated behind config (`Foundry:Endpoint`,
  `Foundry:Deployment`) with fail-fast validation before the host builds
- Exit criteria — verified: `dotnet run` throws a clear `InvalidOperationException`
  naming the missing env var when config is absent; with valid-shaped config, the app
  builds the agent and starts Kestrel (`GET /` → `200 AFClaude is running.`)

## Phase 2 — HTTP OpenAI-compatible proxy

- `GET /v1/models` returning the configured deployment
- `POST /v1/chat/completions` (non-streaming first), using correctly-typed
  `OpenAI.Chat` message classes (see decision 4)
- Manual smoke test against a real Foundry deployment after `az login`
- Exit criteria: a plain `curl`/`Invoke-RestMethod` call to
  `http://127.0.0.1:5277/v1/chat/completions` round-trips a real completion

## Phase 3 — MCP stdio server (primary Claude integration)

- Add the stdio MCP host alongside (or instead of, per decision) the HTTP host
- Define the `ask_foundry` tool: schema, description, handler that calls the same
  `ChatClientAgent` used by Phase 2
- Exit criteria: register the built binary in Claude Code's `.mcp.json` locally and
  confirm Claude can discover and invoke the tool in a real conversation

## Phase 4 — `dnx` packaging

- Set `PackAsTool=true`, `ToolCommandName=AFClaude` (or `afclaude`) in the project file
- `dotnet pack -c Release`, then verify `dnx AFClaude --yes` launches the same way a
  fresh machine (no prior `dotnet build`) would invoke it
- Exit criteria: launching via the `.mcp.json` `command`/`args` from README works from
  a clean shell with only `az login` having been run first

## Phase 5 — Auth hardening

- Confirm behavior when `az login` has expired or was never run (should fail with a
  clear, actionable error, not a cryptic 401 deep in the SDK)
- Decide whether to fall back to `DefaultAzureCredential` (managed identity, VS/VS
  Code credential, etc.) for non-interactive/CI use, or stay `AzureCliCredential`-only
  for simplicity
- Exit criteria: documented, reproducible error message for the "forgot to `az login`"
  case

## Phase 6 — Polish (only once 1–5 work end to end)

- Streaming, if decided in scope
- Error surface parity between HTTP and MCP modes (same underlying failures, mode
  -appropriate presentation)
- Basic integration test hitting a real (or recorded) Foundry response

## Explicitly out of scope for now

- Multi-deployment / multi-model routing (single `Foundry:Deployment` only)
- API-key auth path (Entra/`az` only, per the original ask)
- Anthropic-shaped (`/v1/messages`) endpoint — only OpenAI-compatible HTTP shape and
  native MCP tool calling are in scope
