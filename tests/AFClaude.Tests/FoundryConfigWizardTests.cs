using AFClaude;
using Spectre.Console;
using Spectre.Console.Testing;

namespace AFClaude.Tests;

// Shares CurrentDirectoryTestCollection with FoundryConfigFileTests: both mutate the
// process-global Directory.CurrentDirectory, so they must be serialized (see comment
// there) rather than left to xUnit's default cross-class parallelization.
[Collection(CurrentDirectoryTestCollection.Name)]
public class FoundryConfigWizardTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("afclaude-wizard-tests-").FullName;
    private readonly string _originalCwd = Directory.GetCurrentDirectory();

    public FoundryConfigWizardTests() => Directory.SetCurrentDirectory(_tempDir);

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCwd);
        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void PickSubscription_SingleSubscription_ReturnsItWithoutPrompting()
    {
        var console = new TestConsole();
        var subscriptions = new List<AzSubscription> { new("sub-1", "Only Subscription") };

        var picked = FoundryConfigWizard.PickSubscription(console, subscriptions);

        Assert.Equal("Only Subscription", picked.Name);
    }

    [Fact]
    public void PickSubscription_NoneFound_Throws()
    {
        var console = new TestConsole();

        Assert.Throws<InvalidOperationException>(
            () => FoundryConfigWizard.PickSubscription(console, []));
    }

    [Fact]
    public void PickSubscription_MultipleSubscriptions_PromptsAndReturnsSelection()
    {
        var console = new TestConsole();
        console.Interactive();
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        var subscriptions = new List<AzSubscription> { new("sub-1", "First"), new("sub-2", "Second") };

        var picked = FoundryConfigWizard.PickSubscription(console, subscriptions);

        Assert.Equal("Second", picked.Name);
    }

    [Fact]
    public void PickResource_NoneFound_Throws()
    {
        var console = new TestConsole();

        Assert.Throws<InvalidOperationException>(
            () => FoundryConfigWizard.PickResource(console, []));
    }

    [Fact]
    public void PickResource_SingleResource_ReturnsItWithoutPrompting()
    {
        var console = new TestConsole();
        var resources = new List<AzCognitiveServicesAccount>
        {
            new("qhub-infra-resource", "AIServices", "swedencentral", "rg-qhub-infra",
                new AzCognitiveServicesAccountProperties("https://qhub-infra-resource.cognitiveservices.azure.com/")),
        };

        var picked = FoundryConfigWizard.PickResource(console, resources);

        Assert.Equal("qhub-infra-resource", picked.Name);
    }

    [Fact]
    public void PickDeployment_NoneFound_Throws()
    {
        var console = new TestConsole();

        Assert.Throws<InvalidOperationException>(
            () => FoundryConfigWizard.PickDeployment(console, []));
    }

    [Fact]
    public void PickDeployment_SingleDeployment_ReturnsItWithoutPrompting()
    {
        var console = new TestConsole();
        var deployments = new List<AzDeployment>
        {
            new("gpt-4.1", new AzDeploymentProperties(new AzDeploymentModel("gpt-4.1", "2025-04-14"))),
        };

        var picked = FoundryConfigWizard.PickDeployment(console, deployments);

        Assert.Equal("gpt-4.1", picked.Name);
    }

    [Fact]
    public void OfferSave_DontSave_WritesNoFile()
    {
        var console = new TestConsole();
        console.Interactive();
        console.Input.PushKey(ConsoleKey.Enter); // first choice: "Don't save"
        var config = new FoundryConfig("https://example.com/", "gpt-4.1", "openai");

        FoundryConfigWizard.OfferSave(console, config, "afclaude.config.json");

        Assert.False(File.Exists("afclaude.config.json"));
    }

    [Fact]
    public void OfferSave_SaveToFile_WritesConfigWithChosenFileName()
    {
        var console = new TestConsole();
        console.Interactive();
        console.Input.PushKey(ConsoleKey.DownArrow); // move to "Save to file"
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("custom.json"); // accept/override the suggested filename
        var config = new FoundryConfig("https://example.com/", "gpt-4.1", "openai");

        FoundryConfigWizard.OfferSave(console, config, "afclaude.config.json");

        Assert.True(File.Exists("custom.json"));
        Assert.Equal(config, FoundryConfigFile.TryLoad("custom.json"));
    }
}
