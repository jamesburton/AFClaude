# TESTING.md — First real-Foundry verification (v0.2.0)

Instructions for an agent (Claude Code or similar) running on a machine where
`az login` has already been completed for an organisation with Azure AI Foundry
resources.

> **Run history (Framework, 2026-07-02):** v0.2.0 full plan — Stages 1–6b PASS, 6c
> FAIL on the az 13s process timeout (fixed in v0.2.1: 60s default +
> `Foundry__CliTimeoutSeconds` + token cache + launch warm-up; PLAN.md Phase 8).
> v0.2.1/v0.2.2 re-test — 6a/6b PASS, 6c FAIL differently: trace mode showed the
> bridge mapping Claude Code's trailing `role:"system"` hook message to a final
> *user* turn, burying the task (fixed in v0.2.3: system-role preserved in place;
> PLAN.md Phase 8.2). v0.3.0–v0.3.2 (Stage 7): 7a PASS; 7b hit the
> `anthropic-beta` header rejection (fixed v0.3.1, strip) then the
> `context_management` body-field rejection (fixed v0.3.2, strict body filter);
> 7c confirmed gpt-4.1 won't drive tool calls — a model limit, not a proxy bug.
> **2026-07-03: Stage 7b PASSED on v0.3.2** — real Claude Code → AFClaude
> passthrough → genuine claude-sonnet-4-6 on Foundry, tool call executed,
> correct output. (One false FAIL on the way: the probe file must be inside the
> launch working directory — Claude Code's own permission gate denies
> out-of-scope reads in `-p` mode; commands below updated accordingly.)
> The account needs the "Cognitive Services OpenAI User" role on the resource —
> already granted on qhub-infra-resource during the first run.

**Rules for the agent executing this:**

- Run stages **in order**; each has an explicit PASS criterion. Don't skip ahead on
  failure — stop, check Troubleshooting, and record the failure verbatim.
- All commands are **PowerShell** (Windows). Nothing here should require
  interactive input. **Never run `launch` without `--version` or `-p <prompt>`** —
  bare `launch` starts a live interactive nested Claude Code session.
- Fill in the **Report** section at the end and return it. Several open design
  questions are settled by what you observe — record exactly what you see.

## Stage 0 — Prerequisites and discovery

```powershell
az account show --query "{sub:name, tenant:tenantId, user:user.name}" -o json
dotnet --version          # need 10.x
claude --version          # need Claude Code on PATH (only for Stages 5-6)
```

Discover the Foundry resource and a deployment (any chat model that supports
function calling, e.g. gpt-4o / gpt-4o-mini / gpt-4.1):

```powershell
az cognitiveservices account list --query "[].{name:name, rg:resourceGroup, kind:kind, endpoint:properties.endpoint}" -o table
az cognitiveservices account deployment list -n <ACCOUNT_NAME> -g <RESOURCE_GROUP> --query "[].{deployment:name, model:properties.model.name}" -o table
```

Set the config every later stage uses (**adjust to what you found**):

```powershell
$env:Foundry__Endpoint   = "https://<resource>.openai.azure.com/"   # use the endpoint az reported
$env:Foundry__Deployment = "<deployment-name>"                       # deployment name, NOT base model name
```

> **Record for the report:** the exact endpoint URL shape az reported —
> `https://<resource>.openai.azure.com/` vs `https://<name>.cognitiveservices.azure.com/`
> vs `https://<project>.services.ai.azure.com/`. Which shape works is an open
> design question (PLAN.md decision 2). Start with whatever
> `properties.endpoint` says.

PASS: `az account show` returns the expected org tenant; a deployment is identified.

## Stage 1 — Zero-install + fail-fast (no config)

```powershell
$env:Foundry__Endpoint = ""; $env:Foundry__Deployment = ""
dnx AFClaude@0.3.2 -y
```

PASS: downloads from nuget.org (first run may take ~a minute) and exits with an
`InvalidOperationException` naming `Foundry:Endpoint` / the `Foundry__Endpoint`
env var. No hang, no stack-free silent exit.
(Then re-set the two env vars from Stage 0 before continuing.)

## Stage 2 — HTTP proxy: first real Azure round trip

Start the proxy on a fixed port (background job), then hit it:

```powershell
$env:ASPNETCORE_URLS = "http://127.0.0.1:31399"
$proxy = Start-Job { dnx AFClaude@0.3.2 -y -- --http }
Start-Sleep -Seconds 20   # first run resolves the tool; check Receive-Job $proxy if unsure

Invoke-RestMethod http://127.0.0.1:31399/v1/models | ConvertTo-Json -Depth 5

Invoke-RestMethod -Method Post -Uri http://127.0.0.1:31399/v1/chat/completions -ContentType 'application/json' -Body (@{
  model = $env:Foundry__Deployment
  messages = @(@{ role = "user"; content = "Reply with exactly: FOUNDRY OK" })
} | ConvertTo-Json -Depth 5) | ConvertTo-Json -Depth 6
```

PASS: `/v1/models` lists the deployment; `/v1/chat/completions` returns a real
model reply containing `FOUNDRY OK`. **This is the first-ever real Foundry round
trip — it simultaneously validates the endpoint shape, Entra auth, and the
`Azure.AI.OpenAI 2.9.0-beta.1` package pin** (a pin regression shows up here as a
`MissingMethodException`-derived 500, not a compile error).

Keep the proxy job running for Stages 3–4.

## Stage 3 — /v1/messages: Anthropic-shaped text turn

```powershell
$r = Invoke-RestMethod -Method Post -Uri http://127.0.0.1:31399/v1/messages -ContentType 'application/json' -Body (@{
  model = $env:Foundry__Deployment; max_tokens = 100
  messages = @(@{ role = "user"; content = "Reply with exactly: MESSAGES OK" })
} | ConvertTo-Json -Depth 5)
$r | ConvertTo-Json -Depth 6
```

PASS: `type: "message"`, one `content` block of `type: "text"` containing
`MESSAGES OK`, `stop_reason: "end_turn"`, and **non-zero**
`usage.input_tokens`/`usage.output_tokens`.

Also verify streaming returns an SSE burst (expect `event: message_start` …
`event: message_stop` lines):

```powershell
Invoke-WebRequest -Method Post -Uri http://127.0.0.1:31399/v1/messages -ContentType 'application/json' -Body (@{
  model = $env:Foundry__Deployment; max_tokens = 100; stream = $true
  messages = @(@{ role = "user"; content = "Say hi" })
} | ConvertTo-Json -Depth 5) | Select-Object -ExpandProperty Content
```

## Stage 4 — /v1/messages: tool-use bridging (the new feature)

**4a — model emits a tool call:**

```powershell
$req = @{
  model = $env:Foundry__Deployment; max_tokens = 300
  tools = @(@{
    name = "get_weather"; description = "Get current weather for a city"
    input_schema = @{ type = "object"; properties = @{ city = @{ type = "string" } }; required = @("city") }
  })
  messages = @(@{ role = "user"; content = "What's the weather in London? You MUST use the get_weather tool." })
}
$r = Invoke-RestMethod -Method Post -Uri http://127.0.0.1:31399/v1/messages -ContentType 'application/json' -Body ($req | ConvertTo-Json -Depth 8)
$r | ConvertTo-Json -Depth 8
```

PASS: `stop_reason: "tool_use"` and a content block
`{ type: "tool_use", id: <id>, name: "get_weather", input: { city: "London" } }`.
Record the `id`.

**4b — round the result back (tests tool_use/tool_result history translation):**

```powershell
$toolUse = $r.content | Where-Object type -eq 'tool_use' | Select-Object -First 1
$req.messages += @{ role = "assistant"; content = @($r.content) }
$req.messages += @{ role = "user"; content = @(@{
  type = "tool_result"; tool_use_id = $toolUse.id
  content = @(@{ type = "text"; text = "22C and sunny" })
}) }
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:31399/v1/messages -ContentType 'application/json' -Body ($req | ConvertTo-Json -Depth 10) | ConvertTo-Json -Depth 8
```

PASS: a normal text answer mentioning ~22 / sunny, `stop_reason: "end_turn"` —
proving the assistant `tool_use` + user `tool_result` history was accepted by
Azure (no 400/500).

Stop the proxy: `Stop-Job $proxy; Remove-Job $proxy`.

## Stage 5 — MCP surface (`ask_foundry`)

```powershell
claude mcp add afclaude --env Foundry__Endpoint=$env:Foundry__Endpoint --env Foundry__Deployment=$env:Foundry__Deployment -- dnx AFClaude@0.3.2 -y
claude -p "Use the ask_foundry tool to ask: 'Reply with exactly MCP OK'. Report the tool's response verbatim." --allowedTools "mcp__afclaude__ask_foundry"
claude mcp remove afclaude   # cleanup
```

PASS: the printed answer contains `MCP OK` (i.e. Claude called the MCP tool and
the tool reached Foundry). If `claude mcp add` syntax differs on this version,
`claude mcp add --help` — the equivalent `.mcp.json` shape is in README.md.

## Stage 6 — `launch` mode: Claude Code itself on Foundry

**6a — pipeline smoke (safe, non-interactive):**

```powershell
dnx AFClaude@0.3.2 -y -- launch --version
```

PASS: prints `AFClaude proxy listening on http://127.0.0.1:31337 ...`, then the
claude version, exits 0. (Port busy? Set `$env:AFClaude__Launch__Port = "31401"`.)

**6b — real text turn through claude (print mode, non-interactive):**

```powershell
dnx AFClaude@0.3.2 -y -- launch -p "Reply with exactly: LAUNCH OK"
```

PASS: prints `LAUNCH OK`. **Known-unverified assumption being tested here:**
AFClaude sets a placeholder `ANTHROPIC_API_KEY=afclaude-local` — if claude
instead demands login/OAuth or rejects the key, record the exact behaviour; that
assumption is wrong and needs a fix.

**6c — real tool use through claude:**

```powershell
Set-Content -Path "$PWD\afclaude-probe.txt" -Value "PROBE-VALUE-12345"   # MUST be in the working directory — see note below
dnx AFClaude@0.3.2 -y -- launch -p "Read the file $PWD\afclaude-probe.txt and reply with only its contents."
```

> **The probe file must live inside the working directory `launch` runs from.**
> Claude Code's own file-access permission gate silently denies out-of-scope
> reads in `-p` mode (no prompt is possible) — a probe in `$env:TEMP` produces a
> false FAIL where the tool call round-trips fine but the tool itself is denied.
> Confirmed live on the v0.3.2 run: identical setup failed with a TEMP path and
> passed with a working-directory path. Delete the probe file afterwards.

PASS: prints `PROBE-VALUE-12345`. This is the end-to-end proof of Phase 7:
claude planned a tool call, the proxy bridged it to Azure function-calling and
back, and claude executed the tool and continued.

If 6b/6c fail with errors mentioning **`count_tokens`** or a 404 on
`/v1/messages/count_tokens`: that endpoint is not implemented (known gap) —
record it; it means Claude Code requires it and it must be added.

## Stage 6c diagnosis — trace mode (v0.2.2+)

Context: the v0.2.1 Stage 6c failure (HTTP 200, plausible-but-wrong text, file
never read) was diagnosed **from this trace mode's output on the v0.2.2 run**: the
bridge mapped Claude Code's trailing `role: "system"` hook-injection message (a
26.5KB skill listing placed after the real task) to a final **user** turn, so the
model responded to boilerplate instead of calling tools. **Fixed in v0.2.3** —
system-role messages inside the messages array now stay system-role in place;
regression-tested and re-verified with the local harness
(`tools/local-e2e/run-e2e.ps1`, real `claude` CLI against a fake model).
**Re-run Stage 6c on v0.2.3.** If it still misbehaves, trace mode below produces
the evidence: check whether the final message in `azure-request.json` is the
user's task (correct) and whether `azure-response.json` contains `tool_calls`.

Re-run 6c with tracing enabled:

```powershell
$env:AFClaude__TraceDir = "$env:TEMP\afclaude-trace"
dnx AFClaude@0.3.2 -y -- launch -p "Read the file $PWD\afclaude-probe.txt and reply with only its contents."
```

Then inspect `$env:TEMP\afclaude-trace` — per request `NNN`:

| File | What to check |
|---|---|
| `NNN-anthropic-request.json` | claude's raw request — `tools` array present? |
| `NNN-azure-request.json` | the translated request — `tools` should list Read/Bash/etc. If tools are missing HERE but present above, that IS a bridge bug: report it. |
| `NNN-azure-response.json` | the model's actual reply — does `tool_calls` appear? If the model returned plain `content` with no `tool_calls` for a "read this file" prompt, the model isn't driving the tools (model behaviour, not proxy). |
| `NNN-anthropic-response.json` | what we sent claude — `tool_use` blocks and `stop_reason` should mirror the Azure response. |

Report: attach (or summarise) the first exchange's four files, and state whether
`tool_calls` ever appeared in any `azure-response.json`. Trace files contain the
full conversation — treat the directory as sensitive and delete it afterwards.

## Stage 7 — Claude-on-Foundry native passthrough (v0.3.0)

Context: Claude deployments on Foundry (e.g. `claude-sonnet-4-6` on qhub-sweden)
are served on `{endpoint}/anthropic/v1/messages`, not the Azure-OpenAI route
(confirmed live: OpenAI route → `404 api_not_supported`; Anthropic route works
with an `anthropic-version` header). v0.3.0 auto-detects this and proxies
byte-faithfully — no translation, real streaming, `count_tokens` proxied.

Point at the Claude deployment (endpoint/deployment from Stage 0 discovery
against the resource that hosts it):

```powershell
$env:Foundry__Endpoint   = "https://<claude-resource-endpoint>/"
$env:Foundry__Deployment = "claude-sonnet-4-6"
# Foundry__Api unset -> auto-detection (or set "anthropic" to skip the probe)
```

**7a — direct Anthropic-shaped call through the proxy:**

```powershell
$env:ASPNETCORE_URLS = "http://127.0.0.1:31399"
$proxy = Start-Job { dnx AFClaude@0.3.2 -y -- --http }
Start-Sleep -Seconds 20
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:31399/v1/messages -ContentType 'application/json' -Body (@{
  model = $env:Foundry__Deployment; max_tokens = 100
  messages = @(@{ role = "user"; content = "Reply with exactly: SONNET OK" })
} | ConvertTo-Json -Depth 5) | ConvertTo-Json -Depth 6
Stop-Job $proxy; Remove-Job $proxy
```

PASS: a genuine Claude reply containing `SONNET OK` with a real `msg_...` id and
non-zero usage. (First call includes the one-off 1-token detection probe.)

**7b — Claude Code on a real Claude model (the goal of the whole project):**

```powershell
Set-Content -Path "$PWD\afclaude-probe.txt" -Value "PROBE-VALUE-12345"   # MUST be in the working directory — see note below
dnx AFClaude@0.3.2 -y -- launch -p "Read the file $PWD\afclaude-probe.txt and reply with only its contents."
```

PASS: stderr shows `Native Anthropic (Claude) deployment detected — /v1/messages
runs as a direct passthrough.` after the token warm-up, and the output is
`PROBE-VALUE-12345`. Record: detection line seen? Streaming felt incremental?
Any `count_tokens` calls in a trace (now proxied rather than 404)?

> v0.3.1/v0.3.2: Claude Code's beta feature flags are stripped at BOTH levels by
> default — the `anthropic-beta` header (v0.3.0 7b failure) and beta-gated body
> fields like `context_management` (v0.3.1 7b failure); Foundry 400s on both where
> the real Anthropic API ignores them. Expect two log lines: "Stripping client
> anthropic-beta ..." and "Dropping non-standard request field(s) ...". Override
> via `Foundry__AnthropicBeta` / `Foundry__AnthropicBody` if Foundry gains support.

**7c — regression check on the GPT deployment (bridge path, v0.2.3 fix):** point
`Foundry__Endpoint`/`Foundry__Deployment` back at gpt-4.1 (qhub-infra-resource)
and repeat 7b. PASS: `OpenAI-compatible deployment — /v1/messages bridges ...`
detection line, and `PROBE-VALUE-12345` printed (this validates the Phase 8.2
trailing-system-role fix against the real model).

## Troubleshooting

| Symptom | Meaning / action |
|---|---|
| `401 authentication_error`, "Azure authentication is unavailable" | `az` not on PATH or never logged in — `az login` (correct tenant: `az login --tenant <id>`). |
| `401`, "session expired" message | Re-run `az login`. **Record the exact message** — this error branch has never been observed live. |
| `404 DeploymentNotFound` / resource not found | Deployment name wrong, or endpoint shape wrong — retry Stage 2 with the alternative endpoint shape (`https://<name>.cognitiveservices.azure.com/` or `https://<project>.services.ai.azure.com/`) and record which one worked. |
| `500` mentioning `MissingMethodException` | The `Azure.AI.OpenAI` 2.9.0-beta.1 / `OpenAI` package pin has regressed — stop and report; this is a packaging bug, not a config problem. |
| Stage 4a returns plain text, no `tool_use` | The deployed model may not support function calling — try a gpt-4o/gpt-4.1-family deployment. |
| Proxy port refused | Job still starting (first `dnx` run downloads) — `Receive-Job $proxy` to see logs; increase the sleep. |

## Report (fill in and return)

```
Machine / az tenant:
Endpoint URL shape that worked:            # settles PLAN.md open decision 2
Deployment/model used:
Stage 1 fail-fast:                         PASS/FAIL
Stage 2 /v1/chat/completions (real):       PASS/FAIL
Stage 3 /v1/messages text + stream:        PASS/FAIL   usage tokens seen: in=__ out=__
Stage 4a tool_use emitted:                 PASS/FAIL
Stage 4b tool_result round trip:           PASS/FAIL
Stage 5 MCP ask_foundry:                   PASS/FAIL
Stage 6a launch --version:                 PASS/FAIL
Stage 6b launch -p text:                   PASS/FAIL   (API-key placeholder assumption held? Y/N)
Stage 6c launch -p tool use:               PASS/FAIL
count_tokens 404 seen?                     Y/N
Any expired-session auth message observed? (verbatim if so)
Anomalies / verbatim errors:
```
