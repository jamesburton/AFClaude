# Handoff

## Goal
AFClaude is a local proxy that lets Claude Code (and MCP clients) run against Azure AI Foundry deployments — both native Anthropic (Claude) deployments via a passthrough, and OpenAI-compatible deployments via an Anthropic↔OpenAI bridge.

## Current State
**v0.9.1, 144 tests, all green.** Model capability equivalence mappings for longer-context models published.

## qhub-sweden Resource Inventory
Endpoint: `https://qhub-sweden.cognitiveservices.azure.com/`  
**All GlobalStandard (PAYG/serverless). Zero standing cost when idle.**

| Deployment | Model | Version | Context Window | Best-Matched Equivalent Alias |
|-----------|-------|---------|----------------|-------------------------------|
| `claude-sonnet-5` | claude-sonnet-5 | v2 | 1M ✅ | — (native passthrough) |
| `claude-opus-5` | claude-opus-5 | v2 | 1M ✅ | — (native passthrough) |
| `claude-haiku-4-5` | claude-haiku-4-5 | v2 | 200K | — (native passthrough) |
| `claude-fable-5-1` | claude-fable-5-1 | v1 (Preview) | 1M+ tier | — (native passthrough) |
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

# Subsequent launches — loads saved config, injects env vars, applies aliases
dnx AFClaude -y -- launch
```

## Saved Config Format (with Equivalence Aliases)
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
    "gpt-6-astra": "claude-fable-5-1",
    "gpt-5.6-sol": "claude-opus-5",
    "gpt-5.6-terra": "claude-sonnet-5",
    "gpt-5.6-luna": "claude-haiku-4-5"
  }
}
```

**`ModelRoles`** — injects `ANTHROPIC_DEFAULT_*_MODEL` env vars into claude's process at launch. Enables `/model` switching and background model selection.

**`ModelNameAliases`** — rewrites the `model` field in bridge-path (OpenAI) responses so Claude Code recognizes them as defined capability tiers with 1M-context compaction behavior:
- `astra` → `claude-fable-5-1` (frontier/agent flagship)
- `sol` → `claude-opus-5` (heavy reasoning tier)
- `terra` → `claude-sonnet-5` (balanced 1M tier)
- `luna` → `claude-haiku-4-5` (fast tier; 200K window)
- `grok` → `claude-opus-5`, `deepseek`/`kimi` → `claude-sonnet-5`

Native Anthropic passthrough is byte-faithful and unaffected.

## Recent Progress Summary

### Phase 14.2 (v0.8.0)
- `FoundryConfig` + `ModelRoles` wizard step + `ApplyModelRoles` in launch.
- Auto-injects `ANTHROPIC_DEFAULT_*_MODEL` at every launch from saved config.

### Phase 14.3 (v0.9.0 & Capability Equivalences)
- `FoundryConfig.ModelNameAliases` + `ModelAliasConfig` (DI singleton).
- Bridge-path `/v1/messages` handler rewrites `model` field in responses.
- Extended wizard to suggest aliases for **all** non-Claude deployments on the resource.
- Introduced `SuggestAliasFor` with a dedicated capability equivalence table:
  - `astra` → `fable`
  - `sol` → `opus`
  - `terra` → `sonnet`
  - `luna` → `haiku` (with a context window notice in the UI)
  - `grok` → `opus`, `deepseek`/`kimi` → `sonnet`
- Test suite expanded to 144 passing unit tests covering all pattern matches and fallbacks.

## Key Design Decisions
- Equivalence matching uses deployment name patterns (`astra`, `sol`, `terra`, `luna`) and prioritizes the highest matching version (`claude-sonnet-5` beats `4-6`).
- Aliasing operates across all non-Claude deployments present on the resource, not just those currently selected in `ModelRoles`.
- Native Anthropic passthrough remains byte-faithful and unmodified.
- When `luna` is aliased to `haiku`, a console notice highlights that Haiku 4.5 is a 200K context model.

## Open Items & Future Plans
1. **Live-verify v0.9.0+ wizard** — run `dnx AFClaude -y -- launch --select` on qhub-sweden to test interactive auto-suggestions for all GPT models.
2. **Phase 14.4** — history `server_tool_use`/result block stripping (verify if resumed sessions with stripped tool definitions trigger validation 400s in Foundry).
3. **Evaluate `gpt-6-astra` and `gpt-5.6-sol` tool calling fidelity** — run E2E tool tests with Claude Code in bridge mode to check if 2026 OpenAI models avoid the plain-text tool fabrication seen in gpt-4.1.
4. **`claude-fable-5-1` evaluation** — explore performance differences on complex tasks compared to Sonnet and Opus.

## Billing Reminder
All qhub-sweden deployments are `GlobalStandard` = PAYG. No standing hourly cost.
`GlobalProvisionedManaged` = PTU (hourly charge). None of our deployments are PTU.
