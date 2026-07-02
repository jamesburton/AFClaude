using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using OpenAI.Chat;

namespace AFClaude;

internal sealed record FoundryClient(ChatClient ChatClient, string Deployment, TokenCredential Credential);

internal static class FoundryClientFactory
{
    // Azure OpenAI data-plane scope — used by the launch-mode token warm-up. The
    // AzureOpenAIClient pipeline requests the same scope internally.
    public const string TokenScope = "https://cognitiveservices.azure.com/.default";

    public static FoundryClient Create(IConfiguration configuration)
    {
        var endpointValue = configuration["Foundry:Endpoint"];
        var deployment = configuration["Foundry:Deployment"];

        if (string.IsNullOrWhiteSpace(endpointValue) || !Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException(
                "Missing or invalid configuration 'Foundry:Endpoint'. Set the Foundry__Endpoint environment variable " +
                "to the Azure OpenAI/Foundry resource endpoint, e.g. https://<resource>.openai.azure.com/");
        }

        if (string.IsNullOrWhiteSpace(deployment))
        {
            throw new InvalidOperationException(
                "Missing configuration 'Foundry:Deployment'. Set the Foundry__Deployment environment variable " +
                "to the target deployment name.");
        }

        // AzureCliCredential's default ProcessTimeout is 13s, but a cold `az` start can
        // take 14-24s on a loaded machine — which then surfaces as a bogus auth failure.
        var timeoutSeconds = configuration.GetValue<int?>("Foundry:CliTimeoutSeconds") ?? 60;
        var credential = new CachingTokenCredential(new AzureCliCredential(new AzureCliCredentialOptions
        {
            ProcessTimeout = TimeSpan.FromSeconds(timeoutSeconds),
        }));

        var azureClient = new AzureOpenAIClient(endpoint, credential);
        return new FoundryClient(azureClient.GetChatClient(deployment), deployment, credential);
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
