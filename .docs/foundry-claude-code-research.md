# Research: Claude Code on Azure AI Foundry

Source: https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/configure-claude-code  
Source: https://code.claude.com/docs/en/microsoft-foundry  
Source: https://code.claude.com/docs/en/model-config  
Captured: 2026-09-22

---

## 1. Reduced Context / Early Compaction on Foundry

### Root Cause

Claude Code defaults models on Foundry to older versions whose context windows are only **200K tokens**, not 1M. Per the model-config docs:

> *"Sonnet 4.6 and Opus 4.6 without extended context compact at the 200K boundary, and so do Opus 4.8 and Opus 5 when they run with a 200K context window, such as on Amazon Bedrock, Google Cloud's Agent Platform, and **Microsoft Foundry**"*

The Foundry defaults (when no `ANTHROPIC_DEFAULT_*_MODEL` env vars are set) are:

| Alias    | Resolves to on Foundry |
|----------|------------------------|
| `sonnet` | claude-sonnet-4.5      |
| `opus`   | claude-opus-4.6        |
| `haiku`  | claude-haiku-4.5       |

Both Sonnet 4.5 and Opus 4.6 run with a **200K context window on Foundry**, which causes early compaction compared to what you'd see on the direct Anthropic API.

### Solution: Pin Newer Models

Deploy and pin newer models that support the **1M context window**. The Microsoft Learn docs recommend:

```powershell
$env:ANTHROPIC_DEFAULT_SONNET_MODEL = "claude-sonnet-5"
$env:ANTHROPIC_DEFAULT_OPUS_MODEL   = "claude-opus-4-8"  # or claude-opus-5
$env:ANTHROPIC_DEFAULT_HAIKU_MODEL  = "claude-haiku-4-5"
```

The Anthropic Claude Code on Foundry docs specifically note:

> *"Without `ANTHROPIC_DEFAULT_OPUS_MODEL`, the `opus` alias on Microsoft Foundry resolves to Opus 4.6. Set it to the ID of a newer Opus model, such as Opus 4.8"*

Models with **native 1M context on Foundry** (once pinned and deployed):
- `claude-sonnet-5` — native 1M, compacts at ~967K
- `claude-opus-4-7`, `claude-opus-4-8`, `claude-opus-5` — 1M context

### Relevant AFClaude Consideration

AFClaude currently proxies the Anthropic API surface and passes `model` through as-is. The reduced context isn't caused by AFClaude — it's caused by Claude Code's alias resolution picking old Foundry defaults. However, AFClaude could **surface a warning** if it detects a model with a 200K window being used, or document the fix prominently in the README.

The `AFClaude__TraceDir` trace logs would show which model name is arriving in requests — useful for diagnosing whether the pinning is working.

---

## 2. Specifying Multiple Models for Switching within the CLI

Claude Code has built-in mechanisms for multi-model configuration:

### Environment Variables (Foundry-specific)

```powershell
# Set per-role deployment names:
$env:ANTHROPIC_DEFAULT_SONNET_MODEL = "claude-sonnet-5"    # primary coding
$env:ANTHROPIC_DEFAULT_HAIKU_MODEL  = "claude-haiku-4-5"   # fast/background tasks
$env:ANTHROPIC_DEFAULT_OPUS_MODEL   = "claude-opus-4-8"    # complex reasoning
$env:ANTHROPIC_DEFAULT_FABLE_MODEL  = "claude-fable-5"     # longest tasks (if deployed)
```

Claude Code uses these automatically for different task types internally (Haiku for background title generation, Sonnet for primary, Opus for plan mode when using `opusplan`, etc.).

### In-Session Switching

Users can switch models mid-session with:

```text
/model sonnet
/model opus
/model haiku
/model claude-opus-4-8  ← full deployment name, required on Foundry
```

On Foundry, **full deployment names are required** because Foundry uses provider-specific deployment IDs. The alias `opus` resolves via `ANTHROPIC_DEFAULT_OPUS_MODEL` env var.

### Startup Model Selection

```bash
claude --model claude-sonnet-5
claude --model claude-opus-4-8
```

### `opusplan` Alias

Special alias that **uses Opus for plan mode, then switches to Sonnet for execution**:

```text
/model opusplan
```

On Foundry with providers using deployment IDs, plan-mode substitution behaviour is limited — it stays on the session model when the upgrade model is excluded, rather than the newest-permitted-version behaviour on the Anthropic API.

### Fallback Model Chains

Configure automatic fallback when primary model is overloaded/unavailable:

```bash
claude --fallback-model sonnet,haiku
```

Or persist in settings:
```json
{
  "fallbackModel": ["claude-sonnet-5", "claude-haiku-4-5"]
}
```

**Note**: On Foundry, fallback chains use deployment names, not aliases.

### AFClaude Consideration for Multi-Model Support

AFClaude currently uses a **single Foundry endpoint** per run. To support multi-model switching:

1. **Claude Code side**: The `ANTHROPIC_DEFAULT_*_MODEL` env vars plus `/model` switching in-session cover most use cases already — this is pure Claude Code config, not requiring AFClaude changes.

2. **AFClaude side (if supporting multiple deployments)**: Users could need separate Foundry deployments for each model. The wizard currently configures one endpoint. A potential enhancement:
   - Support multiple named endpoint configs (e.g., `Foundry:Deployments:sonnet`, `Foundry:Deployments:opus`)
   - Route by the `model` field in the incoming request body
   - This would let AFClaude act as a model-aware router across Foundry deployments

3. **Current workaround**: Users can set all three `ANTHROPIC_DEFAULT_*_MODEL` env vars to deployment names they've created in Foundry, and Claude Code handles the switching. AFClaude passes `model` through to Foundry unchanged in strict/passthrough mode.

---

## 3. Key Foundry Configuration Variables (Full List)

| Variable | Purpose |
|----------|---------|
| `CLAUDE_CODE_USE_FOUNDRY` | `1` to enable Foundry mode |
| `ANTHROPIC_FOUNDRY_RESOURCE` | Azure resource name |
| `ANTHROPIC_FOUNDRY_BASE_URL` | Full base URL alternative to resource name |
| `ANTHROPIC_FOUNDRY_API_KEY` | API key auth |
| `ANTHROPIC_FOUNDRY_AUTH_TOKEN` | Bearer token auth (v2.1.203+) |
| `ANTHROPIC_DEFAULT_SONNET_MODEL` | Deployment name for Sonnet role |
| `ANTHROPIC_DEFAULT_HAIKU_MODEL` | Deployment name for Haiku role |
| `ANTHROPIC_DEFAULT_OPUS_MODEL` | Deployment name for Opus role |
| `ANTHROPIC_DEFAULT_FABLE_MODEL` | Deployment name for Fable role |
| `CLAUDE_CODE_AUTO_COMPACT_WINDOW` | Override compaction threshold (e.g., `900000`) |
| `CLAUDE_CODE_DISABLE_1M_CONTEXT` | `1` to cap all models at 200K |
| `ANTHROPIC_MAX_TOKENS` | Token limit per request |
| `ENABLE_PROMPT_CACHING_1H` | `1` for 1-hour cache TTL instead of 5-min |

---

## 4. Deploying Models in Foundry

From the MS Learn article, Claude Code uses these deployment roles:

| Role | Recommended Deployment | Purpose |
|------|------------------------|---------|
| Primary | `claude-sonnet-4-6` (or newer) | General coding |
| Fast | `claude-haiku-4-5` | Quick ops, background tasks |
| Extended thinking | `claude-opus-4-6` (or newer) | Complex reasoning |

**Key warning from Anthropic docs**:
> *"Without pinning, model aliases such as `sonnet` and `opus` resolve to Claude Code's built-in default for Microsoft Foundry, which can lag the newest release and may not yet be available in your account. Microsoft Foundry has no startup model check, so requests fail when the default is unavailable."*

---

## 5. Model Router Alternative

Foundry's **Model Router** (`2025-11-18` version) supports `claude-haiku-4-5`, `claude-sonnet-4-5`, `claude-opus-4-6`, `claude-opus-4-7`, and `claude-opus-4-8`, and automatically routes prompts to the best model. This provides a **single-endpoint multi-model experience** that AFClaude wouldn't need to handle specially — Claude Code sees one deployment name and Model Router handles dispatch.
