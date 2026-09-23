# Handoff

## Goal
AFClaude is a local proxy that lets Claude Code (and MCP clients) run against Azure AI Foundry deployments — both native Anthropic (Claude) deployments via a passthrough, and OpenAI-compatible deployments via an Anthropic↔OpenAI bridge.

## Current State
**v0.9.0, 135 tests, all green.** `main` at `0594611`. CI publishing v0.9.0 to NuGet.

## qhub-sweden Resource Inventory
Endpoint: `https://qhub-sweden.cognitiveservices.azure.com/`  
**All GlobalStandard (PAYG/serverless). Zero standing cost when idle.**

| Deployment | Model | Version | Context |
|-----------|-------|---------|---------|
| `claude-sonnet-5` | claude-sonnet-5 | v2 | 1M ✅ |
| `claude-opus-5` | claude-opus-5 | v2 | 1M ✅ |
| `claude-haiku-4-5` | claude-haiku-4-5 | v2 | 200K |
| `claude-fable-5-1` | claude-fable-5-1 | v1 (Preview) | — |
| `gpt-6-astra` | gpt-6-astra | 2026-09-03 | — (alias to claude-sonnet-5 for 1M) |
| `gpt-5.6-sol/terra/luna` | gpt-5.6-* | 2026-07-09 | — |
| `claude-sonnet-4-6`, `claude-opus-4-6` | — | v1 | 200K (legacy) |
| grok, DeepSeek, Kimi, realtime, embeddings | — | various | — |

## Quick-Start
```powershell
# First time — runs wizard, saves config with ModelRoles + ModelNameAliases
dnx AFClaude -y -- launch --select

# Subsequent launches — loads saved config, injects env vars, applies aliases
dnx AFClaude -y -- launch
```

## Saved Config Format (v0.9.0)
```json
{
  "Endpoint": "https://qhub-sweden.cognitiveservices.azure.com/",
  "Deployment": "claude-sonnet-5",
  "Api": "anthropic",
  "MaxTokensParam": "auto",
  "ModelRoles": {
    "Sonnet": "claude-sonnet-5",
    "Haiku":  "claude-haiku-4-5",
    "Opus":   "claude-opus-5",
    "Fable":  "claude-fable-5-1"
  },
  "ModelNameAliases": {
    "gpt-6-astra": "claude-sonnet-5"
  }
}
```

**`ModelRoles`** — injects `ANTHROPIC_DEFAULT_*_MODEL` env vars into claude's process at launch. Enables `/model` switching and background model selection.

**`ModelNameAliases`** — rewrites the `model` field in bridge-path (OpenAI) responses. Allows Claude Code to apply 1M-context compaction thresholds for deployments it doesn't know about (e.g. `gpt-6-astra → claude-sonnet-5`). Native Anthropic passthrough is byte-faithful and unaffected.

## What This Session Did

### Phase 14.2 (v0.8.0)
- `FoundryConfig` + `ModelRoles` wizard step + `ApplyModelRoles` in launch
- Auto-injects `ANTHROPIC_DEFAULT_*_MODEL` at every launch from saved config

### Phase 14.3 (v0.9.0)
- `FoundryConfig.ModelNameAliases` + `ModelAliasConfig` (DI singleton)
- Bridge-path `/v1/messages` handler rewrites `model` field in responses before Claude Code sees it
- Wizard `OfferConfigureModelNameAliases` — detects non-Claude roles, offers per-deployment alias picker using Anthropic-format deployments as targets
- `AzDeploymentModel.Format` added for accurate Anthropic vs OpenAI detection
- `RunLaunchAsync` refactored to load saved config once (previously loaded twice, with a dead ModelRoles-from-overrides check)
- 10 new tests (135 total)
- README: config example + per-field descriptions updated

## Key Design Decisions
- Model alias rewriting only on bridge path — native Anthropic passthrough is byte-faithful by design
- `<skip>` / `<no alias>` listed last in wizard pickers so Enter immediately selects the suggested deployment
- `IsAnthropicDeployment` uses `Format` field with name-based fallback for hand-crafted test data
- Caller env vars always override `ModelRoles`-injected env vars

## Open Items (priority order)
1. **Live-verify v0.9.0** — run `--select`, confirm both wizard steps appear, confirm model alias shows up in trace logs for a gpt-6-astra request
2. **Phase 14.4** — history `server_tool_use`/result block stripping (unverified — needs a traced resumed session with a stripped tool type in history)
3. **Evaluate `gpt-6-astra` tool reliability** — tool use fidelity for Claude Code tasks (gpt-4.1 was unreliable; gpt-6-astra may be better)
4. **`claude-fable-5-1` exploration** — what workloads does Fable excel at vs Sonnet/Opus?

## Billing Reminder
All qhub-sweden deployments are `GlobalStandard` = PAYG. No standing hourly cost.
`GlobalProvisionedManaged` = PTU (hourly charge). None of our deployments are PTU.

## Important Context
- Windows dev box, push directly to `main`, no PR flow
- `CurrentDirectoryTestCollection` serializes wizard tests (they mutate `Directory.CurrentDirectory`)
- `AFClaude__TraceDir` for wire-level diagnosis
- Saved configs from ≤v0.6.1 have `"MaxTokensParam": "legacy"` — won't self-heal; run `--select` to regenerate
