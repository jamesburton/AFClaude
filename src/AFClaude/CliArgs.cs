namespace AFClaude;

// Parsing/removal helpers for AFClaude's own new CLI flags (--select, --configure,
// --config <file>), distinct from LaunchArgs.cs which translates flags meant for the
// `claude` child process itself.
internal static class CliArgs
{
    // Known, accepted limitation: this matches by whole-argument equality across the
    // entire arg vector, with no notion of which positions are flags vs values. A
    // prompt passed as `-p "--select"` would be indistinguishable from the real
    // --select flag. Real collisions are very unlikely (a prompt string that IS
    // exactly "--select"/"--configure"), and correctly scoping this would require
    // knowing which args are values, which isn't information available here.
    public static bool HasSelectFlag(string[] args)
        => args.Contains("--select", StringComparer.OrdinalIgnoreCase)
        || args.Contains("--configure", StringComparer.OrdinalIgnoreCase);

    // Known, accepted limitation: same whole-argument-equality caveat as HasSelectFlag
    // above -- a `--config` appearing as someone else's flag value would also match.
    public static string? GetConfigPath(string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, "--config", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    // Removes AFClaude's own flags before the remaining args are forwarded to another
    // process (the real `claude` binary in launch mode) that doesn't know about them.
    // Known, accepted limitation: same whole-argument-equality caveat as HasSelectFlag
    // above -- e.g. `-p "--select"` strips the prompt value along with the flag.
    public static string[] StripAfClaudeFlags(string[] args)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--select", StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[i], "--configure", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase))
            {
                i++; // also skip its value
                continue;
            }
            result.Add(args[i]);
        }
        return [.. result];
    }
}
