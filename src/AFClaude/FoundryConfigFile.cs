using System.Text.Json;

namespace AFClaude;

// The three Foundry settings the interactive picker resolves and can persist. Api is
// always a concrete value here ("anthropic"/"openai") — "auto" is never saved, since a
// saved config exists specifically to skip the probe next time.
internal sealed record FoundryConfig(string Endpoint, string Deployment, string Api);

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
}
