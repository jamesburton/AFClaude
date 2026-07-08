using AFClaude;

namespace AFClaude.Tests;

public class CliArgsTests
{
    [Theory]
    [InlineData("--select")]
    [InlineData("--configure")]
    [InlineData("--SELECT")]
    public void HasSelectFlag_DetectsEitherSpellingCaseInsensitive(string flag)
    {
        Assert.True(CliArgs.HasSelectFlag(["launch", flag]));
    }

    [Fact]
    public void HasSelectFlag_AbsentReturnsFalse()
    {
        Assert.False(CliArgs.HasSelectFlag(["launch", "--continue"]));
    }

    [Fact]
    public void GetConfigPath_ReturnsValueFollowingFlag()
    {
        Assert.Equal("myfile.json", CliArgs.GetConfigPath(["--config", "myfile.json", "--continue"]));
    }

    [Fact]
    public void GetConfigPath_AbsentReturnsNull()
    {
        Assert.Null(CliArgs.GetConfigPath(["--continue"]));
    }

    [Fact]
    public void GetConfigPath_FlagWithNoValueReturnsNull()
    {
        Assert.Null(CliArgs.GetConfigPath(["--config"]));
    }

    [Fact]
    public void StripAfClaudeFlags_RemovesSelectConfigureAndConfigWithValue()
    {
        string[] args = ["--select", "--config", "myfile.json", "--continue", "-p", "do the thing"];

        Assert.Equal(
            ["--continue", "-p", "do the thing"],
            CliArgs.StripAfClaudeFlags(args));
    }

    [Fact]
    public void StripAfClaudeFlags_NoAfClaudeFlags_LeavesArgsUntouched()
    {
        string[] args = ["--continue", "-p", "mentions --select in text"];

        Assert.Equal(args, CliArgs.StripAfClaudeFlags(args));
    }
}
