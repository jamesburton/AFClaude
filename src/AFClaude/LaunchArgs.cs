namespace AFClaude;

// `launch` forwards everything after it to `claude` verbatim — --continue,
// --resume [id], --worktree <name>, --model, -p, --allowedTools, ... — so new
// claude options work without AFClaude changes. Only convenience aliases that
// claude itself doesn't know are translated here, by exact whole-argument match
// (values, e.g. a prompt string that merely mentions an alias, are unaffected).
internal static class LaunchArgs
{
    public static string[] Translate(string[] claudeArgs)
        => [.. claudeArgs.Select(arg => arg switch
        {
            "--yolo" => "--dangerously-skip-permissions",
            _ => arg,
        })];
}
