using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using OpenAI.Chat;

namespace AFClaude;

internal sealed record FoundryClient(
    ChatClient ChatClient,
    FoundryAnthropicClient Anthropic,
    FoundryApiResolver Api,
    string Deployment,
    TokenCredential Credential,
    MaxTokensParamResolver MaxTokensParam,
    string? ConfigFilePath);

internal static class FoundryClientFactory
{
    // Azure OpenAI data-plane scope — used by the launch-mode token warm-up. The
    // AzureOpenAIClient pipeline requests the same scope internally.
    public const string TokenScope = "https://cognitiveservices.azure.com/.default";

    private static readonly HttpClient SharedHttp = new();

    public static FoundryClient Create(IConfiguration configuration)
    {
        var endpointValue = configuration["Foundry:Endpoint"];
        var rawDeployment = configuration["Foundry:Deployment"];

        if (string.IsNullOrWhiteSpace(endpointValue) || !Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException(
                "Missing or invalid configuration 'Foundry:Endpoint'. Set the Foundry__Endpoint environment variable " +
                "to the Azure OpenAI/Foundry resource endpoint, e.g. https://<resource>.openai.azure.com/");
        }

        if (string.IsNullOrWhiteSpace(rawDeployment))
        {
            throw new InvalidOperationException(
                "Missing configuration 'Foundry:Deployment'. Set the Foundry__Deployment environment variable " +
                "to the target deployment name.");
        }

        var deployment = LaunchEnvironment.Strip1mSuffix(rawDeployment.Trim());

        // Foundry serves different model families on different API surfaces: GPT-family
        // deployments on the Azure-OpenAI chat-completions route, Claude deployments on
        // a native Anthropic Messages route. auto (default) probes once and prefers the
        // native passthrough; the explicit values skip probing. The OpenAI path also
        // stays available for future OpenAI-only hosts (e.g. Ollama).
        var apiValue = configuration["Foundry:Api"];
        FoundryApi? configuredApi = apiValue?.Trim().ToLowerInvariant() switch
        {
            null or "" or "auto" => null,
            "anthropic" or "claude" => FoundryApi.Anthropic,
            "openai" or "azure-openai" => FoundryApi.OpenAI,
            _ => throw new InvalidOperationException(
                $"Invalid configuration 'Foundry:Api' value '{apiValue}'. Use 'auto' (default), 'anthropic', or 'openai'."),
        };

        // AzureCliCredential's default ProcessTimeout is 13s, but a cold `az` start can
        // take 14-24s on a loaded machine — which then surfaces as a bogus auth failure.
        var timeoutSeconds = configuration.GetValue<int?>("Foundry:CliTimeoutSeconds") ?? 60;
        var credential = new CachingTokenCredential(new AzureCliCredential(new AzureCliCredentialOptions
        {
            ProcessTimeout = TimeSpan.FromSeconds(timeoutSeconds),
        }));

        // Endpoint must end with a slash so relative paths append instead of replacing.
        if (!endpoint.AbsoluteUri.EndsWith('/'))
        {
            endpoint = new Uri(endpoint.AbsoluteUri + "/");
        }

        // anthropic-beta policy for the passthrough: strip (default — Foundry 400s on
        // beta flags it doesn't recognise), passthrough, or a literal replacement list.
        var betaMode = configuration["Foundry:AnthropicBeta"];
        betaMode = string.IsNullOrWhiteSpace(betaMode) ? FoundryAnthropicClient.BetaStrip : betaMode.Trim();

        // Body policy: strict (default — Foundry also 400s on beta-gated top-level
        // body fields like context_management) or passthrough.
        var bodyMode = configuration["Foundry:AnthropicBody"];
        bodyMode = string.IsNullOrWhiteSpace(bodyMode) ? FoundryAnthropicClient.BodyStrict : bodyMode.Trim();

        // The Azure OpenAI SDK silently rewrites the modern `max_completion_tokens`
        // field back to the legacy `max_tokens` before every request unless told
        // otherwise (AzureChatClient.RefreshMaxTokenSerialization) -- most Azure
        // deployments still expect the legacy name, but some newer/non-GPT-family
        // deployments (observed live: gpt-5.6) reject `max_tokens` outright ("Use
        // 'max_completion_tokens' instead"). There's no reliable way to know which in
        // advance, so auto (default) starts optimistic with legacy and self-heals the
        // first time a request is rejected for exactly this reason (see
        // MaxTokensParamResolver); legacy/new pin the behaviour explicitly and skip
        // self-healing entirely.
        var maxTokensParamValue = configuration["Foundry:MaxTokensParam"]?.Trim().ToLowerInvariant();
        bool? configuredUseMaxCompletionTokens = maxTokensParamValue switch
        {
            null or "" or "auto" => null,
            "legacy" or "max_tokens" => false,
            "new" or "max_completion_tokens" => true,
            _ => throw new InvalidOperationException(
                $"Invalid configuration 'Foundry:MaxTokensParam' value '{maxTokensParamValue}'. Use 'auto' (default), 'legacy', or 'new'."),
        };

        // Set only when a saved config file is backing this run (loaded from disk, or
        // just written by the wizard) -- lets a successful self-heal patch that same
        // file so future runs skip the retry. Absent for pure env-var invocations.
        var configFilePath = configuration["Foundry:ConfigFilePath"];

        var azureClient = new AzureOpenAIClient(endpoint, credential);
        var anthropic = new FoundryAnthropicClient(SharedHttp, endpoint, deployment, credential, betaMode, bodyMode);
        var resolver = new FoundryApiResolver(configuredApi, anthropic.ProbeAsync);
        var maxTokensParam = new MaxTokensParamResolver(configuredUseMaxCompletionTokens);
        return new FoundryClient(azureClient.GetChatClient(deployment), anthropic, resolver, deployment, credential, maxTokensParam, configFilePath);
    }
}

// AzureCliCredential spawns an `az` process on EVERY GetToken call and never caches,
// so each token acquisition pays the full az startup cost. Caching in-process means a
// proxy instance pays it once per token lifetime, and launch mode's warm-up call
// benefits the request pipeline afterwards. Scope differences are ignored — this
// process only ever requests the single Azure OpenAI data-plane scope.
internal sealed class CachingTokenCredential(TokenCredential inner) : TokenCredential
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AccessToken _token;

    // Add the margin to "now" rather than subtracting from ExpiresOn: the default
    // (never-acquired) token has ExpiresOn == DateTimeOffset.MinValue, which underflows.
    private bool IsFresh => _token.ExpiresOn > DateTimeOffset.UtcNow + RefreshMargin;

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        if (IsFresh)
        {
            return _token;
        }

        _gate.Wait(cancellationToken);
        try
        {
            if (!IsFresh)
            {
                _token = inner.GetToken(requestContext, cancellationToken);
            }
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        if (IsFresh)
        {
            return _token;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsFresh)
            {
                _token = await inner.GetTokenAsync(requestContext, cancellationToken);
            }
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }
}
