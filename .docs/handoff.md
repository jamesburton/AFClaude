# Handoff

## Goal
AFClaude is a local proxy that lets Claude Code (and MCP clients) run against Azure AI Foundry deployments — both native Anthropic (Claude) deployments via a passthrough, and OpenAI-compatible deployments via an Anthropic↔OpenAI bridge.

## Current State
**v0.7.0, 116 tests, all green.** `main` == `origin/main` at `f9eeeb7` (Phase 14.1 docs commit).
No code changes this session — deployment management and documentation only.

## qhub-sweden Resource Inventory (updated 2026-09-22)
Subscription: FNZ Q-Hub Azure (`c83a19df-6be1-4eba-9505-9ab469177af5`)  
Resource endpoint: `https://qhub-sweden.cognitiveservices.azure.com/`  
**All deployments: `GlobalStandard` (pay-per-token, zero standing cost). No PTU/provisioned deployments exist.**

| Deployment | Model | Version | Context | Notes |
|-----------|-------|---------|---------|-------|
| `claude-sonnet-5` | claude-sonnet-5 | **v2** ✅ | 1M | Upgraded this session |
| `claude-opus-5` | claude-opus-5 | **v2** ✅ | 1M | New this session |
| `claude-haiku-4-5` | claude-haiku-4-5 | **v2** ✅ | 200K | Delete+recreate this session (was 20251001, non-upgradable) |
| `claude-fable-5-1` | claude-fable-5-1 | v1 | — | New this session (Preview) |
| `gpt-6-astra` | gpt-6-astra | 2026-09-03 | — | New this session; EU data residency |
| `gpt-5.6-sol` | gpt-5.6-sol | 2026-07-09 | — | New this session |
| `gpt-5.6-terra` | gpt-5.6-terra | 2026-07-09 | — | Pre-existing |
| `gpt-5.6-luna` | gpt-5.6-luna | 2026-07-09 | — | Pre-existing |
| `claude-sonnet-4-6` | claude-sonnet-4-6 | v1 | 200K | Legacy, pre-existing |
| `claude-opus-4-6` | claude-opus-4-6 | v1 | 200K | Legacy, pre-existing |
| `grok-4-1-fast-reasoning` | grok-4-1-fast-reasoning | v1 | — | Pre-existing |
| `DeepSeek-V4-Pro` | DeepSeek-V4-Pro | 2026-04-23 | — | Pre-existing |
| `Kimi-K2.6` | Kimi-K2.6 | 2026-04-20 | — | Pre-existing |
| `gpt-realtime-mini` | gpt-realtime-mini | 2025-12-15 | — | Pre-existing |
| `text-embedding-3-large` | text-embedding-3-large | v1 | — | Standard SKU |
| `text-embedding-3-small` | text-embedding-3-small | v1 | — | Pre-existing |

## Billing Clarification (confirmed this session)
- `GlobalStandard` = pay-per-token (serverless). Capacity number = TPM rate limit, not reserved compute. **No hourly charge when idle.**
- `GlobalProvisionedManaged` / `ProvisionedManaged` = PTU (hourly charge regardless of usage). **None of these exist in qhub-sweden.**
- The user confirmed they want serverless only; all new deployments comply.

## Recommended PowerShell Quick-Start
```powershell
$env:Foundry__Endpoint              = "https://qhub-sweden.cognitiveservices.azure.com/"
$env:Foundry__Deployment            = "claude-sonnet-5"
$env:ANTHROPIC_DEFAULT_SONNET_MODEL = "claude-sonnet-5"
$env:ANTHROPIC_DEFAULT_HAIKU_MODEL  = "claude-haiku-4-5"
$env:ANTHROPIC_DEFAULT_OPUS_MODEL   = "claude-opus-5"
$env:ANTHROPIC_DEFAULT_FABLE_MODEL  = "claude-fable-5-1"
dnx AFClaude -y -- launch
```

## What This Session Did
- Checked Sweden Central model catalogue; identified available versions of opus, haiku, fable, astra, sol
- Deployed: `claude-opus-5` v2, `claude-fable-5-1` v1, `gpt-6-astra`, `gpt-5.6-sol`
- Upgraded: `claude-sonnet-5` v1→v2 (in-place), `claude-haiku-4-5` 20251001→v2 (delete+recreate, in-place blocked)
- Confirmed all deployments are GlobalStandard (PAYG/serverless, no standing cost)
- Updated `qhub-sweden-setup.md` artifact with full inventory and quick-start commands

## Important Context
- Windows dev box; `az` logged in to FNZ Q-Hub subscription.
- `main` pushed directly; no PR flow.
- Tests: `CurrentDirectoryTestCollection` for config file tests.
- `AFClaude__TraceDir` for wire-level diagnosis.
- Saved config files from ≤v0.6.1 wizard have explicit `"MaxTokensParam": "legacy"` — won't self-heal until `--select` or manual edit to `"auto"`.
- `claude-haiku-4-5` v2 was still in `Creating` state when this handoff was written — should be `Succeeded` within a few minutes.

## Next Steps (in order)
1. **Live-verify** v0.7.0 against `claude-sonnet-5` v2: launch Claude Code, confirm advisor-tool 400 absorbed, confirm 1M context window is in effect (check `CLAUDE_CODE_AUTO_COMPACT_WINDOW` isn't forcing early compaction).
2. **Live-verify** `opusplan` with `claude-opus-5` v2: set both `ANTHROPIC_DEFAULT_OPUS_MODEL` + `ANTHROPIC_DEFAULT_SONNET_MODEL`, run `/model opusplan` in-session.
3. **Evaluate `claude-fable-5-1`** — what is it good for? Try a long-running agentic task.
4. **Evaluate `gpt-6-astra`** — test through AFClaude bridge path; check if it needs `MaxTokensParam: new` (will self-heal on first request if so).
5. **Phase 14.2** — decide on multi-deployment routing: AFClaude router vs Foundry Model Router vs env-var only.
6. **Phase 14.3** — history block stripping for resumed sessions with stripped tool types.
7. **Consider retiring** `claude-sonnet-4-6` and `claude-opus-4-6` once newer versions are confirmed stable.
