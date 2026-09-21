using Microsoft.Extensions.Configuration;
using OpenAI.Chat;
using Spectre.Console;

namespace AFClaude;

// The wizard's outcome: the resolved config, and where (if anywhere) it was saved --
// callers need the path to know where a later runtime self-heal (MaxTokensParamResolver)
// should persist to.
internal sealed record FoundryWizardResult(FoundryConfig Config, string? SavedPath);

// Interactive subscription -> resource -> deployment picker for launch/--http modes.
// Only invoked (see Program.cs's ResolveFoundryConfigOverridesAsync) when
// Foundry__Endpoint/Foundry__Deployment aren't already resolvable some other way and a
// real terminal is attached. Selection/save logic is split out as testable pure
// functions taking an IAnsiConsole, so tests drive them with Spectre.Console.Testing's
// TestConsole instead of a real terminal; only the az-calling steps need the real thing.
internal static class FoundryConfigWizard
{
    public static async Task<FoundryWizardResult> RunAsync(
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
        var (api, probeClient) = await ProbeApiAsync(resource.Endpoint, deployment.Name, cancellationToken);
        console.MarkupLine(api == "anthropic"
            ? "[green]Detected native Anthropic (Claude) deployment.[/]"
            : "[green]Detected OpenAI-compatible deployment.[/]");

        // MaxTokensParam only matters on the OpenAI bridge -- the native Anthropic
        // passthrough never touches ChatCompletionOptions at all.
        var maxTokensParam = "auto";
        if (api == "openai")
        {
            console.MarkupLine("Checking whether this deployment needs 'max_completion_tokens' instead of 'max_tokens'...");
            maxTokensParam = await ProbeMaxTokensParamAsync(console, probeClient.ChatClient, cancellationToken);
        }

        var config = new FoundryConfig(resource.Endpoint, deployment.Name, api, maxTokensParam);
        var savedPath = OfferSave(console, config, suggestedSaveFileName);
        return new FoundryWizardResult(config, savedPath);
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

    internal static string? OfferSave(IAnsiConsole console, FoundryConfig config, string suggestedFileName)
    {
        const string DontSave = "Don't save";
        const string SaveToFile = "Save to file";

        var choice = console.Prompt(
            new SelectionPrompt<string>()
                .Title("Save this configuration for future runs?")
                .AddChoices(DontSave, SaveToFile));

        if (choice == DontSave)
        {
            return null;
        }

        var fileName = console.Prompt(
            new TextPrompt<string>("Save as:").DefaultValue(suggestedFileName));
        FoundryConfigFile.Save(fileName, config);
        console.MarkupLine($"[green]Saved to {fileName}.[/]");
        return fileName;
    }

    // Reuses FoundryClientFactory.Create + the existing FoundryApiResolver rather than
    // reimplementing the probe: exercises the same code path launch mode's warm-up does.
    // Returns the constructed client too, so ProbeMaxTokensParamAsync can reuse its
    // ChatClient instead of building a second one for the same deployment.
    private static async Task<(string Api, FoundryClient Client)> ProbeApiAsync(
        string endpoint, string deployment, CancellationToken cancellationToken)
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
        return (api == FoundryApi.Anthropic ? "anthropic" : "openai", foundry);
    }

    // A single real request settles it: Azure's rejection message for this ("Use
    // 'max_completion_tokens' instead") is authoritative, so there's no need to also
    // verify the modern field works before trusting it -- that would cost a second
    // live round trip for no real gain in confidence. Otherwise the answer is "auto",
    // not "legacy": auto behaves identically (legacy first) but can still self-heal if
    // the deployment's model is upgraded later. The probe is only an optimization, so
    // any other failure (429, quota, content filter...) also falls back to auto rather
    // than aborting a wizard the operator has already clicked through.
    private static async Task<string> ProbeMaxTokensParamAsync(
        IAnsiConsole console, ChatClient chatClient, CancellationToken cancellationToken)
    {
        try
        {
            await chatClient.CompleteChatAsync(
                [new UserChatMessage("ping")], new ChatCompletionOptions { MaxOutputTokenCount = 1 }, cancellationToken);
            console.MarkupLine("[green]This deployment accepts the standard max_tokens field.[/]");
            return "auto";
        }
        catch (Exception ex) when (MaxTokensParamResolver.IsMaxTokensRejected(ex))
        {
            console.MarkupLine("[green]This deployment requires the modern max_completion_tokens field.[/]");
            return "new";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            console.MarkupLine($"[yellow]Couldn't check ({Markup.Escape(ex.Message.Split('\n')[0])}); it'll be detected on first use instead.[/]");
            return "auto";
        }
    }
}
