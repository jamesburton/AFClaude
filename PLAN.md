# AFClaude — Build Plan

Origin: this plan captures and structures a design chat about wrapping an Azure AI
Foundry model for local use from Claude via Microsoft Agent Framework, .NET 10, `dnx`,
and `az`-based auth. Phases 1–9 are implemented and verified as described below (see
each phase for exactly what "verified" means); Phase 10 is still ahead.

## Open decisions

These were left unresolved in the original design chat. Resolved ones are marked with
the actual finding; the rest still need a decision before the phase that touches them.

1. **MCP transport for .NET — RESOLVED.** Official SDK is NuGet package
   `ModelContextProtocol`, stable at `1.4.0` (also `2.0.0-preview.1` exists — stayed on
   the stable 1.x line). `AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()`
   plus `[McpServerToolType]`/`[McpServerTool]` on `FoundryTools` (Phase 3) — verified
   end to end against the real `AFClaude.dll`: `initialize` and `tools/list` both
   return correct JSON-RPC responses, `ask_foundry` shows up with the right schema.
2. **Foundry endpoint style — RESOLVED (Phase 8, real-Foundry run).** The shape that
   `az cognitiveservices account list` reports as `properties.endpoint` is what works
   with `AzureOpenAIClient(Uri, TokenCredential)`. On the tested org (fnz-qhub), all
   AIServices accounts reported `https://<resource>.cognitiveservices.azure.com/`,
   which passed every stage against a real gpt-4.1 deployment.
   `.openai.azure.com` / `.services.ai.azure.com` shapes were not exercised (the
   first shape worked immediately) — if a future org reports one of those, try it
   as-is before assuming a different SDK entry point is needed.
3. **Entra scope / token audience — RESOLVED (Phase 8).** Moot as predicted:
   `AzureOpenAIClient` requests `https://cognitiveservices.azure.com/.default`
   internally and real-Foundry auth succeeded with no scope handling in our code.
   (That scope is now also referenced explicitly by launch mode's token warm-up —
   `FoundryClientFactory.TokenScope`.) The auth failure that *did* occur was RBAC,
   not scope — see Phase 8.
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

## Phase 6 — `launch` command + Anthropic Messages API endpoint — DONE (text-only)

**Origin:** a follow-up ask — support `dnx AFClaude -- launch [...args]` to start the
HTTP proxy and run `claude` (Claude Code) in the same session, routed to the Foundry
model via env vars.

**Key finding (research, not assumption):** Claude Code's `ANTHROPIC_BASE_URL` only
speaks the **Anthropic Messages API** wire format (`POST /v1/messages`) — it has no
OpenAI-compatibility mode. Pointing Claude Code at the existing `/v1/chat/completions`
endpoint (OpenAI-shaped) would not work; every request would fail to parse. Source:
Claude Code's own gateway-protocol docs, cited via a `claude-code-guide` research
task — `ANTHROPIC_BASE_URL` → `Selected by` → "Anthropic Messages" →
`/v1/messages` (+ optional `/v1/messages/count_tokens`); no `OPENAI_BASE_URL` or
format-translation exists.

- `AnthropicMessages.cs`: `AnthropicMessagesRequest`/`AnthropicMessageIn` DTOs
  (`max_tokens` needs an explicit `[JsonPropertyName]` — camelCase-insensitive
  matching doesn't bridge `max_tokens` → `MaxTokens`) and `AnthropicContent.ExtractText`,
  which accepts Anthropic's `content` field in either shape (plain string, or an
  array of blocks) and concatenates `type: "text"` blocks.
- `POST /v1/messages` added to the same `BuildHttpApp` host as the OpenAI endpoints
  (`--http` mode now serves both shapes on one Kestrel instance). Supports both
  `stream: false` (plain JSON) and `stream: true` (a **coalesced** SSE burst —
  `message_start` → `content_block_start` → one `content_block_delta` carrying the
  full reply → `content_block_stop` → `message_delta` → `message_stop` — not
  incremental token-by-token streaming from Azure). Same auth-failure/generic-failure
  classification and Anthropic-shaped `{type: "error", error: {type, message}}` body
  as the OpenAI endpoint's `{error: {...}}` shape, via the shared `FoundryErrors`
  helper.
- **Scope explicitly limited to text — no tool-use bridging.** `tool_use`/
  `tool_result` content blocks and the `tools` request field are not translated.
  This means **Claude Code's actual tools (Read/Edit/Bash/etc.) will not function**
  against this endpoint — only plain conversational text. This was a scope decision
  surfaced to the user (full tool-use bridging vs. text-only vs. text-first) with no
  reply received in time; proceeded with the smallest working increment per the
  fallback default, consistent with how every other phase in this plan started
  small and verified before extending. **Follow-up done in Phase 7** (tool-use
  bridging); real incremental Azure streaming remains (Phase 8).
- `launch` subcommand (`Program.cs`): `dnx AFClaude -- launch [claude-args...]` —
  fail-fast config check (reusing `FoundryClientFactory.Create`) → starts the shared
  HTTP host bound to a fixed local port (`http://127.0.0.1:31337`, overridable via
  `AFClaude__Launch__Port`) → spawns `claude` as a child process with
  `ANTHROPIC_BASE_URL` set to that URL, `ANTHROPIC_MODEL` set to the configured
  Foundry deployment, and a placeholder non-empty `ANTHROPIC_API_KEY` (the proxy
  itself doesn't check any auth header, but Claude Code likely expects *some*
  credential present to avoid its interactive OAuth login flow — unconfirmed by the
  research beyond that env var's existence, so treat this as a reasonable default,
  not a verified requirement) → forwards all args after `launch` straight to
  `claude` → waits for it to exit → stops the HTTP host → exits with `claude`'s exit
  code.
- Exit criteria — verified live, twice: (1) missing config fails fast before the
  HTTP host binds or `claude` spawns, identical to Phase 1's exit criteria; (2) with
  valid-shaped config, `dnx AFClaude launch --version` started the proxy on
  `127.0.0.1:31337`, spawned `claude --version` with the env vars set, which printed
  its real version and exited 0, after which AFClaude stopped the host and exited 0
  itself. **Not yet verified:** an actual interactive `claude` conversation
  round-tripping through `/v1/messages` against a real Foundry deployment — this
  needs a real endpoint + `az login`, and (per the scope note above) will currently
  only work for plain chat, not tool-using turns.

## Phase 7 — Tool-use bridging for `/v1/messages` — DONE

Closes the Phase 6 gap: `claude launch` was previously chat-only because
`tools`/`tool_use`/`tool_result` were dropped in translation. The scope question
(full bridging vs. text-only) was re-surfaced to the user at the start of this
session with no reply; proceeded with the recommended full-bridging option per the
same fallback rule as Phase 6.

- `AnthropicMessages.cs` — extended into a full bidirectional bridge
  (`AnthropicBridge`):
  - Request: `tools` (name/description/`input_schema`) →
    `ChatTool.CreateFunctionTool(...)`; built-in/server tool types without an
    `input_schema` object (e.g. `web_search_20250305`) are skipped, since they have
    no function-calling counterpart. `tool_choice` `auto`/`any`/`tool`/`none` →
    `ChatToolChoice.CreateAutoChoice/CreateRequiredChoice/CreateFunctionChoice/
    CreateNoneChoice`; `disable_parallel_tool_use` → `AllowParallelToolCalls`.
    `max_tokens`/`temperature`/`top_p`/`stop_sequences` now also pass through via
    `ChatCompletionOptions` (previously ignored).
  - History: assistant `tool_use` blocks →
    `ChatToolCall.CreateFunctionToolCall(id, name, input-JSON)` on an
    `AssistantChatMessage` (text blocks ride along as a content part); user
    `tool_result` blocks → standalone `ToolChatMessage(tool_use_id, text)` emitted
    **before** any user text, because OpenAI requires tool-role messages to directly
    follow the assistant message that made the calls (Anthropic instead nests results
    inside the next user message). `thinking`/image/unknown blocks are dropped.
  - Response: `ChatCompletion.ToolCalls` → Anthropic `tool_use` content blocks
    (arguments parsed to a JSON `input` object; malformed arguments degrade to `{}`
    rather than a 500); finish reason → `stop_reason` (`ToolCalls`/any-tool-call →
    `tool_use`, `Length` → `max_tokens`, else `end_turn`). Real
    `usage.input_tokens`/`output_tokens` from `ChatCompletion.Usage` (previously
    hardcoded 0).
  - Streaming: the coalesced SSE burst now emits one content block per output block —
    text blocks as `text_delta`, tool_use blocks as `content_block_start` (id/name) +
    `input_json_delta` (full arguments as one `partial_json`) — still not
    incremental token-by-token streaming (see Phase 8).
- API surface verified by reflecting the **resolved** `OpenAI` 2.10.0 assembly (the
  one the `Azure.AI.OpenAI` 2.9.0-beta.1 pin actually loads) before writing code —
  same discipline as decision 4, and how `functionSchemaIsStrict`,
  `AssistantChatMessage(IEnumerable<ChatToolCall>)`, and
  `OpenAIChatModelFactory.ChatCompletion(...)` were confirmed.
- `tests/AFClaude.Tests` — new xUnit project (added to `AFClaude.slnx`,
  `InternalsVisibleTo` from the main project; **not** packed). 13 tests cover both
  directions of the bridge; the response direction uses
  `OpenAIChatModelFactory.ChatCompletion(...)` test doubles (experimental API —
  `OPENAI001` suppressed in the test project only), which is the only way to
  exercise OpenAI→Anthropic translation without real Azure auth. `publish.yml`
  gained a `dotnet test` step between Build and Pack, so publishing is now gated on
  the suite.
- Exit criteria — verified: all 13 unit tests pass (`dotnet test -c Release
  --no-build`, exactly as CI runs it); live HTTP smoke test (PowerShell
  `Start-Process` harness, fake endpoint) POSTed a full tool conversation — `tools`
  array with one function tool + one built-in, `tool_choice`, assistant `tool_use`
  history, user `tool_result` + text — to `/v1/messages` in both `stream: false`
  and `stream: true` modes and got the clean Phase 5 `401 authentication_error` in
  both, proving the entire request-side translation executes and fails only at the
  real Azure auth boundary. **Still unverified (needs real `az login` + endpoint):**
  a genuine tool-calling round trip where the model actually returns function calls.

## Phase 8 — Real-Foundry verification + auth robustness — DONE

**First genuine Azure round trip**, run via `TESTING.md` on the Framework machine
(Windows 11, `az` configured for the fnz-qhub tenant; resource
`qhub-infra-resource` in `rg-qhub-infra`, deployment `gpt-4.1`, endpoint shape
`https://<resource>.cognitiveservices.azure.com/`). Results on v0.2.0:
Stages 1–6b all PASS — real `/v1/chat/completions`, `/v1/messages` text +
streaming (real usage tokens), tool_use emission **and** tool_result round trip,
MCP `ask_foundry`, `launch --version`, and `launch -p` text (the placeholder
`ANTHROPIC_API_KEY=afclaude-local` assumption **held** — no OAuth prompt).
`/v1/messages/count_tokens` was never requested by claude in these runs.

Two real defects surfaced, both fixed in this phase:

1. **`AzureCliCredential`'s fixed 13s `ProcessTimeout` is too short** — a cold `az`
   start took 14–24s on the test machine, so any request needing a fresh token
   reliably failed (Stage 6c FAIL: each `launch` starts a new process → uncached
   token → timeout). Fix: `ProcessTimeout` now defaults to **60s**, configurable
   via `Foundry__CliTimeoutSeconds`; tokens are cached in-process
   (`CachingTokenCredential` — `AzureCliCredential` itself spawns `az` on *every*
   `GetToken` and never caches, refresh 5 min before expiry); and `launch` mode
   **warms the token before spawning `claude`** through the same credential
   instance the request pipeline uses, failing fast with a classified message
   instead of letting every claude turn 401.
2. **The "session may have expired" message was misleading** — observed firing for
   both the az-timeout case above and a **data-plane RBAC gap** (subscription Owner
   without "Cognitive Services OpenAI User" on the resource; control-plane roles do
   not grant inference under Entra auth — fixed on the org side with
   `az role assignment create --role "Cognitive Services OpenAI User"` + ~60–90s
   propagation). Fix: `FoundryErrors` now classifies four distinct auth failures —
   az missing (`CredentialUnavailableException`), az token timeout
   (`AuthenticationFailedException` + "timed out", points at
   `Foundry__CliTimeoutSeconds`), genuinely expired/wrong-tenant session, and
   service 401/403 (`ClientResultException`/`RequestFailedException` status,
   points at the RBAC role). `IsAuthFailure` includes the 401/403 case, so RBAC
   failures now return 401 with the actionable message instead of a generic 500.
3. Minor: `GET|HEAD /` now returns `200 "AFClaude is running."` — the claude CLI
   probes the base URL at startup and previously got a 404 (harmless but noisy).

Exit criteria — verified: 22 unit tests pass (9 new: error classification incl. a
faked `ClientResultException(401/403)`, plus `CachingTokenCredential` single-flight
and near-expiry refresh); live smoke on the dev box confirms `launch` fails fast at
warm-up with the clean classified message before `claude` spawns, `GET|HEAD /`
return 200, and `/v1/messages` still returns the classified 401. **Stage 6c
(launch tool-use through a real deployment) still needs a re-run on v0.2.1** — the
fix directly targets its failure mode but hasn't been re-tested against the real
org yet.

## Phase 8.1 — Stage 6c investigation: bridge exonerated, trace mode — DONE

The v0.2.1 Framework re-run fixed the auth timeout (6a/6b PASS, token pre-warm cut
6b from 18s to 5.5s) but 6c failed **differently**: HTTP 200 everywhere, exit 0,
and plausible-but-wrong output — three distinct fabrications across three runs,
the probe file never actually read. Silent wrongness, worse than the loud 401 it
replaced.

**Local reproduction harness** (`tools/local-e2e/`, reusable regression): drives
the REAL `claude` CLI through `launch` against a fully fake stack — `fake-az/az.cmd`
(stub `az` returning a fake token; AzureCliCredential happily parses it) + a fake
Azure OpenAI endpoint on the trusted ASP.NET dev cert (HTTPS is required — the
SDK's bearer policy refuses plain HTTP) that scripts a `Read` function call and
then echoes the tool result. Run via `tools/local-e2e/run-e2e.ps1`.

**Result: E2E PASS.** claude's genuine 119KB request (31 tools, `stream: true`,
`max_tokens` 32000) translated correctly — all 31 tools forwarded, the scripted
`tool_calls` response became a `tool_use` SSE block that real claude parsed and
executed, the `tool_result` history round-tripped, and the probe file's contents
appeared in the final answer. **The bridge is protocol-correct against the real
client; the Framework 6c failure is model behaviour** — gpt-4.1, driven by Claude
Code's Claude-tuned prompt (plus that machine's plugin/skill injections), answers
in text instead of emitting function calls, at temperature 1, non-deterministically.
Nothing bridge-side can force a model to call tools without breaking normal turns.

Shipped alongside the harness (v0.2.2):

- **Wire-level trace mode** — `AFClaude__TraceDir=<dir>` dumps, per `/v1/messages`
  call: the raw Anthropic request, the translated Azure request (real wire format
  via `ModelReaderWriter`), Azure's response, and our reply. This is how a future
  "tool use behaves oddly" report gets diagnosed from evidence (does
  `azure-response.json` contain `tool_calls`?) instead of symptoms. Tracing
  failures can never break a request. TESTING.md gained a "Stage 6c diagnosis"
  section built on it.
- `/v1/messages` now reads its body manually: malformed JSON returns a proper
  Anthropic-shaped `400 invalid_request_error` (previously the framework's default
  binding failure), and the raw body is available to tracing.
- Fixed a latent config bug: the documented `AFClaude__Launch__Port` override was
  never read (code looked only at `Launch__Port`); both now work.
- Harness gotcha worth remembering: a minimal-API handler with signature
  `Func<HttpContext, Task<IResult>>` is treated as a plain `RequestDelegate` — the
  returned `IResult` is silently never executed (200, empty body). Write the
  response explicitly in such handlers.

Follow-up options if launch mode against non-Claude models matters (not started):
try deployments with stronger agentic function-calling behaviour, and capture a
real traced exchange from an affected machine to confirm the model-side diagnosis.

## Phase 8.2 — Trailing system-role message defect — DONE (corrects 8.1's verdict)

The v0.2.2 traced re-run on Framework proved Phase 8.1's "model behaviour, not a
bridge defect" conclusion **wrong in one specific way**. The trace showed: all 32
tools translated correctly, but the Azure request ended with a **26.5KB user-role
message** containing Claude Code's SessionStart-hook skill listing — originally a
**`role: "system"` entry inside the Anthropic `messages` array** (not valid per
the public Anthropic API, but real Claude Code gateway traffic), placed *after*
the user's actual task. The bridge mapped every non-assistant role to user, so
the model's most recent user turn was boilerplate and the 100-char file-read
instruction sat buried two messages back; `azure-response.json` showed plain text,
zero `tool_calls`. Why Phase 8.1's harness missed it: the fake model keyed on the
probe marker anywhere in the payload, so it was immune to burial — and the same
trailing-system idiom WAS present in the local traces (claude sent an 11KB
`role: "system"` skills message here too), already mistranslated, silently
tolerated by the fake.

Fix (`AnthropicBridge.ToChatMessages`): `role: "system"` messages (string or
block-array content) now map to `SystemChatMessage` **in place** — position
preserved, role faithful; OpenAI accepts system messages anywhere in the list and
models treat them as context rather than "the latest user request". Two
regression tests pin the exact real-world shape (24 total). Verified live: the
local E2E still passes and its trace now shows `system, user, system` ordering
with the task as the final user turn.

Moral for the record: "protocol-correct" (valid JSON, right schema, tools intact)
is not the same as "semantically faithful" — message-role translation shapes what
the model attends to.

## Phase 9 — Claude-on-Foundry native passthrough + API auto-detection — DONE

**Trigger (Framework, real org):** a `claude-sonnet-4-6` deployment was unreachable
through AFClaude — the Azure-OpenAI route returned `404 api_not_supported`.
Confirmed by direct calls bypassing AFClaude: **Claude models on Foundry are served
on a native Anthropic Messages endpoint, `{endpoint}/anthropic/v1/messages`**,
which 400s "anthropic-version: header is required" until that header is supplied,
then answers with genuine Anthropic-shaped responses. A completely separate API
surface from chat-completions.

Implementation (user direction: prefer auto-detection with passthrough to the
matched API; keep the OpenAI path for future OpenAI-only hosts like Ollama):

- `Foundry__Api` = `auto` (default) | `anthropic` | `openai`. Auto probes once per
  process — a 1-token request to the Anthropic route (2xx → Anthropic; 404/400 →
  OpenAI; 401/403 **throws** so auth problems surface via `FoundryErrors` instead
  of silently mis-detecting). Result cached; faulted probes retry on next request.
- **Anthropic mode is a byte-faithful passthrough** (`FoundryAnthropicClient`):
  `/v1/messages` forwards the raw body to `{endpoint}/anthropic/v1/messages` with
  an Entra bearer token and `anthropic-version` (client's value forwarded, default
  `2023-06-01`; `anthropic-beta` forwarded too). The only body mutation: `model`
  is rewritten to the configured deployment (single-deployment proxy; Claude Code
  sends its own aliases for background traffic). SSE streams relay chunk-by-chunk —
  **real incremental streaming**, no coalescing. Upstream errors pass through
  verbatim (already Anthropic-shaped). `/v1/messages/count_tokens` is proxied in
  this mode (stays 404 in OpenAI mode as before). Trace mode captures both
  directions (SSE teed to `*.sse.txt`).
- OpenAI mode is unchanged (the Phase 7 bridge); `/v1/chat/completions` under an
  Anthropic deployment returns a clear `501` pointing at `/v1/messages` (reverse
  OpenAI→Anthropic translation deliberately not built).
- MCP `ask_foundry` and `launch` are API-aware; `launch` logs which mode was
  detected after the token warm-up.
- Tests: 36 total (12 new — resolver caching/fault-retry/explicit-override,
  forward URL/auth/version/beta headers, model rewrite, probe status mapping incl.
  the 401/403-throws rule, Ask text extraction). Local E2E harness now runs BOTH
  legs against the real `claude` CLI — the fake Foundry serves both surfaces, and
  the anthropic leg runs with `Foundry__Api` unset to exercise real auto-detection:
  both legs PASS (probe value round-tripped; passthrough leg streamed SSE).

Still unverified against real Azure: the passthrough with a real Claude deployment
(qhub-sweden `claude-sonnet-4-6`) — TESTING.md Stage 7.

## Phase 9.1 — Stage 7 findings: strip `anthropic-beta` by default — DONE

Real Stage 7 run (Framework, v0.3.0):

- **7a PASS** — direct `/v1/messages` call reached the real `claude-sonnet-4-6`
  through auto-detection + passthrough ("SONNET OK", genuine `msg_...` id/usage).
- **7b FAIL (new defect, fixed here)** — detection correct, but Foundry 400'd:
  `Unexpected value(s) 'advisor-tool-2026-03-01' for the 'anthropic-beta' header`.
  Claude Code sends opt-in beta flags when it believes it's talking to genuine
  Anthropic infrastructure (triggered by the claude-* model name); **Foundry
  hard-rejects unrecognised beta values instead of ignoring them** like the real
  Anthropic API. Byte-faithful forwarding of that one header is therefore exactly
  wrong. Fix: `Foundry__AnthropicBeta` = `strip` (default — beta features degrade
  gracefully when the server doesn't advertise them) | `passthrough` | literal
  replacement list. The strip is logged (once per request) so it's visible in
  diagnosis. The fake Foundry in `tools/local-e2e` now mirrors the hard-reject, so
  the E2E anthropic leg fails if the default ever regresses — both legs re-PASS
  with the real `claude` CLI (which does send the advisor flag). 38 tests.
- **7c FAIL (confirmed model limit, no code change)** — gpt-4.1 through the bridge,
  with the Phase 8.2 ordering fix live and a clean 200, still fabricated instead of
  calling Read. The bridge is correct; **gpt-4.1 is simply not reliable as a Claude
  Code backend**. README now says so and points bridge users with Claude
  deployments at the passthrough.

## Phase 9.2 — Strict body filtering for the passthrough — DONE

The v0.3.1 Stage 7b re-run got past the header rejection (strip confirmed working
— all 8 beta flags dropped) and hit the same problem one layer deeper: **Foundry
also rejects beta-gated top-level BODY fields** — `400 context_management: Extra
inputs are not permitted`. Claude Code sends the field because it assumes the
corresponding beta feature exists; Foundry's schema validation is strict where
the real Anthropic API is tolerant.

Fix mirrors the header policy, as an allowlist rather than a blocklist (a
blocklist re-breaks on every new claude feature): `Foundry__AnthropicBody` =
`strict` (default — keeps only the standard Messages API top-level fields:
model/messages/max_tokens/system/metadata/stop_sequences/stream/temperature/
top_k/top_p/tools/tool_choice/thinking/service_tier; drops and logs everything
else) | `passthrough`. Applied in `PrepareBody` alongside the model rewrite, so
`count_tokens` gets the same treatment. The fake Foundry now mimics the strict
schema for ANY non-standard key, so the E2E anthropic leg catches future claude
body fields automatically — both legs re-PASS with the real `claude` CLI.
40 tests.

## Phase 10 — Polish (remaining)

- Real incremental streaming for the **bridge** path (`/v1/chat/completions` and
  OpenAI-mode `/v1/messages`) — the passthrough path already streams natively;
  bridge deltas must distinguish text vs. tool-call-in-progress
- Error surface parity across all three surfaces (HTTP OpenAI, HTTP Anthropic, MCP —
  same underlying failures, surface-appropriate presentation)
- Reverse bridge (OpenAI-shaped `/v1/chat/completions` → native Anthropic
  deployment), if a real need appears
- Other OpenAI-only hosts (e.g. Ollama) via `Foundry__Api=openai` + non-Azure
  auth — needs an auth-mode option (the current path always uses `AzureCliCredential`)

## Explicitly out of scope for now

- Multi-deployment / multi-model routing (single `Foundry:Deployment` only)
- API-key auth path (Entra/`az` only, per the original ask)
