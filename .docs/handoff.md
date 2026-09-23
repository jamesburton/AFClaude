# Handoff

## Goal
AFClaude is a local proxy that lets Claude Code (and MCP clients) run against Azure AI Foundry deployments — both native Anthropic (Claude) deployments via a passthrough, and OpenAI-compatible deployments via an Anthropic↔OpenAI bridge.

## Current State
**v0.9.4 in progress, 179 tests, all green.** Known model group onboarding, recommended-first selection sorting, statusline progress bar fix, model `[1m]` suffix handling, 1M context auto-compaction window injection, and history `server_tool_use` sanitization completed.

## qhub-sweden Resource Inventory
Endpoint: `https://qhub-sweden.cognitiveservices.azure.com/`  
**All GlobalStandard (PAYG/serverless). Zero standing cost when idle.**

| Deployment | Model | Version | Context Window | Best-Matched Equivalent Alias |
|-----------|-------|---------|----------------|-------------------------------|
| `claude-sonnet-5` | claude-sonnet-5 | v2 | 1M ✅ | — (native passthrough with [1m] designation) |
| `claude-opus-5` | claude-opus-5 | v2 | 1M ✅ | — (native passthrough with [1m] designation) |
| `claude-haiku-4-5` | claude-haiku-4-5 | v2 | 200K | — (native passthrough) |
| `claude-fable-5-1` | claude-fable-5-1 | v1 (Preview) | 1M+ tier | — (native passthrough with [1m] designation) |
| `gpt-6-astra` | gpt-6-astra | 2026-09-03 | frontier tier | `claude-fable-5-1` |
| `gpt-5.6-sol` | gpt-5.6-sol | 2026-07-09 | deep reasoning tier | `claude-opus-5` |
| `gpt-5.6-terra` | gpt-5.6-terra | 2026-07-09 | balanced tier | `claude-sonnet-5` |
| `gpt-5.6-luna` | gpt-5.6-luna | 2026-07-09 | fast tier | `claude-haiku-4-5` (200K window notice) |
| `grok-4-1-fast-reasoning` | grok-4-1-fast-reasoning | v1 | reasoning tier | `claude-opus-5` |
| `DeepSeek-V4-Pro` | DeepSeek-V4-Pro | 2026-04-23 | balanced tier | `claude-sonnet-5` |
| `Kimi-K2.6` | Kimi-K2.6 | 2026-04-20 | balanced tier | `claude-sonnet-5` |
| `claude-sonnet-4-6`, `claude-opus-4-6` | — | v1 | 200K (legacy) | — |
| `gpt-realtime-mini`, embeddings | — | various | — | — |

## Quick-Start
```powershell
# First time — runs wizard, saves config with ModelRoles + ModelNameAliases
dnx AFClaude -y -- launch --select

# Subsequent launches — loads saved config, injects env vars, applies aliases & 1M compaction window
dnx AFClaude -y -- launch
```

## Saved Config Format (v0.9.3)
```json
{
  "Endpoint": "https://qhub-sweden.cognitiveservices.azure.com/",
  "Deployment": "claude-sonnet-5",
  "Api": "anthropic",
  "MaxTokensParam": "auto",
  "AutoCompactWindow": 900000,
  "ModelRoles": {
    "Sonnet": "claude-sonnet-5",
    "Haiku":  "claude-haiku-4-5",
    "Opus":   "claude-opus-5",
    "Fable":  "claude-fable-5-1"
  },
  "ModelNameAliases": {
    "gpt-6-astra": "claude-fable-5-1",
    "gpt-5.6-sol": "claude-opus-5",
    "gpt-5.6-terra": "claude-sonnet-5",
    "gpt-5.6-luna": "claude-haiku-4-5"
  }
}
```

**`ModelRoles`** — injects `ANTHROPIC_DEFAULT_*_MODEL` env vars into claude's process at launch (with `[1m]` suffix appended for 1M models). Enables in-session `/model` switching and background model selection.

**`ModelNameAliases`** — rewrites the `model` field in bridge-path (OpenAI) responses so Claude Code recognizes them as defined capability tiers with 1M-context compaction behavior:
- `astra` → `claude-fable-5-1[1m]` (frontier/agent flagship)
- `sol` → `claude-opus-5[1m]` (heavy reasoning tier)
- `terra` → `claude-sonnet-5[1m]` (balanced 1M tier)
- `luna` → `claude-haiku-4-5` (fast tier; 200K window)
- `grok` → `claude-opus-5[1m]`, `deepseek`/`kimi` → `claude-sonnet-5[1m]`

**`AutoCompactWindow`** — configures token threshold for compaction. When 1M models are in use, AFClaude defaults `CLAUDE_CODE_AUTO_COMPACT_WINDOW` to `900000` automatically so Claude Code does not default to 200k.

## Recent Progress Summary

### Phase 14.2 (v0.8.0)
- `FoundryConfig` + `ModelRoles` wizard step + `ApplyModelRoles` in launch.
- Auto-injects `ANTHROPIC_DEFAULT_*_MODEL` at every launch from saved config.

### Phase 14.3 (v0.9.0 & Capability Equivalences)
- `FoundryConfig.ModelNameAliases` + `ModelAliasConfig` (DI singleton).
- Bridge-path `/v1/messages` handler rewrites `model` field in responses.
- Extended wizard to suggest aliases for all non-Claude deployments on the resource.
- Introduced `SuggestAliasFor` with dedicated capability equivalence table.

### Phase 14.4 (v0.9.2 - v0.9.3)
- **Resolved History `server_tool_use` Validation Error during `/compact`**: `FoundryAnthropic.SanitizeHistoryServerToolUse` converts unknown server tools (e.g. `advisor_20260301`) and their corresponding tool result blocks in messages history into standard `text` blocks, bypassing Foundry's strict server-tool enum validator while preserving conversational context.
- **Resolved Short Context / 200k Default on 1M Models**:
  - Claude Code requires the `[1m]` suffix on model identifiers (e.g. `claude-sonnet-5[1m]`) when connected to custom endpoints, otherwise it reports `capped to 200k by model`.
  - `LaunchEnvironment.With1mSuffixIfSupported` appends `[1m]` to `ANTHROPIC_MODEL`, `ModelRoles`, and bridge aliases for all 1M models (`sonnet-5`, `opus-5`, `fable-5-1`).
  - `ForwardAnthropicAsync` injects `[1m]` into the `message_start` response event and non-streaming responses.
  - `FoundryClientFactory` and `PrepareBody` transparently strip `[1m]` so Foundry receives clean Azure deployment names.
- Test suite expanded to 169 tests, all green.

### Phase 14.5 (Context Progress Bar Fix)
- **Resolved 100% Context Progress Bar Issue**:
  - **Root Cause**: The statusline hook (`C:\Users\james\.claude\hooks\gsd-statusline.js`) had an inverted calculation when `CLAUDE_CODE_AUTO_COMPACT_WINDOW` was set. It calculated `AUTO_COMPACT_BUFFER_PCT = (acw / totalCtx) * 100` (evaluating to 90% for a 900k threshold on 1M context), mistakenly treating the compaction threshold as the buffer rather than `(totalCtx - acw) / totalCtx * 100` (10% buffer). Any usage over 10% (remaining <= 90%) caused `usableRemaining` to clamp to `0%`, pegging the statusline meter to `💀 [██████████] 100%`.
  - **Context Window Property**: In Claude Code v2.1+, the payload property is `data.context_window.context_window_size` rather than `total_tokens`.
  - **Fix Applied**: Updated `gsd-statusline.js` to recognize `context_window_size` and correctly calculate the autocompact buffer percentage as `((totalCtx - acw) / totalCtx) * 100`. The status bar now accurately reflects 1M usage (e.g., 255k tokens renders as ~29% in green instead of 100% skull).

### Phase 14.6 (Streamlined Onboarding & Known Model Groups)
- **Known Model Group Detection**:
  - Automatically identifies complete model groups on the selected resource:
    - **Anthropic Group**: `claude-fable-5-1`, `claude-opus-5`, `claude-sonnet-5` (and optional `claude-haiku-4-5`).
    - **OpenAI Group**: `gpt-6-astra`, `gpt-5.6-sol`/`gpt-6-sol`, `gpt-5.6-terra` (and optional `gpt-5.6-luna`/`gpt-6-luna`).
    - **Custom models**: manual individual selection fallback.
  - The Haiku / Luna tier is optional and omitted from the group label if absent on the resource.
- **Active / Start Model Swapped First**:
  - When a group is chosen, it automatically maps the roles and lets the user select the active/start model directly from the mapped group.
- **Recommended-First Sorting**:
  - Every selection prompt puts the recommended option at the very top:
    - Group picker: Anthropic group `(Recommended)` first.
    - Active model picker: Sonnet / Terra `(Recommended)` first.
    - Deployment picker: Sonnet / Terra `(Recommended)` first.
    - Role and alias picker: Suggested deployment / alias `(suggested)` first.
- **Default Aliases for OpenAI Groups**:
  - `BuildDefaultModelNameAliases` automatically maps OpenAI deployments to Claude counterparts so Claude Code treats them with appropriate context windows and role mapping without tedious per-model prompts.
- **Azure SwedenCentral Model Availability Findings (2026-09-23)**:
  - `gpt-6-sol` (version 2026-09-22) and `gpt-6-luna` (version 2026-09-22) released yesterday on GlobalStandard PAYG. Available to be deployed to `qhub-sweden`.
  - `gpt-6-astra` (version 2026-09-03) is already deployed.
  - `gpt-5.6-terra` (version 2026-07-09) is the current terra version (no `gpt-6-terra` on Azure yet).
  - `claude-opus-5-5` (version 2, GA) is also available on GlobalStandard.
- Test suite expanded to 179 tests (10 new tests), all passing.

## Key Design Decisions
- Equivalence matching uses deployment name patterns (`astra`, `sol`, `terra`, `luna`) and prioritizes the highest matching version (`claude-sonnet-5` beats `4-6`).
- Unknown server tool calls in history are converted to `[Server tool call: <name> ...]` text blocks rather than dropped, keeping message turn parity and transcript readability for summary generation.
- Caller env vars always override injected `ANTHROPIC_DEFAULT_*_MODEL` and `CLAUDE_CODE_AUTO_COMPACT_WINDOW`.

## Open Items & Future Plans
1. **Live-verify v0.9.2** — test `/compact` in a long session to confirm successful compaction without the 400 validation error, and check `/autocompact` to confirm the 900,000 token window.
2. **Evaluate `gpt-6-astra` and `gpt-5.6-sol` tool calling fidelity** — run E2E tool tests with Claude Code in bridge mode.
3. **`claude-fable-5-1` evaluation** — explore performance differences on complex tasks compared to Sonnet and Opus.

## Billing Reminder
All qhub-sweden deployments are `GlobalStandard` = PAYG. No standing hourly cost.
`GlobalProvisionedManaged` = PTU (hourly charge). None of our deployments are PTU.
