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

    // ── SuggestRole ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Sonnet", new[] { "claude-sonnet-4-6", "claude-sonnet-5", "claude-haiku-4-5" }, "claude-sonnet-5")]
    [InlineData("Haiku",  new[] { "claude-sonnet-5", "claude-haiku-4-5", "claude-opus-5" }, "claude-haiku-4-5")]
    [InlineData("Opus",   new[] { "claude-opus-4-6", "claude-opus-5" }, "claude-opus-5")]
    [InlineData("Fable",  new[] { "claude-fable-5-1", "claude-sonnet-5" }, "claude-fable-5-1")]
    [InlineData("Sonnet", new[] { "gpt-4.1", "gpt-5.6-terra" }, null)]  // no match
    public void SuggestRole_ReturnsHighestLexicographicMatchOrNull(
        string role, string[] deploymentNames, string? expected)
    {
        var result = FoundryConfigWizard.SuggestRole(role, deploymentNames);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SuggestRole_CaseInsensitive()
    {
        var result = FoundryConfigWizard.SuggestRole("SONNET", ["Claude-Sonnet-5"]);
        Assert.Equal("Claude-Sonnet-5", result);
    }

    // ── OfferConfigureModelRoles ──────────────────────────────────────────────────

    [Fact]
    public void OfferConfigureModelRoles_SingleDeployment_ReturnsNullWithoutPrompting()
    {
        var console = new TestConsole();
        var deployments = new List<AzDeployment>
        {
            new("claude-sonnet-5", new AzDeploymentProperties(new AzDeploymentModel("claude-sonnet-5", "2"))),
        };

        var result = FoundryConfigWizard.OfferConfigureModelRoles(console, deployments, "claude-sonnet-5");

        Assert.Null(result);
    }

    [Fact]
    public void OfferConfigureModelRoles_UserSkips_ReturnsNull()
    {
        var console = new TestConsole();
        console.Interactive();
        console.Input.PushKey(ConsoleKey.DownArrow); // move to "No — skip"
        console.Input.PushKey(ConsoleKey.Enter);
        var deployments = MakeDeployments("claude-sonnet-5", "claude-haiku-4-5", "claude-opus-5");

        var result = FoundryConfigWizard.OfferConfigureModelRoles(console, deployments, "claude-sonnet-5");

        Assert.Null(result);
    }

    [Fact]
    public void OfferConfigureModelRoles_UserConfigures_ReturnsMappedRoles()
    {
        var console = new TestConsole();
        console.Interactive();
        // Opt in to role configuration
        console.Input.PushKey(ConsoleKey.Enter); // "Yes — configure roles now"
        // Deployments are listed before <skip>, so pressing Enter selects the first
        // deployment for each of the 4 roles (Sonnet, Haiku, Opus, Fable).
        for (int i = 0; i < 4; i++)
        {
            console.Input.PushKey(ConsoleKey.Enter);
        }

        var deployments = MakeDeployments("claude-sonnet-5", "claude-haiku-4-5", "claude-opus-5", "claude-fable-5-1");

        var result = FoundryConfigWizard.OfferConfigureModelRoles(console, deployments, "claude-sonnet-5");

        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    private static List<AzDeployment> MakeDeployments(params string[] names)
        => names.Select(n => new AzDeployment(n, new AzDeploymentProperties(new AzDeploymentModel(n, "1")))).ToList();

    private static List<AzDeployment> MakeDeploymentsWithFormat(params (string Name, string Format)[] entries)
        => entries.Select(e => new AzDeployment(e.Name,
            new AzDeploymentProperties(new AzDeploymentModel(e.Name, "1", e.Format)))).ToList();

    // ── OfferConfigureModelNameAliases ────────────────────────────────────────────

    [Fact]
    public void OfferConfigureModelNameAliases_NoNonClaudeDeployments_ReturnsNull()
    {
        var console = new TestConsole();
        var deployments = MakeDeploymentsWithFormat(("claude-sonnet-5", "Anthropic"), ("claude-opus-5", "Anthropic"));

        var result = FoundryConfigWizard.OfferConfigureModelNameAliases(console, null, deployments);

        Assert.Null(result); // no non-Claude deployments → no prompts fired
    }

    [Fact]
    public void OfferConfigureModelNameAliases_UserSkips_ReturnsNull()
    {
        var console = new TestConsole();
        console.Interactive();
        console.Input.PushKey(ConsoleKey.DownArrow); // "No — skip"
        console.Input.PushKey(ConsoleKey.Enter);

        var deployments = MakeDeploymentsWithFormat(("claude-sonnet-5", "Anthropic"), ("gpt-6-astra", "OpenAI"));

        var result = FoundryConfigWizard.OfferConfigureModelNameAliases(console, null, deployments);

        Assert.Null(result);
    }

    [Fact]
    public void OfferConfigureModelNameAliases_UserConfigures_ReturnsMappings()
    {
        var console = new TestConsole();
        console.Interactive();
        console.Input.PushKey(ConsoleKey.Enter);  // "Yes — configure aliases"
        // Anthropic deployments listed before <no alias>; Enter picks first (claude-sonnet-5)
        console.Input.PushKey(ConsoleKey.Enter);  // select claude-sonnet-5 for gpt-6-astra

        var deployments = MakeDeploymentsWithFormat(("claude-sonnet-5", "Anthropic"), ("gpt-6-astra", "OpenAI"));

        var result = FoundryConfigWizard.OfferConfigureModelNameAliases(console, null, deployments);

        Assert.NotNull(result);
        Assert.True(result.ContainsKey("gpt-6-astra"));
    }

    [Fact]
    public void OfferConfigureModelNameAliases_AllClaudeDeployments_ReturnsNullWithoutPrompting()
    {
        // Even if ModelRoles maps Sonnet to a Claude deployment, no prompt if no non-Claude deployments exist.
        var console = new TestConsole();
        var roles = new Dictionary<string, string> { ["Sonnet"] = "claude-sonnet-5" };
        var deployments = MakeDeploymentsWithFormat(("claude-sonnet-5", "Anthropic"), ("claude-opus-5", "Anthropic"));

        var result = FoundryConfigWizard.OfferConfigureModelNameAliases(console, roles, deployments);

        Assert.Null(result);
    }

    // ── SuggestAliasFor via known equivalence table ───────────────────────────────

    [Theory]
    [InlineData("gpt-6-astra",    null, "claude-fable-5-1")]   // astra → fable tier
    [InlineData("gpt-5.6-sol",    null, "claude-opus-5")]       // sol → opus tier
    [InlineData("gpt-5.6-terra",  null, "claude-sonnet-5")]     // terra → sonnet tier
    [InlineData("gpt-5.6-luna",   null, "claude-haiku-4-5")]    // luna → haiku tier
    [InlineData("grok-4-1-fast",  null, "claude-opus-5")]       // grok → opus tier
    [InlineData("DeepSeek-V4",    null, "claude-sonnet-5")]     // deepseek → sonnet
    [InlineData("Kimi-K2",        null, "claude-sonnet-5")]     // kimi → sonnet
    [InlineData("custom-model",   "Sonnet", "claude-sonnet-5")] // fallback to role
    [InlineData("unknown-model",  null, null)]                  // no match
    public void SuggestAliasFor_KnownEquivalences_ReturnsBestMatch(
        string deploymentName, string? fallbackRole, string? expectedAlias)
    {
        var anthropicNames = new[] { "claude-sonnet-5", "claude-opus-5", "claude-haiku-4-5", "claude-fable-5-1" };

        var match = FoundryConfigWizard.SuggestAliasFor(deploymentName, fallbackRole, anthropicNames);

        Assert.Equal(expectedAlias, match);
    }
}

// ── ModelAliasConfig ──────────────────────────────────────────────────────────────

public class ModelAliasConfigTests
{
    [Fact]
    public void Resolve_KnownAlias_ReturnsAlias()
    {
        var config = ModelAliasConfig.From(new Dictionary<string, string> { ["gpt-6-astra"] = "claude-sonnet-5" });
        Assert.Equal("claude-sonnet-5", config.Resolve("gpt-6-astra"));
    }

    [Fact]
    public void Resolve_UnknownModel_ReturnsOriginal()
    {
        var config = ModelAliasConfig.From(new Dictionary<string, string> { ["gpt-6-astra"] = "claude-sonnet-5" });
        Assert.Equal("gpt-5.6-terra", config.Resolve("gpt-5.6-terra"));
    }

    [Fact]
    public void Resolve_Empty_ReturnsOriginal()
    {
        Assert.Equal("gpt-6-astra", ModelAliasConfig.Empty.Resolve("gpt-6-astra"));
    }

    [Fact]
    public void From_NullAliases_ReturnsEmpty()
    {
        var config = ModelAliasConfig.From(null);
        Assert.Same(ModelAliasConfig.Empty, config);
    }

    [Fact]
    public void From_EmptyDict_ReturnsEmpty()
    {
        var config = ModelAliasConfig.From(new Dictionary<string, string>());
        Assert.Same(ModelAliasConfig.Empty, config);
    }

    [Fact]
    public void Resolve_CaseInsensitive()
    {
        var config = ModelAliasConfig.From(new Dictionary<string, string> { ["GPT-6-Astra"] = "claude-sonnet-5" });
        // Alias lookup should be case-insensitive if configured that way — depends on dict comparer.
        // Default StringComparer.OrdinalIgnoreCase used by ModelAliasConfig.From with DI config.
        // Here the key casing must match since FoundryConfig uses Dictionary<string,string> from JSON.
        Assert.Equal("claude-sonnet-5", config.Resolve("GPT-6-Astra"));
    }
}

// ── AutoCompactWindowTests ────────────────────────────────────────────────────────

public class AutoCompactWindowTests
{
    [Theory]
    [InlineData("claude-sonnet-5", true)]
    [InlineData("claude-opus-5", true)]
    [InlineData("claude-opus-4-8", true)]
    [InlineData("claude-opus-4-7", true)]
    [InlineData("claude-fable-5-1", true)]
    [InlineData("claude-haiku-4-5", false)]
    [InlineData("claude-sonnet-4-6", false)]
    [InlineData("gpt-4o-mini", false)]
    [InlineData(null, false)]
    public void IsLongContextModel_Identifies1MModels(string? model, bool expected)
    {
        Assert.Equal(expected, LaunchEnvironment.IsLongContextModel(model));
    }

    [Fact]
    public void ApplyAutoCompactWindow_1MModel_Sets900000ByDefault()
    {
        var psi = new System.Diagnostics.ProcessStartInfo();
        LaunchEnvironment.ApplyAutoCompactWindow(psi, null, "claude-sonnet-5");
        Assert.Equal("900000", psi.Environment["CLAUDE_CODE_AUTO_COMPACT_WINDOW"]);
    }

    [Fact]
    public void ApplyAutoCompactWindow_1MRole_Sets900000()
    {
        var psi = new System.Diagnostics.ProcessStartInfo();
        var config = new FoundryConfig("https://example/", "other", "anthropic",
            ModelRoles: new Dictionary<string, string> { ["Sonnet"] = "claude-sonnet-5" });
        LaunchEnvironment.ApplyAutoCompactWindow(psi, config, "other");
        Assert.Equal("900000", psi.Environment["CLAUDE_CODE_AUTO_COMPACT_WINDOW"]);
    }

    [Fact]
    public void ApplyAutoCompactWindow_1MAlias_Sets900000()
    {
        var psi = new System.Diagnostics.ProcessStartInfo();
        var config = new FoundryConfig("https://example/", "gpt-6-astra", "openai",
            ModelNameAliases: new Dictionary<string, string> { ["gpt-6-astra"] = "claude-fable-5-1" });
        LaunchEnvironment.ApplyAutoCompactWindow(psi, config, "gpt-6-astra");
        Assert.Equal("900000", psi.Environment["CLAUDE_CODE_AUTO_COMPACT_WINDOW"]);
    }

    [Fact]
    public void ApplyAutoCompactWindow_ExplicitConfigWindow_TakesPrecedence()
    {
        var psi = new System.Diagnostics.ProcessStartInfo();
        var config = new FoundryConfig("https://example/", "claude-sonnet-5", "anthropic",
            AutoCompactWindow: 800000);
        LaunchEnvironment.ApplyAutoCompactWindow(psi, config, "claude-sonnet-5");
        Assert.Equal("800000", psi.Environment["CLAUDE_CODE_AUTO_COMPACT_WINDOW"]);
    }

    [Fact]
    public void ApplyAutoCompactWindow_Non1MModel_DoesNotSet()
    {
        var psi = new System.Diagnostics.ProcessStartInfo();
        LaunchEnvironment.ApplyAutoCompactWindow(psi, null, "claude-haiku-4-5");
        Assert.False(psi.Environment.ContainsKey("CLAUDE_CODE_AUTO_COMPACT_WINDOW"));
    }
}
