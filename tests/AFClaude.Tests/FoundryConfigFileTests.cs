using AFClaude;

namespace AFClaude.Tests;

public class FoundryConfigFileTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("afclaude-config-tests-").FullName;
    private readonly string _originalCwd = Directory.GetCurrentDirectory();

    public FoundryConfigFileTests() => Directory.SetCurrentDirectory(_tempDir);

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCwd);
        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void TryLoad_DefaultFileMissing_ReturnsNull()
    {
        Assert.Null(FoundryConfigFile.TryLoad(explicitPath: null));
    }

    [Fact]
    public void TryLoad_ExplicitFileMissing_ThrowsMissingConfig()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FoundryConfigFile.TryLoad("does-not-exist.json"));

        Assert.Equal("Missing Config does-not-exist.json", ex.Message);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var config = new FoundryConfig(
            "https://my-resource.cognitiveservices.azure.com/", "gpt-4.1", "openai");

        FoundryConfigFile.Save(FoundryConfigFile.DefaultFileName, config);
        var loaded = FoundryConfigFile.TryLoad(explicitPath: null);

        Assert.Equal(config, loaded);
    }

    [Fact]
    public void TryLoad_EmptyFile_ThrowsInvalidOperationException()
    {
        File.WriteAllText(FoundryConfigFile.DefaultFileName, "");

        Assert.Throws<InvalidOperationException>(() => FoundryConfigFile.TryLoad(explicitPath: null));
    }

    [Fact]
    public void TryLoad_ExplicitPath_LoadsThatFileNotTheDefault()
    {
        var config = new FoundryConfig("https://explicit.example.com/", "explicit-deployment", "anthropic");
        FoundryConfigFile.Save("custom.json", config);

        var loaded = FoundryConfigFile.TryLoad("custom.json");

        Assert.Equal(config, loaded);
    }
}
