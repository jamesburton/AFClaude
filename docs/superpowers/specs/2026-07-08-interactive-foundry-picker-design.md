# Interactive Azure Foundry region/model picker — Design

Date: 2026-07-08

## Problem

Today, `Foundry__Endpoint` and `Foundry__Deployment` must be known and set as env vars
(or `appsettings.json`/user-secrets) before running AFClaude at all — `launch` and
`--http` fail fast with an `InvalidOperationException` if either is missing. Finding
the right values requires manually running `az cognitiveservices account list` and
`az cognitiveservices account deployment list` and reading JSON (documented in
README.md, but entirely manual). This design adds an interactive picker so a user
with `az login` already done can select subscription → resource → deployment from a
TUI menu instead, with the result optionally saved for future runs.

## Scope

In scope: `launch` and `--http` modes only (both normally run attached to a real
terminal). Out of scope: the default MCP stdio mode's stdout is reserved for
JSON-RPC (Phase 3) and Claude Code launches it with no operator present, so it can
never show a menu — it keeps today's fail-fast behavior, but gains passive support
for reading a saved config file (see below).

## Trigger & precedence

Resolved once at startup, before `FoundryClientFactory.Create` runs:

1. `--select` (alias `--configure`) passed → always run the interactive wizard,
   ignoring env vars and any existing config file. This also covers "reset" — running
   `--select` when a default config file already exists re-triggers selection and
   overwrites it if the user chooses to save again.
2. Else `Foundry__Endpoint` **and** `Foundry__Deployment` env vars both set → use
   them (today's behavior, unchanged, all modes).
3. Else `--config <file>` passed → load that file. If it does not exist, fail fast
   with `Missing Config <file>` — no silent fallback to the wizard, since an explicit
   path means explicit intent.
4. Else a default config file (`afclaude.config.json`, current working directory)
   exists → load it silently. Applies to **all modes, including MCP** — this is a
   plain file read, not an interactive act.
5. Else, only in `launch`/`--http` modes, and only when both
   `Console.IsInputRedirected` and `Console.IsOutputRedirected` are false (a real
   interactive terminal is attached) → run the interactive wizard.
6. Else → today's fail-fast `InvalidOperationException` naming the missing env vars
   (covers MCP default mode with nothing configured, and non-interactive/CI
   invocations of launch or `--http`).

`Foundry__Api` participates in the same precedence: when the wizard resolves it via
a live probe, the concrete value (`anthropic`/`openai`) is what gets saved/used —
`auto` is never written to a saved config file.

## Components

- **`AzCli.cs`** — shells out to `az <args> -o json`, applies the same timeout as
  `Foundry__CliTimeoutSeconds`, and classifies failures (az not installed / not
  logged in / timeout) reusing `FoundryErrors`' existing classification style rather
  than surfacing raw `Process` exceptions. Returns parsed JSON (`JsonDocument` or a
  small typed record per call site).
- **`FoundryConfigFile.cs`** — the JSON config model (`Endpoint`, `Deployment`,
  `Api`), default filename (`afclaude.config.json`), load/save, and the precedence
  logic in steps 2–4 above. Pure, no Spectre/console dependency — independently
  testable.
- **`FoundryConfigWizard.cs`** — the interactive flow itself, built on
  `Spectre.Console` (new NuGet dependency):
  1. `az account list` → if exactly one subscription, skip straight through; else a
     `SelectionPrompt<T>` by subscription name.
  2. `az cognitiveservices account list` (scoped to the chosen subscription),
     filtered to `kind` `AIServices`/`OpenAI`, shown as
     `"<name> (<location>, <resourceGroup>)"`.
  3. `az cognitiveservices account deployment list` for the chosen resource, shown
     as `"<deployment-name> → <model>/<version>"`.
  4. Run the existing `Foundry__Api=auto` probe logic against the resolved
     endpoint+deployment and display which mode was detected
     (`anthropic`/`openai`).
  5. Prompt: **Save to file** (default filename shown — the `--config` path if one
     was given, else `afclaude.config.json`; editable) or **Don't save** (default).
     No third "session" option — a child process cannot mutate the parent shell's
     environment, so anything short of writing a file wouldn't persist past this
     run anyway.
- **`Program.cs`** — wires the precedence chain in before constructing the
  `FoundryClientFactory`, and adds the `--select`/`--configure` and `--config <file>`
  CLI flags (parsed alongside the existing `--http`/`launch` argument handling).

Any subscription/resource/deployment list that comes back empty at its stage is a
clear "no X found" error and exit, not a confusing empty menu.

## Config file format

```json
{
  "Endpoint": "https://<resource>.cognitiveservices.azure.com/",
  "Deployment": "<deployment-name>",
  "Api": "anthropic"
}
```

Written/read from the current working directory by default (`afclaude.config.json`),
or the path given via `--config`.

## CLI flags summary

| Flag | Effect |
|---|---|
| `--select` / `--configure` | Force the interactive wizard now, regardless of env vars or an existing config file. |
| `--config <file>` | Load config from `<file>` instead of the default `afclaude.config.json`; fails fast with `Missing Config <file>` if it doesn't exist. Also the suggested save-target filename when the wizard runs. |

## Testing

- Unit tests: `FoundryConfigFile` precedence/load/save and JSON parsing (canned `az`
  JSON fixtures for subscriptions/resources/deployments), `AzCli` error
  classification (az missing / timeout), wizard selection logic driven through
  Spectre.Console's `IAnsiConsole` test-console abstraction (no real terminal
  required, matches how the rest of the suite avoids needing real Azure auth).
- Manual: a new TESTING.md stage exercising the real interactive flow end-to-end
  against a real subscription (`az login` already done), matching how other real-
  Foundry behavior in this project is verified.

## Out of scope

- MCP stdio mode never runs the interactive wizard (no TTY available to it).
- No support for mutating the parent shell's environment ("session" persistence) —
  removed per review; save-to-file or don't-save only.
- No change to the existing `Foundry__Api=auto` runtime probe behavior when no
  saved `Api` value is present — this only adds an option to resolve and pin it
  once during the wizard.
