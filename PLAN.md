# AFClaude — Build Plan

Origin: this plan captures and structures a design chat about wrapping an Azure AI
Foundry model for local use from Claude via Microsoft Agent Framework, .NET 10, `dnx`,
and `az`-based auth. Phases 1–5 are implemented and verified as described below (see
each phase for exactly what "verified" means); Phase 6 is still ahead.

## Open decisions

These were left unresolved in the original design chat. Resolved ones are marked with
the actual finding; the rest still need a decision before the phase that touches them.

1. **MCP transport for .NET — RESOLVED.** Official SDK is NuGet package
   `ModelContextProtocol`, stable at `1.4.0` (also `2.0.0-preview.1` exists — stayed on
   the stable 1.x line). `AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()`
   plus `[McpServerToolType]`/`[McpServerTool]` on `FoundryTools` (Phase 3) — verified
   end to end against the real `AFClaude.dll`: `initialize` and `tools/list` both
   return correct JSON-RPC responses, `ask_foundry` shows up with the right schema.
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
4. **OpenAI .NET SDK message construction — RESOLVED.** Confirmed by reflecting the
   installed `OpenAI` 2.1.0 assembly: `OpenAI.Chat.ChatMessage` is abstract;
   `UserChatMessage(string)`, `SystemChatMessage(string)`, and
   `AssistantChatMessage(string)` each have a plain single-string-content constructor.
   `ChatClient.CompleteChatAsync(IEnumerable<ChatMessage>, ChatCompletionOptions?,
   CancellationToken)` returns `Task<ClientResult<ChatCompletion>>`; the reply text is
   `completion.Value.Content[0].Text` (`ChatCompletion.Content` is a list of
   `ChatMessageContentPart`, each with a `.Text` property) — matches the original
   chat's sketch once the message constructors are fixed. Used as-is in Phase 2.
5. **Streaming.** Neither mode streams yet. Decide whether Claude-side tool calls need
   streaming responses, or whether a single blocking response per tool call is
   acceptable.
6. **Tool name & description exposed to Claude — RESOLVED.** `ask_foundry`, single
   required `prompt` string parameter, description as implemented in
   `src/AFClaude/FoundryTools.cs`. No separate system/context parameter — the agent's
   fixed `instructions` cover that; revisit only if real usage shows it's needed.
7. **`Azure.AI.OpenAI` / `OpenAI` version compatibility gap — discovered, worked
   around.** Latest *stable* `Azure.AI.OpenAI` is `2.1.0`, compiled against `OpenAI`
   `2.1.0`. But `Microsoft.Agents.AI.OpenAI` 1.12.0 pulls in `Microsoft.Extensions.AI.OpenAI`
   10.6.0, which floors the transitive `OpenAI` package at `2.10.0` — NuGet's
   highest-wins resolution then loads `Azure.AI.OpenAI` 2.1.0 against `OpenAI` 2.10.0,
   which is binary-incompatible (`System.MissingMethodException` on
   `ChatCompletionOptions.get_SerializedAdditionalRawData()`, thrown from inside
   `AzureChatClient.PostfixSwapMaxTokens`, reproduced live via `/v1/chat/completions`).
   No stable `Azure.AI.OpenAI` release currently supports `OpenAI` 2.10.x. Worked
   around by pinning `Azure.AI.OpenAI` to the `2.9.0-beta.1` **prerelease**, whose
   floor (`OpenAI` >= 2.9.1) is close enough to the resolved `2.10.0` that the same
   crash does not reproduce. **This is a prerelease dependency in a project with no
   other prereleases — revisit when a stable `Azure.AI.OpenAI` targeting `OpenAI`
   2.10.x+ ships**, and re-run the Phase 2 HTTP smoke test after any future package
   upgrade in this area, since this class of break won't show up as a compile error.

## Phase 1 — Project scaffold — DONE

- `dotnet new sln -n AFClaude` → .NET 10 defaults to the `.slnx` format
  (`AFClaude.slnx`), not `.sln`
- `src/AFClaude/` — the single .NET 10 project hosting both integration modes
  (`dotnet new web -n AFClaude -f net10.0`), added to the solution
- Packages added at these resolved versions: `Azure.AI.OpenAI` 2.9.0-beta.1 (bumped
  from the initial 2.1.0 — see open decision 7), `Azure.Identity` 1.21.0,
  `Microsoft.Agents.AI` 1.12.0, `Microsoft.Agents.AI.OpenAI` 1.12.0,
  `ModelContextProtocol` 1.4.0
- `Program.cs` wires `AzureCliCredential` → `AzureOpenAIClient` → `ChatClient` →
  `AsAIAgent(...)` (see decision 4), gated behind config (`Foundry:Endpoint`,
  `Foundry:Deployment`) with fail-fast validation before the host builds
- Exit criteria — verified: `dotnet run` throws a clear `InvalidOperationException`
  naming the missing env var when config is absent; with valid-shaped config, the app
  builds the agent and starts Kestrel (`GET /` → `200 AFClaude is running.`)

## Phase 2 — HTTP OpenAI-compatible proxy — DONE

- `FoundryClientFactory.cs`: extracted the Phase 1 config-validation +
  `AzureOpenAIClient`/`ChatClient` construction into a shared helper, since Phase 2
  (raw `ChatClient`) and Phase 3 (`ChatClient.AsAIAgent(...)`) need different
  wrappers around the same underlying client — the HTTP proxy passes caller-supplied
  messages straight through rather than going through the agent's fixed
  `instructions`.
- `GET /v1/models` returns the configured deployment; `POST /v1/chat/completions`
  maps `OpenAiMessage.Role` to `UserChatMessage`/`SystemChatMessage`/`AssistantChatMessage`
  (decision 4) and calls `ChatClient.CompleteChatAsync`.
- HTTP mode is opt-in (`--http` arg or `AFClaude__Mode=http` env var); default mode is
  the MCP stdio server, since that's the primary Claude integration path.
- Exit criteria — verified: `GET /v1/models` returns the configured deployment id.
  `POST /v1/chat/completions` against a fake endpoint correctly reaches
  `ChatClient.CompleteChatAsync` and fails only on `AzureCliCredential`'s "Azure CLI
  not installed" (this sandbox has no `az`, and the endpoint isn't real) — i.e.
  request parsing and message construction are proven correct; a **real** Foundry
  round-trip is still unverified and should be re-checked against an actual
  deployment after `az login`.

## Phase 3 — MCP stdio server (primary Claude integration) — DONE

- `FoundryTools.cs`: `[McpServerToolType]` class, constructor-injected `AIAgent`,
  single `[McpServerTool(Name = "ask_foundry")]` method taking a `prompt` string,
  calling `agent.RunAsync(prompt, ...)` and returning `response.Text`
  (`Microsoft.Agents.AI.AgentResponse.Text`).
- `Program.cs` MCP branch: `Host.CreateApplicationBuilder` +
  `builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)`
  (stdout is reserved for JSON-RPC per the MCP stdio spec — logs must not land there)
  + `AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()`.
- Exit criteria — verified against the real `AFClaude.dll`, not just a toy sample:
  sent `initialize` → `notifications/initialized` → `tools/list` over stdin and
  confirmed correct JSON-RPC replies on stdout, including `ask_foundry`'s schema.
  **Tooling note:** naive Bash pipe tests (`cmd < input.jsonl > output`, `coproc`)
  consistently showed 0 bytes on stdout even though the server logged successful
  request handling — a Git Bash/MSYS2 pipe-interop quirk with the `dotnet.exe`
  child process, not a code bug. Confirmed by reproducing the same false-negative on
  a minimal isolated `dotnet new console` + `ModelContextProtocol` sample outside
  this repo. The reliable verification path on this Windows box is a
  `System.Diagnostics.Process` harness (e.g. via PowerShell) with
  `OutputDataReceived`/`ErrorDataReceived` event-based async reads — real Claude
  Code/Desktop MCP clients use native process APIs, not Git Bash pipes, so this
  doesn't affect real usage, only how you'd manually test it from this shell.
  **Not yet done:** registering the built tool in Claude Code's own `.mcp.json` and
  confirming Claude itself can discover/invoke `ask_foundry` in a live conversation.

## Phase 4 — `dnx` packaging + NuGet publish pipeline — DONE (pipeline side)

- `src/AFClaude/AFClaude.csproj`: `IsPackable=true` (the Web SDK defaults this to
  `false`), `PackAsTool=true`, `ToolCommandName=AFClaude`, `PackageId=AFClaude`,
  `Version=0.1.0` (local/dev fallback — the publish workflow overrides via
  `-p:Version=`), plus `Authors`/`Description`/`RepositoryUrl`/`PackageReadmeFile`.
  Verified locally: `dotnet pack -c Release` produces `AFClaude.0.1.0.nupkg`.
  `AFClaude` was unclaimed on nuget.org as of this check.
- `.github/workflows/publish.yml`: builds, packs, and pushes to nuget.org on
  `v*.*.*` tag pushes (or manual `workflow_dispatch` with an explicit version).
  Uses **NuGet Trusted Publishing** (OIDC) — `NuGet/login@v1` exchanges the job's
  GitHub OIDC token for a 1-hour NuGet API key, no long-lived secret stored in the
  repo except the publishing **username** (see below).
- **Required one-time setup on nuget.org (manual — not something this session can
  do):**
  1. Sign in to nuget.org → username menu → **Trusted Publishing** → add a policy
     with:
     - **Repository Owner:** `jamesburton`
     - **Repository:** `AFClaude`
     - **Workflow File:** `publish.yml` (file name only, not the
       `.github/workflows/` path)
     - **Environment:** `nuget` (matches `environment: nuget` in the workflow —
       restricts the policy to jobs running under that GitHub Environment)
  2. In the GitHub repo → Settings → Environments → create an environment named
     `nuget`. Optionally add required reviewers here to gate publishing behind manual
     approval — recommended, since a successful OIDC exchange otherwise publishes
     straight to a public feed.
  3. In the GitHub repo → Settings → Secrets and variables → Actions → add a repo
     secret `NUGET_USER` set to the nuget.org **profile username** (not email) that
     owns the trusted publishing policy — passed as `NuGet/login@v1`'s `user` input.
  4. If the repo is private, note the policy starts in a temporary 7-day-active state
     until the first successful publish; irrelevant once the repo is public.
- Exit criteria — fully verified end to end: pushed `main` + tag `v0.1.0`, the
  `publish.yml` workflow ran (build → pack → `NuGet/login@v1` OIDC exchange →
  `dotnet nuget push`) and succeeded; `AFClaude 0.1.0` is live on
  `api.nuget.org/v3-flatcontainer/afclaude/index.json`. `dnx AFClaude -y` on a clean
  shell (no local build, no local NuGet cache entry for this package) downloaded and
  ran the real published binary and correctly threw the Phase 1 fail-fast
  `InvalidOperationException` on missing config — the stack trace's `/home/runner/...`
  paths confirm it's the GitHub Actions build artifact, not anything local.

## Phase 5 — Auth hardening — DONE

- `FoundryErrors.cs`: classifies `Azure.Identity.CredentialUnavailableException`
  ("az not installed / never logged in") and `AuthenticationFailedException`
  ("`az login` session expired or invalid") into short user-facing messages, with a
  generic fallback for everything else. No stack traces in either message.
- HTTP mode: `/v1/chat/completions` wraps its body in try/catch — auth failures
  return `401` with an OpenAI-style `{error: {message, type: "authentication_error",
  code}}` body; anything else returns `500` with `type: "server_error"`. Full
  exception detail goes to `ILogger`, never to the client.
- MCP mode: `FoundryTools.AskFoundryAsync` catches all exceptions, logs the full
  exception via injected `ILogger<FoundryTools>` (stderr, per the stdio logging
  setup from Phase 3), and returns `FoundryErrors.Describe(ex)` as the tool's normal
  string result — no exception propagates through the MCP protocol layer, so there's
  no dependency on how the SDK would have serialized an unhandled exception.
- Decision: stayed **`AzureCliCredential`-only**, no `DefaultAzureCredential`
  fallback — matches the original ask (Entra/`az` auth specifically) and keeps the
  error surface to two well-understood exception types instead of the wider set
  `DefaultAzureCredential`'s credential chain can throw.
- Exit criteria — verified against the real `AFClaude.dll` with a fake endpoint
  (`az` not installed in this sandbox, so `CredentialUnavailableException` is what
  actually fires — the real, not simulated, "forgot to `az login`" case): HTTP mode
  returns `401` + the clean JSON body above (no stack trace) instead of the raw `500`
  seen in Phase 2; MCP mode's `tools/call` for `ask_foundry` returns the same clean
  message as the tool's `content[0].text`, confirmed over stdio via the
  `System.Diagnostics.Process` harness from Phase 3. The "`az login` expired"
  (`AuthenticationFailedException`) branch is implemented identically but unverified
  live — this sandbox has no `az` at all, so only the "never logged in" path is
  reachable here; re-check the expired-session message against a real `az` install
  when convenient.

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
