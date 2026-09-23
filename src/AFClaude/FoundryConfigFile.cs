using System.Text.Json;

namespace AFClaude;

// The Foundry settings the interactive picker resolves and can persist. Api is always
// a concrete value here ("anthropic"/"openai") — "auto" is never saved, since a saved
// config exists specifically to skip the probe next time. MaxTokensParam defaults to
// "auto" -- the same default as when nothing is configured at all -- so older 3-field
// saved config files (from before this field existed) get the self-healing behaviour
// too. auto sends the legacy field until Azure authoritatively rejects it, so it's
// never worse than an explicit "legacy".
//
// ModelRoles maps Claude Code role names to Foundry deployment names. When populated,
// launch mode injects ANTHROPIC_DEFAULT_*_MODEL env vars automatically before spawning
// claude, so in-session /model switching and background-task model selection work
// without manual env-var management. Keys: "Sonnet", "Haiku", "Opus", "Fable".
// Null (the default) means no role aliases are configured — existing saved files
// without this field continue to work as before.
// ModelNameAliases maps incoming request model names to the model name returned in
// bridge-path responses. This lets Claude Code treat an OpenAI deployment (e.g.
// "gpt-6-astra") as a known model with a defined context window (e.g. "claude-sonnet-5"),
// preventing early compaction on models whose context window Claude Code doesn't know.
// Only affects the OpenAI bridge path — native Anthropic passthrough responses are
// forwarded byte-faithfully and already carry the correct Claude model name.
// AutoCompactWindow: token threshold for Claude Code context compaction (e.g. 900000).
// When null (default), AFClaude auto-detects if 1M models are in use and defaults to 900000.
internal sealed record FoundryConfig(string Endpoint, string Deployment, string Api, string MaxTokensParam = "auto",
    Dictionary<string, string>? ModelRoles = null,
    Dictionary<string, string>? ModelNameAliases = null,
    int? AutoCompactWindow = null);

internal static class FoundryConfigFile
{
    public const string DefaultFileName = "afclaude.config.json";

    // Returns null only when using the DEFAULT filename and it doesn't exist yet -- a
    // normal "nothing saved" state, not an error. An explicitly-named file that's
    // missing is always a hard failure: an explicit path means explicit intent.
    public static FoundryConfig? TryLoad(string? explicitPath)
    {
        var path = explicitPath ?? DefaultFileName;
        if (!File.Exists(path))
        {
            if (explicitPath is not null)
            {
                throw new InvalidOperationException($"Missing Config {explicitPath}");
            }
            return null;
        }

        var json = File.ReadAllText(path);
        try
        {
            return JsonSerializer.Deserialize<FoundryConfig>(json, JsonSerializerOptions.Web)
                ?? throw new InvalidOperationException($"Config file '{path}' is empty or invalid JSON.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Config file '{path}' is not valid JSON.", ex);
        }
    }

    public static void Save(string path, FoundryConfig config)
    {
        // PascalCase to match the documented config-file format (README.md); reads stay
        // case-insensitive via JsonSerializerOptions.Web in TryLoad, so older/hand-edited
        // lowercase files still load fine.
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(config, options));
    }

    // Best effort, for MaxTokensParamResolver's runtime self-heal: rewrites just the
    // MaxTokensParam of an existing saved config so the next run skips the retry.
    // Never throws -- the request that triggered this has already succeeded, and a
    // missing/locked/corrupt file must not turn that into a failure.
    public static void TryPersistMaxTokensParam(string path, string maxTokensParam)
    {
        try
        {
            var existing = TryLoad(path);
            if (existing is not null && existing.MaxTokensParam != maxTokensParam)
            {
                Save(path, existing with { MaxTokensParam = maxTokensParam });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Ignored -- see comment above.
        }
    }
}
