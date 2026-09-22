# Handoff

## Goal
AFClaude is a local proxy that lets Claude Code (and MCP clients) run against Azure AI Foundry deployments — both native Anthropic (Claude) deployments via a passthrough, and OpenAI-compatible deployments via an Anthropic↔OpenAI bridge. The last two sessions (v0.5–v0.7) made it robust to Foundry incompatibilities and added a self-healing model; this session began Phase 14 — multi-model routing and context-window documentation.

## Current State
**v0.7.0, 116 tests, all green.** Working tree has uncommitted README/PLAN.md changes from this session (not yet committed). `main` == `origin/main` at `4973c15`.

Nothing from Phase 14.2 (multi-deployment router) or 14.3 (history block stripping) has been implemented yet.

## qhub-sweden Resource Inventory (checked 2026-09-22)
Subscription: FNZ Q-Hub Azure (`c83a19df-6be1-4eba-9505-9ab469177af5`)  
Resource endpoint: `https://qhub-sweden.cognitiveservices.azure.com/`

| Deployment | Model | Format | Context |
|-----------|-------|--------|---------|
| `claude-sonnet-5` | claude-sonnet-5 v2 | Anthropic | **1M** ✅ |
| `claude-opus-4-6` | claude-opus-4-6 v1 | Anthropic | 200K ⚠️ |
| `claude-sonnet-4-6` | claude-sonnet-4-6 v1 | Anthropic | 200K ⚠️ |
| `claude-haiku-4-5` | claude-haiku-4-5 | Anthropic | 200K ⚠️ |
| `gpt-5.6-terra` | gpt-5.6-terra | OpenAI | — |
| `gpt-5.6-luna` | gpt-5.6-luna | OpenAI | — |
| `grok-4-1-fast-reasoning` | grok-4-1-fast-reasoning | xAI | — |
| `DeepSeek-V4-Pro` | DeepSeek-V4-Pro | DeepSeek | — |
| `Kimi-K2.6` | Kimi-K2.6 | MoonshotAI | — |
| `gpt-realtime-mini` | gpt-realtime-mini | OpenAI | — |

**Critical gap:** No 1M-context Opus deployed. `claude-opus-4-6` is 200K — early compaction
for any Opus-mode session. Needs `claude-opus-4-8` or `claude-opus-5` deployed in the Foundry portal.

## What This Session Did (Phase 14.1)
- Added "Multi-model configuration and context windows" section to README: `ANTHROPIC_DEFAULT_*_MODEL`
  env vars, `/model` switching, `--fallback-model`, `CLAUDE_CODE_AUTO_COMPACT_WINDOW`, AFClaude's
  single-deployment limitation, Foundry Model Router note.
- Updated README Status section to reflect v0.7.0 and the full feature set.
- Added PLAN.md phases 13.5, 13.6, 13.7 (documenting the three commits from the previous Claude Opus 5
  session that weren't yet documented), plus Phase 14 skeleton with 14.1/14.2/14.3 sub-phases.
- `qhub-sweden-setup.md` artifact written (immediate PowerShell/cmd.exe setup guide with current deployment table).

## Key Decisions Made
- Context-window fix is **env-var only** for now (no AFClaude code change) — `ANTHROPIC_DEFAULT_*_MODEL`
  is read by `claude` itself, not AFClaude, so no proxy change is needed. Document it prominently.
- Multi-deployment routing (14.2) deferred pending a decision between three options:
  1. AFClaude routes by `model` field across multiple `Foundry__Deployments__<alias>` entries
  2. Foundry Model Router (single endpoint, Foundry handles dispatch — no AFClaude change)
  3. Env-var only (current workaround — already works for same-resource deployments)
- History block stripping (14.3) deferred until there is evidence of it actually causing a 400
  in a resumed session.

## What Worked
- `az cognitiveservices account deployment list` is the right command to inventory deployments;
  `--query` with JMESPath `properties.model.name`/`properties.model.version`/`properties.model.format`
  gives the format needed to distinguish Anthropic vs OpenAI vs xAI deployments.
- The `ANTHROPIC_DEFAULT_*_MODEL` env vars are the correct client-side fix; AFClaude passes `model`
  through unchanged in passthrough mode.

## Important Context
- Windows dev box; `az` logged in to FNZ Q-Hub subscription.
- `main` pushed directly; no PR flow.
- Tests that touch config files: `CurrentDirectoryTestCollection`.
- `AFClaude__TraceDir` for wire-level diagnosis.
- Saved config files from v0.6.1 wizard have explicit `"MaxTokensParam": "legacy"` — won't self-heal
  until the user runs `--select` or edits the file to `"auto"`.

## Next Steps (in order)
1. **Commit README + PLAN.md changes** from this session, bump to v0.7.1 (docs-only), push tag.
2. **Deploy `claude-opus-4-8`** (or `claude-opus-5`) in the qhub-sweden Foundry resource — then set
   `ANTHROPIC_DEFAULT_OPUS_MODEL=claude-opus-4-8` and verify `opusplan` works end-to-end.
3. **Live-verify v0.7.0** against `claude-sonnet-5` on Foundry:
   - Confirm advisor 400 is absorbed and log shows `Dropping ... tools[advisor_20260301]`
   - Confirm a gpt-5.6 deployment with `auto` config retries and patches the config file to `"new"`
4. **Phase 14.2** — decide on multi-deployment routing approach and implement if needed.
5. **Phase 14.3** — history block stripping: reproduce the 400 in a resumed session with a stripped
   tool in history, then implement stripping of `server_tool_use` content blocks.
6. Consider adding `ANTHROPIC_DEFAULT_*_MODEL` env-var passthrough to the wizard output / saved config
   as a convenience (so `--select` produces a ready-to-use env-var block to copy-paste).
