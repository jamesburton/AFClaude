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

        // Model role aliases: map Claude Code role names → deployment names on this resource.
        // Auto-suggested by name pattern (e.g. "claude-sonnet-5" → Sonnet role).
        var modelRoles = OfferConfigureModelRoles(console, deployments, deployment.Name);

        // Model name aliases: for any non-Claude deployments used in model roles, offer to
        // rewrite the model name in bridge responses so Claude Code applies the right
        // context window (e.g. "gpt-6-astra" → "claude-sonnet-5" for 1M treatment).
        var modelNameAliases = OfferConfigureModelNameAliases(console, modelRoles, deployments);

        var config = new FoundryConfig(resource.Endpoint, deployment.Name, api, maxTokensParam, modelRoles, modelNameAliases);
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

    // Offers to assign Claude Code role aliases (Sonnet/Haiku/Opus/Fable) to deployments
    // on the same resource. Auto-suggests by name pattern; user can override each or skip
    // the whole step. Returns null when skipped (no ModelRoles in saved config).
    // Exposed internal so tests can drive it with a TestConsole.
    internal static Dictionary<string, string>? OfferConfigureModelRoles(
        IAnsiConsole console, IReadOnlyList<AzDeployment> deployments, string primaryDeploymentName)
    {
        // Only makes sense when there is more than one deployment to choose from.
        if (deployments.Count <= 1) return null;

        var configure = console.Prompt(
            new SelectionPrompt<string>()
                .Title("Configure model role aliases for Claude Code? ([grey]Sonnet/Haiku/Opus/Fable → deployment[/])")
                .AddChoices("Yes — configure roles now", "No — skip"));

        if (configure.StartsWith("No")) return null;

        var roles = new[] { "Sonnet", "Haiku", "Opus", "Fable" };
        var deploymentNames = deployments.Select(d => d.Name).ToList();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        console.MarkupLine("[grey]Select a deployment for each Claude Code role. Choose [dim]<skip>[/] to leave a role unset.[/]");

        foreach (var role in roles)
        {
            var suggested = SuggestRole(role, deploymentNames) ?? primaryDeploymentName;
            var choices = deploymentNames.Concat(new[] { "<skip>" }).ToList();

            var picked = console.Prompt(
                new SelectionPrompt<string>()
                    .Title($"  [bold]{role}[/] role ([grey]ANTHROPIC_DEFAULT_{role.ToUpperInvariant()}_MODEL[/]):")
                    .AddChoices(choices)
                    .HighlightStyle("green")
                    .UseConverter(c => c == "<skip>" ? "[grey]<skip — leave unset>[/]" :
                        c == suggested ? $"{c} [grey](suggested)[/]" : c));

            if (picked != "<skip>")
            {
                result[role] = picked;
            }
        }

        return result.Count > 0 ? result : null;
    }

    // Pure name-pattern matching: returns the deployment name that best matches a role,
    // or null if nothing is a clear match. Testable without a console or az CLI.
    internal static string? SuggestRole(string role, IReadOnlyList<string> deploymentNames)
    {
        var pattern = role.ToLowerInvariant(); // "sonnet", "haiku", "opus", "fable"
        // Prefer an exact substring match, picking the one with the highest version number
        // (last lexicographically among matches — e.g. "claude-sonnet-5" beats "claude-sonnet-4-6").
        var matches = deploymentNames
            .Where(n => n.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return matches.FirstOrDefault();
    }

    // Offers response model name aliases for ALL non-Claude deployments on the resource —
    // not just those in ModelRoles — so in-session '/model gpt-6-astra' switching also
    // benefits. Each alias rewrites the model field in bridge responses so Claude Code
    // applies a known context window instead of its conservative unknown-model default.
    // Auto-suggests using known OpenAI→Claude equivalence tiers (astra→fable, sol→opus,
    // terra→sonnet, luna→haiku) then falls back to the Claude Code role name pattern.
    // Exposed internal so tests can drive it with a TestConsole.
    internal static Dictionary<string, string>? OfferConfigureModelNameAliases(
        IAnsiConsole console,
        Dictionary<string, string>? modelRoles,
        IReadOnlyList<AzDeployment> deployments)
    {
        // Operate on ALL non-Claude deployments on the resource, not just role-mapped ones.
        var nonClaudeDeployments = deployments
            .Where(d => !IsAnthropicDeployment(d))
            .ToList();

        if (nonClaudeDeployments.Count == 0) return null;

        var configure = console.Prompt(
            new SelectionPrompt<string>()
                .Title("Configure response model name aliases? ([grey]Lets Claude Code apply correct context window limits to non-Claude deployments[/])")
                .AddChoices("Yes — configure aliases", "No — skip"));

        if (configure.StartsWith("No")) return null;

        var anthropicDeployments = deployments.Where(IsAnthropicDeployment).ToList();
        var anthropicNames = anthropicDeployments.Select(d => d.Name).ToList();

        if (anthropicNames.Count == 0)
        {
            console.MarkupLine("[yellow]No Claude deployments found on this resource to use as alias targets — skipping.[/]");
            return null;
        }

        // Determine what role (if any) each non-Claude deployment fills in ModelRoles,
        // for use as the fallback suggestion when no name-pattern match is found.
        var roleByDeployment = (modelRoles ?? new Dictionary<string, string>())
            .ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        console.MarkupLine("[grey]Select which Claude model each deployment should appear as in responses.[/]");
        console.MarkupLine("[grey]Note: claude-haiku-4-5 has a 200K context window (not 1M).[/]");

        foreach (var dep in nonClaudeDeployments)
        {
            var fallbackRole = roleByDeployment.TryGetValue(dep.Name, out var r) ? r : null;
            var suggested = SuggestAliasFor(dep.Name, fallbackRole, anthropicNames);
            var choices = anthropicNames.Concat(new[] { "<no alias>" }).ToList();

            var picked = console.Prompt(
                new SelectionPrompt<string>()
                    .Title($"  [bold]{dep.Name}[/] → appear as:")
                    .AddChoices(choices)
                    .UseConverter(c => c == "<no alias>" ? "[grey]<no alias — Claude Code uses actual model name>[/]" :
                        c == suggested ? $"{c} [grey](suggested)[/]" : c));

            if (picked != "<no alias>")
            {
                result[dep.Name] = picked;
            }
        }

        return result.Count > 0 ? result : null;
    }

    // Known equivalences between OpenAI model families and Claude capability tiers.
    // Used to auto-suggest the best alias target for a non-Claude deployment.
    // astra  (OpenAI frontier / agent flagship) → fable  (Claude frontier)
    // sol    (OpenAI extended reasoning)         → opus   (Claude heavy reasoning)
    // terra  (OpenAI balanced coding)            → sonnet (Claude balanced)
    // luna   (OpenAI fast / light)               → haiku  (Claude fast — 200K, not 1M)
    // grok   (xAI reasoning)                     → opus   (closest Claude reasoning tier)
    // kimi/deepseek (third-party balanced)       → sonnet (balanced tier)
    private static readonly (string Keyword, string ClaudeRole)[] DeploymentEquivalences =
    [
        ("astra",    "fable"),
        ("sol",      "opus"),
        ("terra",    "sonnet"),
        ("luna",     "haiku"),
        ("grok",     "opus"),
        ("deepseek", "sonnet"),
        ("kimi",     "sonnet"),
    ];

    // Returns the best Anthropic alias for an OpenAI deployment name, trying known
    // equivalence patterns first, then the Claude Code role name, then null.
    // Internal for direct unit testing of equivalence matching.
    internal static string? SuggestAliasFor(string deploymentName, string? fallbackRole,
        IReadOnlyList<string> anthropicNames)
    {
        foreach (var (keyword, claudeRole) in DeploymentEquivalences)
        {
            if (deploymentName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                var match = SuggestRole(claudeRole, anthropicNames);
                if (match is not null) return match;
            }
        }
        return fallbackRole is not null ? SuggestRole(fallbackRole, anthropicNames) : null;
    }

    // True when a deployment is confirmed Anthropic-format (from the Format field in
    // the az CLI response), or when Format is absent but the name contains "claude"
    // (fallback for older az output or hand-crafted test data).
    private static bool IsAnthropicDeployment(AzDeployment d) =>
        d.Properties.Model.Format is string fmt
            ? fmt.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
            : d.Name.Contains("claude", StringComparison.OrdinalIgnoreCase);

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
