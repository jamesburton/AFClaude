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

```powershell
az login
$env:Foundry__Endpoint = "https://<resource>.openai.azure.com/"
$env:Foundry__Deployment = "<deployment-name>"
dotnet run
```

## Integrating with Claude

### Claude Code / Claude Desktop (MCP, primary)

Package AFClaude as a .NET tool so it can be launched with `dnx` without a prior
`dotnet build`/`publish` step by the caller:

```powershell
dotnet pack -c Release
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

Claude then sees a tool (working name: `ask_foundry`) it can call mid-conversation to
delegate a prompt to the Foundry model. `az login` must have been run in advance, in the
same user/environment context `dnx` will inherit.

### Other OpenAI-compatible clients (HTTP proxy, secondary)

Run AFClaude in HTTP mode and point any OpenAI-compatible client at:

```
http://127.0.0.1:5277/v1
```

This does **not** register with Claude directly — it's for tooling that already speaks
the OpenAI HTTP API and needs a local, key-free route to a Foundry deployment.

## Status

Early stage — see [PLAN.md](PLAN.md) for the phased build plan and unresolved
decisions (streaming, tool-call passthrough, MCP SDK choice, endpoint style).
