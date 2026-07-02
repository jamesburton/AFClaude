using AFClaude;

namespace AFClaude.Tests;

public class LaunchArgsTests
{
    [Fact]
    public void Yolo_TranslatesToDangerouslySkipPermissions()
    {
        Assert.Equal(
            ["--dangerously-skip-permissions", "--continue"],
            LaunchArgs.Translate(["--yolo", "--continue"]));
    }

    [Fact]
    public void KnownClaudeOptions_PassThroughVerbatim()
    {
        string[] args =
        [
            "--continue",
            "--resume", "abc123",
            "--worktree", "feature-x",
            "--model", "claude-sonnet-4-6",
            "--dangerously-skip-permissions",
            "-p", "do the thing",
        ];

        Assert.Equal(args, LaunchArgs.Translate(args));
    }

    [Fact]
    public void AliasInsideAValue_IsNotTranslated()
    {
        // Only exact whole-argument matches are aliases; prompt text is untouched.
        string[] args = ["-p", "explain what --yolo does"];

        Assert.Equal(args, LaunchArgs.Translate(args));
    }
}
