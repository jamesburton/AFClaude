# AFClaude

A local .NET 10 process that lets Claude (Claude Code / Claude Desktop) call a model
hosted on **Azure AI Foundry**, authenticated via `az login` (Entra ID / `AzureCliCredential`),
with no API keys on disk.

It wraps the Foundry model with **Microsoft Agent Framework** (`ChatClientAgent` over
`IChatClient`) and exposes it two ways:

1. **MCP stdio server** (primary) — the way Claude Code/Desktop actually consumes local
   tools. Claude launches the process, talks JSON-RPC over stdio, and calls a tool
   (e.g. `ask_foundry`) that forwards the prompt to the Foundry deployment.
2. **OpenAI-compatible HTTP proxy** (secondary) — `POST /v1/chat/completions` and
   `GET /v1/models`, for any other OpenAI-compatible client that wants to point at the
   same Foundry deployment over `http://127.0.0.1:<port>/v1`.

> **Why two modes?** Claude Desktop/Code's MCP integration expects a **stdio MCP
> server**, not an arbitrary OpenAI-compatible HTTP endpoint. The HTTP proxy is useful
> for other tools but is not by itself something Claude can "point at" as a model
> backend — see [Integrating with Claude](#integrating-with-claude).

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

## Configuration

Set via environment variables (or `appsettings.json` / `dotnet user-secrets` locally):

| Variable              | Example                                            | Notes |
|-----------------------|-----------------------------------------------------|-------|
| `Foundry__Endpoint`   | `https://<resource>.openai.azure.com/`             | Azure OpenAI-style resource endpoint. If the deployment is a Foundry *project* endpoint instead, this will look like `https://<project>.services.ai.azure.com/` — confirm which one your deployment uses (see [PLAN.md](PLAN.md)). |
| `Foundry__Deployment` | `gpt-4o-mini`                                       | Deployment name, not the base model name. |

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

Phases 1–4 done and smoke-tested (scaffold, HTTP proxy, MCP stdio server, `dnx`
packaging + NuGet publish pipeline). Not yet done: an actual tag-triggered publish to
nuget.org (needs the one-time Trusted Publishing setup above), a real Foundry
round-trip (only tested against a fake endpoint so far), registering the tool in a
live Claude Code session, and Phase 5/6 hardening (error shapes, streaming). See
[PLAN.md](PLAN.md) for the full phase-by-phase detail.
