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
| `Foundry__AnthropicBody` | `strict` (default)                              | Request-body policy for the passthrough — the body-level twin of the header policy. Foundry also **400s on beta-gated top-level body fields** (observed live: `context_management: Extra inputs are not permitted`), so `strict` keeps only the standard Anthropic Messages API fields and logs what it drops. It also **learns from Foundry's own rejections**: on a validation 400 naming an unsupported tool type (observed live: `tools.190: Input tag 'advisor_20260301' ... does not match any of the expected tags: ...`) or a top-level field (`<field>: Extra inputs are not permitted`), it strips just that, retries, and applies the lesson up front for the rest of the process — so only the first affected request pays one extra round trip, and a tool type Foundry starts supporting later is never stripped needlessly. `passthrough` forwards the body untouched. |
| `Foundry__CliTimeoutSeconds` | `60` (default)                              | How long to wait for the `az` CLI to produce a token. The Azure SDK default (13s) is too short for a cold `az` start on slow or loaded machines (14–24s observed) — AFClaude defaults to 60; raise it if you still see token-timeout errors. |
| `Foundry__MaxTokensParam` | `auto` (default)                            | Which token-limit field the bridge (OpenAI-compatible deployments only) sends. There's no reliable way to know in advance which a given deployment needs — it depends on the deployment, not just the model family, and shifts as new model generations ship. `auto` starts optimistic with the legacy `max_tokens` field and self-heals the first time a request is rejected with `Unsupported parameter: 'max_tokens' ... Use 'max_completion_tokens' instead`: it retries once with the modern field, caches the answer for the rest of the process, and patches the backing saved config file (if one exists) so future runs skip the retry. `legacy`/`new` pin the field explicitly and skip self-healing. |
| `AFClaude__TraceDir`  | *(unset)*                                          | Opt-in wire tracing for `/v1/messages`: dumps each request's raw Anthropic body, translated Azure request, Azure response, and the reply to numbered files in this directory. For diagnosing translation/model issues. **Traces contain full conversation content** — use a private directory and delete afterwards. |

### Interactive setup and saved config (`launch` / `--http` only)

If `Foundry__Endpoint`/`Foundry__Deployment` aren't set (and no saved config file is
found — see below), `launch` and `--http` mode drop into an interactive picker
(`az account list` → `az cognitiveservices account list` → `az cognitiveservices
account deployment list`) instead of failing fast, as long as a real terminal is
attached (it never triggers under a redirected stdin/stdout, and never in the default
MCP stdio mode — Claude launches that one with no operator present). After picking a
deployment it:

1. Probes which API surface the deployment answers on (same logic as `Foundry__Api=auto`)
2. For OpenAI-compatible deployments, checks `max_tokens` vs `max_completion_tokens` (see `Foundry__MaxTokensParam`)
3. **Offers to configure model role aliases** — maps Claude Code role names (`Sonnet`, `Haiku`, `Opus`, `Fable`) to specific deployments on the same resource. Auto-suggests by name pattern (e.g. `claude-sonnet-5` → Sonnet role, `claude-opus-5` → Opus role, picking the lexicographically highest match so newer versions win). Each role can be skipped individually. When saved, `launch` injects the corresponding `ANTHROPIC_DEFAULT_*_MODEL` env vars automatically before starting `claude`, so in-session `/model` switching and background-task model selection work without any manual env-var management.
4. Offers to save the result to a config file

| Flag | Effect |
|---|---|
| `--select` / `--configure` | Force the interactive picker to run now, regardless of env vars or an existing saved config file. |
| `--config <file>` | Load Foundry config from `<file>` instead of the default `afclaude.config.json` in the current directory. Fails fast with `Missing Config <file>` if it doesn't exist. Also used as the suggested save-target filename when the picker runs. |

Saved config files are plain JSON:

```json
{
  "Endpoint": "https://<resource>.cognitiveservices.azure.com/",
  "Deployment": "claude-sonnet-5",
  "Api": "anthropic",
  "MaxTokensParam": "auto",
  "ModelRoles": {
    "Sonnet": "claude-sonnet-5",
    "Haiku":  "claude-haiku-4-5",
    "Opus":   "claude-opus-5",
    "Fable":  "claude-fable-5-1"
  }
}
```

`MaxTokensParam` and `ModelRoles` are both optional — older saved files without them
load fine and behave as before. An explicit `legacy` or `new` for `MaxTokensParam`
pins the field and disables self-healing. Keep multiple config files (one per
deployment or model set) and switch between them with `--config <file>`.

Env vars always take priority over a saved config file for any key they set.
Explicit `ANTHROPIC_DEFAULT_*_MODEL` env vars in the caller's environment always
override those injected from `ModelRoles`.

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

This starts the Anthropic-compatible HTTP host on an OS-assigned local port (override
via `AFClaude__Launch__Port`), then execs `claude` with:
- `ANTHROPIC_BASE_URL` pointed at the local proxy
- `ANTHROPIC_MODEL` set to the configured Foundry deployment
- `ANTHROPIC_DEFAULT_SONNET_MODEL` / `ANTHROPIC_DEFAULT_HAIKU_MODEL` / `ANTHROPIC_DEFAULT_OPUS_MODEL` / `ANTHROPIC_DEFAULT_FABLE_MODEL` — set automatically from the saved config's `ModelRoles` (see [interactive setup](#interactive-setup-and-saved-config) below)

When `claude` exits, AFClaude stops the proxy and exits with `claude`'s exit code.

**All arguments after `launch` are forwarded to `claude` verbatim**, so every
claude option works without AFClaude needing to know about it:

```powershell
dnx AFClaude -y -- launch --continue                      # resume most recent session
dnx AFClaude -y -- launch --resume <session-id>           # resume a specific session
dnx AFClaude -y -- launch --worktree feature-x            # run in a git worktree
dnx AFClaude -y -- launch -p "summarize this repo"        # non-interactive print mode
dnx AFClaude -y -- launch --dangerously-skip-permissions
```

One convenience alias is translated (exact whole-argument match only):
`--yolo` → `--dangerously-skip-permissions`. Note claude requires a one-time
interactive acceptance of bypass-permissions mode before it takes effect —
in `-p` (print) mode on a machine that has never accepted it, prefer
`--allowedTools`.

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
> (e.g. web search) have no function-calling counterpart and are skipped, and
> non-text content blocks (images, thinking) are dropped. Streaming is real and
> incremental on both paths as of v0.4.0.
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

### Multi-model configuration and context windows

Claude Code uses **model aliases** (`sonnet`, `haiku`, `opus`) that on Foundry resolve to
specific deployments — and the Foundry defaults resolve to **older models with only a 200K
context window**, causing earlier compaction than you'd see on the direct Anthropic API.

**Pin your deployments explicitly** to avoid this:

```powershell
# PowerShell — set before calling AFClaude launch
$env:ANTHROPIC_DEFAULT_SONNET_MODEL = "claude-sonnet-5"   # 1M context
$env:ANTHROPIC_DEFAULT_HAIKU_MODEL  = "claude-haiku-4-5"  # 200K (fast/background)
$env:ANTHROPIC_DEFAULT_OPUS_MODEL   = "claude-opus-4-8"   # 1M context (deploy first if missing)
```

```cmd
:: cmd.exe equivalent
set ANTHROPIC_DEFAULT_SONNET_MODEL=claude-sonnet-5
set ANTHROPIC_DEFAULT_HAIKU_MODEL=claude-haiku-4-5
set ANTHROPIC_DEFAULT_OPUS_MODEL=claude-opus-4-8
```

These env vars are read by the `claude` binary itself (not by AFClaude), so they work
with both `launch` mode and direct Foundry usage. The deployment names must exactly match
what you have in your Foundry resource — full deployment IDs, not model family aliases.

> **Without these settings**, Claude Code's built-in Foundry defaults resolve `sonnet` →
> claude-sonnet-4.5 and `opus` → claude-opus-4.6 (both 200K context), which compacts far
> earlier than necessary. Models with native 1M context on Foundry: `claude-sonnet-5`,
> `claude-opus-4-7`, `claude-opus-4-8`, `claude-opus-5`.

You can also override the compaction threshold directly:

```powershell
$env:CLAUDE_CODE_AUTO_COMPACT_WINDOW = "900000"   # compact at 900K instead of 200K
```

#### In-session model switching

Claude Code supports `/model <name>` and `--model <name>` at launch. On Foundry, use the
**full deployment name** (not a bare alias):

```
/model claude-sonnet-5
/model claude-opus-4-8
```

`opusplan` (Opus for planning, then Sonnet for execution) works when both
`ANTHROPIC_DEFAULT_OPUS_MODEL` and `ANTHROPIC_DEFAULT_SONNET_MODEL` are set and both
deployments exist.

#### Fallback chains

```powershell
# Launch claude with an automatic fallback model if the primary is overloaded
dnx AFClaude -y -- launch --fallback-model claude-sonnet-5,claude-haiku-4-5
```

> **AFClaude's current limitation:** AFClaude is configured with a **single
> `Foundry__Deployment`** per run. All `/model` switches within a session rewrite
> the `model` field in the request body, and AFClaude passes it through unchanged —
> so the Foundry endpoint must support all the model names Claude Code sends, either
> directly (one deployment per model) or via a **Foundry Model Router** deployment
> (which routes across multiple Claude models from a single endpoint). A future
> AFClaude multi-deployment router is tracked in PLAN.md.

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

**The project's core goal is verified end to end**: real Claude Code, routed
through AFClaude's native passthrough, driving a genuine Claude model on Azure AI
Foundry over Entra auth — tool calls executing correctly, streaming incrementally,
no API keys anywhere.

**v0.7.0** — all phases through 13 done and verified: scaffold, HTTP proxy, MCP
stdio server, `dnx` packaging, a **live** NuGet Trusted Publishing pipeline,
classified auth-error surfaces, full Anthropic↔OpenAI tool-use bridging for GPT
deployments, the auto-detected native Anthropic passthrough for Claude deployments
(with Foundry-compatibility filtering of `anthropic-beta` flags and beta-gated body
fields), interactive Azure Foundry deployment picker, self-healing
`max_tokens`/`max_completion_tokens` detection, and adaptive Foundry rejection
learning (drops unsupported tool types / body fields on first rejection and retries,
so future Foundry updates are picked up automatically). Verified against real Foundry
deployments of both kinds. See [PLAN.md](PLAN.md) for the full build history.
