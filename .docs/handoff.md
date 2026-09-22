# Handoff

## Goal
AFClaude is a local proxy that lets Claude Code (and MCP clients) run against Azure AI Foundry deployments — both native Anthropic (Claude) deployments via a passthrough, and OpenAI-compatible deployments via an Anthropic↔OpenAI bridge.

## Current State
**v0.8.0, 125 tests, all green.** `main` == `origin/main` at `4fccd49`. CI publishing v0.8.0 to NuGet.

## qhub-sweden Resource Inventory (updated 2026-09-22)
Endpoint: `https://qhub-sweden.cognitiveservices.azure.com/`  
**All GlobalStandard (PAYG/serverless). Zero standing cost when idle.**

| Deployment | Model | Version | Context |
|-----------|-------|---------|---------|
| `claude-sonnet-5` | claude-sonnet-5 | **v2** | 1M ✅ |
| `claude-opus-5` | claude-opus-5 | **v2** | 1M ✅ |
| `claude-haiku-4-5` | claude-haiku-4-5 | **v2** | 200K |
| `claude-fable-5-1` | claude-fable-5-1 | v1 (Preview) | — |
| `gpt-6-astra` | gpt-6-astra | 2026-09-03 | — |
| `gpt-5.6-sol/terra/luna` | gpt-5.6-* | 2026-07-09 | — |
| `claude-sonnet-4-6`, `claude-opus-4-6` | — | v1 | 200K (legacy) |
| grok, DeepSeek, Kimi, realtime, embeddings | — | various | — |

## Recommended Quick-Start
```powershell
$env:Foundry__Endpoint   = "https://qhub-sweden.cognitiveservices.azure.com/"
$env:Foundry__Deployment = "claude-sonnet-5"
dnx AFClaude -y -- launch --select   # first time: runs wizard, saves config with ModelRoles
dnx AFClaude -y -- launch            # subsequent: loads saved config, injects model role vars
```

The wizard now offers to configure model role aliases at the end of the setup flow.
Saved config with `ModelRoles` means `ANTHROPIC_DEFAULT_SONNET/HAIKU/OPUS/FABLE_MODEL`
are injected automatically at every launch — no manual env-var management needed.

## What This Session Did (Phase 14.2)
- `FoundryConfig` gained optional `ModelRoles` dictionary (`Sonnet`/`Haiku`/`Opus`/`Fable` → deployment name)
- `ApplyModelRoles` in `Program.cs` injects `ANTHROPIC_DEFAULT_*_MODEL` env vars before spawning `claude`; caller env vars always win
- `FoundryConfigWizard` extended with `OfferConfigureModelRoles` (interactive per-role picker with auto-suggestions) and `SuggestRole` (pure pattern-matching, testable)
- README: interactive setup section rewritten as numbered steps, saved config example updated, launch section documents injected vars
- PLAN.md: 14.2 marked DONE, PLAN.md + HANDOFF.md updated
- 8 new tests (125 total, all green); v0.8.0 tagged and pushed (CI publishing to NuGet)

## Key Decisions Made
- Used env-var injection approach (option 3) rather than building a proxy-level model router or Foundry Model Router — simpler, zero new infrastructure, works because all deployments are on the same resource
- `<skip>` placed last in role picker so Enter immediately selects the suggested deployment
- Deployments listed before `<skip>` in wizard; lexicographically highest deployment name wins for each role (so `claude-sonnet-5` beats `claude-sonnet-4-6`)
- Caller env vars always override injected `ModelRoles` — safe to layer on top of existing scripts

## Open Items (in priority order)
1. **Live-verify v0.8.0**: run `--select`, confirm role wizard appears, confirm `ANTHROPIC_DEFAULT_*_MODEL` vars are visible in a trace session
2. **Deploy `claude-opus-4-8`** as backup if `claude-opus-5` proves too expensive for routine use
3. **Phase 14.3** — history `server_tool_use`/result block stripping: if a resumed conversation has history referencing a stripped tool type (e.g. `advisor_20260301`), Foundry may 400 even though the tool def is no longer in the request. Unverified; needs a traced resumed session.
4. **Evaluate `claude-fable-5-1` and `gpt-6-astra`** — what are they actually good for? Try agentic tasks.
5. Consider wizard preset profiles: "Claude Full Stack" auto-fills all 4 roles from the resource's deployments without prompting per-role.

## Important Context
- Windows dev box; push directly to `main`; no PR flow
- Tests that touch config files: `CurrentDirectoryTestCollection`
- `AFClaude__TraceDir` for wire-level diagnosis
- Saved configs from ≤v0.6.1 have `"MaxTokensParam": "legacy"` — won't self-heal; run `--select` to regenerate
- `claude-haiku-4-5` v2 was deployed with delete+recreate (in-place upgrade blocked by Azure)
