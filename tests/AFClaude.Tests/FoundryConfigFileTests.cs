using AFClaude;

namespace AFClaude.Tests;

// Tests in this collection mutate the process-global Directory.CurrentDirectory (to
// sandbox file I/O in a temp dir), so they must not run in parallel with each other —
// xUnit parallelizes across distinct test classes by default, and two classes racing to
// set/restore CurrentDirectory nondeterministically stomp on one another's directory.
[CollectionDefinition(Name)]
public class CurrentDirectoryTestCollection
{
    public const string Name = "Mutates Directory.CurrentDirectory";
}

[Collection(CurrentDirectoryTestCollection.Name)]
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
    public void Save_WritesPascalCaseKeys()
    {
        FoundryConfigFile.Save(FoundryConfigFile.DefaultFileName, new FoundryConfig("https://example.com/", "gpt-4.1", "openai"));

        var json = File.ReadAllText(FoundryConfigFile.DefaultFileName);
        Assert.Contains("\"Endpoint\"", json);
        Assert.Contains("\"Deployment\"", json);
        Assert.Contains("\"Api\"", json);
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
