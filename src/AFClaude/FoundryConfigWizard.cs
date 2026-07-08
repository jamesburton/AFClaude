using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace AFClaude;

// Interactive subscription -> resource -> deployment picker for launch/--http modes.
// Only invoked (see Program.cs's ResolveFoundryConfigOverridesAsync) when
// Foundry__Endpoint/Foundry__Deployment aren't already resolvable some other way and a
// real terminal is attached. Selection/save logic is split out as testable pure
// functions taking an IAnsiConsole, so tests drive them with Spectre.Console.Testing's
// TestConsole instead of a real terminal; only the az-calling steps need the real thing.
internal static class FoundryConfigWizard
{
    public static async Task<FoundryConfig> RunAsync(
        int azTimeoutSeconds, string suggestedSaveFileName, CancellationToken cancellationToken)
    {
        var console = AnsiConsole.Console;

        var subscriptions = await AzCli.ListSubscriptionsAsync(azTimeoutSeconds, cancellationToken);
        var subscription = PickSubscription(console, subscriptions);

        var resources = await AzCli.ListCognitiveServicesAccountsAsync(subscription.Id, azTimeoutSeconds, cancellationToken);
        var resource = PickResource(console, resources);

        var deployments = await AzCli.ListDeploymentsAsync(resource.ResourceGroup, resource.Name, azTimeoutSeconds, cancellationToken);
        var deployment = PickDeployment(console, deployments);

        console.MarkupLine("Probing which API surface this deployment answers on...");
        var api = await ProbeApiAsync(resource.Endpoint, deployment.Name, cancellationToken);
        var config = new FoundryConfig(resource.Endpoint, deployment.Name, api);
        console.MarkupLine(api == "anthropic"
            ? "[green]Detected native Anthropic (Claude) deployment.[/]"
            : "[green]Detected OpenAI-compatible deployment.[/]");

        OfferSave(console, config, suggestedSaveFileName);
        return config;
    }

    internal static AzSubscription PickSubscription(IAnsiConsole console, IReadOnlyList<AzSubscription> subscriptions)
    {
        if (subscriptions.Count == 0)
        {
            throw new InvalidOperationException("No Azure subscriptions found for the logged-in 'az' account.");
        }
        if (subscriptions.Count == 1)
        {
            return subscriptions[0];
        }
        return console.Prompt(
            new SelectionPrompt<AzSubscription>()
                .Title("Select an Azure subscription:")
                .UseConverter(s => s.Name)
                .AddChoices(subscriptions));
    }

    internal static AzCognitiveServicesAccount PickResource(IAnsiConsole console, IReadOnlyList<AzCognitiveServicesAccount> resources)
    {
        if (resources.Count == 0)
        {
            throw new InvalidOperationException("No AIServices/OpenAI Cognitive Services resources found in that subscription.");
        }
        if (resources.Count == 1)
        {
            return resources[0];
        }
        return console.Prompt(
            new SelectionPrompt<AzCognitiveServicesAccount>()
                .Title("Select a Foundry/OpenAI resource:")
                .UseConverter(a => $"{a.Name} ({a.Location}, {a.ResourceGroup})")
                .AddChoices(resources));
    }

    internal static AzDeployment PickDeployment(IAnsiConsole console, IReadOnlyList<AzDeployment> deployments)
    {
        if (deployments.Count == 0)
        {
            throw new InvalidOperationException("No model deployments found on that resource.");
        }
        if (deployments.Count == 1)
        {
            return deployments[0];
        }
        return console.Prompt(
            new SelectionPrompt<AzDeployment>()
                .Title("Select a model deployment:")
                .UseConverter(d => $"{d.Name} -> {d.ModelName}/{d.ModelVersion}")
                .AddChoices(deployments));
    }

    internal static void OfferSave(IAnsiConsole console, FoundryConfig config, string suggestedFileName)
    {
        const string DontSave = "Don't save";
        const string SaveToFile = "Save to file";

        var choice = console.Prompt(
            new SelectionPrompt<string>()
                .Title("Save this configuration for future runs?")
                .AddChoices(DontSave, SaveToFile));

        if (choice == DontSave)
        {
            return;
        }

        var fileName = console.Prompt(
            new TextPrompt<string>("Save as:").DefaultValue(suggestedFileName));
        FoundryConfigFile.Save(fileName, config);
        console.MarkupLine($"[green]Saved to {fileName}.[/]");
    }

    // Reuses FoundryClientFactory.Create + the existing FoundryApiResolver rather than
    // reimplementing the probe: exercises the same code path launch mode's warm-up does.
    private static async Task<string> ProbeApiAsync(string endpoint, string deployment, CancellationToken cancellationToken)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Foundry:Endpoint"] = endpoint,
                ["Foundry:Deployment"] = deployment,
                ["Foundry:Api"] = "auto",
            })
            .Build();
        var foundry = FoundryClientFactory.Create(configuration);
        var api = await foundry.Api.ResolveAsync(cancellationToken);
        return api == FoundryApi.Anthropic ? "anthropic" : "openai";
    }
}
