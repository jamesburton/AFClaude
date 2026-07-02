# AFClaude

A local .NET 10 process that lets Claude (Claude Code / Claude Desktop) call a model
hosted on **Azure AI Foundry**, authenticated via `az login` (Entra ID / `AzureCliCredential`),
with no API keys on disk.

It wraps the Foundry model with **Microsoft Agent Framework** (`ChatClientAgent` over
`IChatClient`) and exposes it three ways:

1. **MCP stdio server** (default) — the way Claude Code/Desktop actually consumes local
   tools. Claude launches the process, talks JSON-RPC over stdio, and calls a tool
   (e.g. `ask_foundry`) that forwards the prompt to the Foundry deployment.
2. **`launch` mode** — starts an Anthropic Messages API-compatible endpoint
   (`POST /v1/messages`) and execs `claude` itself pointed at it via
   `ANTHROPIC_BASE_URL`, so Claude Code's *own* traffic runs against the Foundry
   model. Tool use (Read/Edit/Bash/etc.) is bridged to Azure OpenAI
   function-calling; see
   [Running claude against Foundry](#running-claude-against-foundry-launch).
3. **OpenAI-compatible HTTP proxy** (`--http`) — `POST /v1/chat/completions` and
   `GET /v1/models`, for any other OpenAI-compatible client that wants to point at the
   same Foundry deployment over `http://127.0.0.1:<port>/v1`.

> **Why not just one HTTP mode?** Claude Desktop/Code's MCP integration expects a
> **stdio MCP server**, not an HTTP endpoint at all. And when Claude Code *is* pointed
> at a custom endpoint via `ANTHROPIC_BASE_URL`, it only speaks the **Anthropic
> Messages API** wire format (`/v1/messages`) — never OpenAI's `/v1/chat/completions`.
> So the three surfaces serve three distinct consumers: Claude via MCP (default), Claude
> Code's own model traffic via `/v1/messages` (`launch`), and everything else that
> already speaks OpenAI's HTTP API (`--http`).

See [PLAN.md](PLAN.md) for the build plan, open decisions, and current status.

## Architecture

```
Claude Code / Claude Desktop
        │  JSON-RPC over stdio (MCP)
        ▼
  AFClaude (this repo, .NET 10)
    ChatClientAgent (Microsoft.Agents.AI)
        │  IChatClient
        ▼
  AzureOpenAIClient (Azure.AI.OpenAI)
        │  Entra ID token (AzureCliCredential, scope https://ai.azure.com/.default)
        ▼
  Azure AI Foundry model deployment
```

The HTTP proxy mode swaps the top of the stack for a Kestrel endpoint instead of an
MCP stdio transport, reusing the same `ChatClientAgent`/`IChatClient` underneath.

## Prerequisites

- .NET 10 SDK
- Azure CLI, logged in with access to the target Foundry resource: `az login`
- An Azure AI Foundry (or Azure OpenAI) resource with a model deployment
- **A data-plane RBAC role on that resource** — grant the account the
  **Cognitive Services OpenAI User** role. Control-plane roles (even subscription
  **Owner**) do **not** grant inference access under Entra auth; without the
  data-plane role every request is rejected as unauthorized. Allow a minute or two
  for a fresh role assignment to propagate.

## Configuration

Set via environment variables (or `appsettings.json` / `dotnet user-secrets` locally):

| Variable              | Example                                            | Notes |
|-----------------------|-----------------------------------------------------|-------|
| `Foundry__Endpoint`   | `https://<resource>.cognitiveservices.azure.com/`  | Use whatever `az cognitiveservices account list` reports as `properties.endpoint`. The `.cognitiveservices.azure.com` shape is verified live against a real AIServices/Foundry resource; `.openai.azure.com` resource endpoints should work identically. |
| `Foundry__Deployment` | `gpt-4o-mini`                                       | Deployment name, not the base model name. |
| `Foundry__Api`        | `auto` (default)                                   | Which API surface serves the deployment. `auto` probes once and prefers the **native Anthropic passthrough** (Claude deployments on Foundry live at `{endpoint}/anthropic/v1/messages`, not the Azure-OpenAI route); `anthropic` / `openai` skip detection. The OpenAI path is retained for OpenAI-compatible deployments and, in future, other OpenAI-only hosts (e.g. Ollama) — that broader use is untested so far. |
| `Foundry__AnthropicBeta` | `strip` (default)                               | `anthropic-beta` header policy for the passthrough. Claude Code sends opt-in feature flags assuming real Anthropic infrastructure, but Foundry **hard-rejects unknown beta values with a 400** (observed live with `advisor-tool-2026-03-01`) — so the default strips them; features degrade gracefully. `passthrough` forwards the client's flags; any other value is sent as a literal replacement list. |
| `Foundry__AnthropicBody` | `strict` (default)                              | Request-body policy for the passthrough — the body-level twin of the header policy. Foundry also **400s on beta-gated top-level body fields** (observed live: `context_management: Extra inputs are not permitted`), so `strict` keeps only the standard Anthropic Messages API fields and logs what it drops. `passthrough` forwards the body untouched. |
| `Foundry__CliTimeoutSeconds` | `60` (default)                              | How long to wait for the `az` CLI to produce a token. The Azure SDK default (13s) is too short for a cold `az` start on slow or loaded machines (14–24s observed) — AFClaude defaults to 60; raise it if you still see token-timeout errors. |
| `AFClaude__TraceDir`  | *(unset)*                                          | Opt-in wire tracing for `/v1/messages`: dumps each request's raw Anthropic body, translated Azure request, Azure response, and the reply to numbered files in this directory. For diagnosing translation/model issues. **Traces contain full conversation content** — use a private directory and delete afterwards. |

No API keys are configured — auth is entirely via `AzureCliCredential` (falls back to
other `DefaultAzureCredential` sources if you later want that instead).

## Running locally

Default mode is the MCP stdio server (what Claude actually launches):

```powershell
az login
$env:Foundry__Endpoint = "https://<resource>.openai.azure.com/"
$env:Foundry__Deployment = "<deployment-name>"
dotnet run
```

For the HTTP proxy instead, add `--http` (see [below](#other-openai-compatible-clients-http-proxy-secondary)).

> One current caveat: `AFClaude.csproj` pins `Azure.AI.OpenAI` to a `2.9.0-beta.1`
> prerelease. The latest *stable* release (2.1.0) is binary-incompatible with the
> `OpenAI` package version pulled in transitively by `Microsoft.Agents.AI.OpenAI`
> (throws `MissingMethodException` on the first real chat call) — see PLAN.md decision
> 7. Revisit when a compatible stable `Azure.AI.OpenAI` ships.

## Integrating with Claude

### Claude Code / Claude Desktop (MCP, primary)

`dnx` resolves `AFClaude` from NuGet like `npx` resolves an npm package — once a
version is published (see [Publishing](#publishing-maintainers)), no local build step
is required:

```powershell
dnx AFClaude -y
```

Before a version is published (or while iterating locally), pack it and point `dnx`
at a local feed instead:

```powershell
dotnet pack src/AFClaude/AFClaude.csproj -c Release -o local-feed
dnx AFClaude -y --add-source ./local-feed
```

Register it as an MCP server (e.g. in Claude Code's `.mcp.json`):

```json
{
  "mcpServers": {
    "afclaude": {
      "command": "dnx",
      "args": ["AFClaude", "--yes"],
      "env": {
        "Foundry__Endpoint": "https://<resource>.openai.azure.com/",
        "Foundry__Deployment": "<deployment-name>"
      }
    }
  }
}
```

Claude then sees a single tool, `ask_foundry` (one required `prompt` string), that it
can call mid-conversation to delegate a prompt to the Foundry model. `az login` must
have been run in advance, in the same user/environment context `dnx` will inherit.

### Running `claude` against Foundry (`launch`)

```powershell
dnx AFClaude -y -- launch
```

This starts the Anthropic-compatible HTTP host on `http://127.0.0.1:31337` (override
via `AFClaude__Launch__Port`), then execs `claude` with `ANTHROPIC_BASE_URL` pointed
at it and `ANTHROPIC_MODEL` set to the configured Foundry deployment. Any arguments
after `launch` are forwarded straight to `claude` — e.g.
`dnx AFClaude -y -- launch --dangerously-skip-permissions`. When `claude` exits,
AFClaude stops the proxy and exits with `claude`'s exit code.

**Claude deployments (native passthrough).** Claude models on Azure AI Foundry are
served on a native Anthropic Messages endpoint (`{endpoint}/anthropic/v1/messages`),
which AFClaude auto-detects and proxies **byte-faithfully** — no translation at all,
real incremental streaming, `count_tokens` proxied too. Auth is the only added
plumbing (Entra bearer token + `anthropic-version` header). This is the best-case
mode: Claude Code talking to a real Claude model, so tool use behaves natively.

**OpenAI-compatible deployments (bridge).** For GPT-family deployments,
`/v1/messages` bridges Anthropic tool calling to Azure OpenAI function-calling in
both directions: the `tools` array becomes function-tool definitions,
`tool_use`/`tool_result` history becomes assistant tool calls and tool-role
messages, and the model's function calls come back as `tool_use` content blocks
with `stop_reason: "tool_use"`. `max_tokens`, `temperature`, `top_p`, and
`stop_sequences` pass through as well.

> **Bridge-mode limitations** (OpenAI-compatible deployments only — none of this
> applies to the native Claude passthrough). Anthropic built-in *server* tools
> (e.g. web search) have no function-calling counterpart and are skipped; non-text
> content blocks (images, thinking) are dropped; streaming responses are a single
> coalesced SSE burst rather than true incremental token streaming.
>
> **In bridge mode, the deployed model decides how useful launch mode is.** Both
> paths are verified end-to-end against the real `claude` client
> (`tools/local-e2e/run-e2e.ps1`), but Claude Code's prompts are tuned for Claude —
> a non-Claude model may answer in plain text (or fabricate output) instead of
> calling tools, which looks like "it read the file" while it never did.
> **Confirmed live: gpt-4.1 does this even with correct message translation** — it
> is not reliable as a Claude Code backend. If your Foundry org has a Claude
> deployment, prefer it (the passthrough makes it behave natively). If tool turns
> behave oddly, set `AFClaude__TraceDir` and check whether the model's responses
> actually contain `tool_calls` (see TESTING.md, "Stage 6c diagnosis"). See [PLAN.md](PLAN.md) Phase 8 for what's left.

### Other OpenAI-compatible clients (HTTP proxy, secondary)

Run AFClaude in HTTP mode and point any OpenAI-compatible client at:

```
http://127.0.0.1:5277/v1
```

This does **not** register with Claude directly — it's for tooling that already speaks
the OpenAI HTTP API and needs a local, key-free route to a Foundry deployment. HTTP
mode is opt-in: pass `--http` or set `AFClaude__Mode=http` (default mode is the MCP
stdio server above).

## Publishing (maintainers)

`.github/workflows/publish.yml` builds, packs, and pushes `AFClaude` to nuget.org on
any `v*.*.*` tag push (or via manual `workflow_dispatch`), using **NuGet Trusted
Publishing** — no long-lived NuGet API key is stored in the repo. This requires a
one-time policy on nuget.org, configured with:

| Field             | Value         |
|--------------------|--------------|
| Repository Owner  | `jamesburton` |
| Repository        | `AFClaude`    |
| Workflow File     | `publish.yml` |
| Environment       | `nuget`       |

See [PLAN.md](PLAN.md) (Phase 4) for the full one-time setup checklist, including the
matching GitHub Environment and the `NUGET_USER` repo secret the workflow needs.

## Status

Phases 1–8 done and verified: scaffold, HTTP proxy, MCP stdio server, `dnx` packaging,
a **live** NuGet Trusted Publishing pipeline, clean auth-error surfaces, the
`/v1/messages` + `launch` path, and full tool-use bridging for `/v1/messages` —
**verified against a real Azure AI Foundry deployment** (gpt-4.1, Entra auth via
`az login`): real chat completions, Anthropic-shaped text + streaming turns, a full
tool_use/tool_result round trip, MCP `ask_foundry`, and `claude -p` through `launch`
all pass. Auth failures are classified into actionable messages (az missing, session
expired, `az` CLI token timeout, missing data-plane RBAC role). See
[PLAN.md](PLAN.md) Phase 9 for what's left (true incremental streaming, error-surface
parity).
