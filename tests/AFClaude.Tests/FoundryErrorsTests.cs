using System.ClientModel;
using System.ClientModel.Primitives;
using Azure;
using Azure.Core;
using Azure.Identity;
using AFClaude;

namespace AFClaude.Tests;

public class FoundryErrorsTests
{
    [Fact]
    public void CredentialUnavailable_PointsAtInstallAndLogin()
    {
        var message = FoundryErrors.Describe(new CredentialUnavailableException("no az"));

        Assert.Contains("Install the Azure CLI", message);
        Assert.True(FoundryErrors.IsAuthFailure(new CredentialUnavailableException("no az")));
    }

    [Fact]
    public void CliTimeout_IsNotBlamedOnExpiredSession()
    {
        // Azure.Identity's actual message when ProcessTimeout is exceeded.
        var ex = new AuthenticationFailedException("Azure CLI authentication timed out.");

        var message = FoundryErrors.Describe(ex);

        Assert.Contains("Foundry__CliTimeoutSeconds", message);
        Assert.Contains("not expired credentials", message);
        Assert.True(FoundryErrors.IsAuthFailure(ex));
    }

    [Fact]
    public void OtherAuthenticationFailure_MentionsExpiryAndTenant()
    {
        var message = FoundryErrors.Describe(
            new AuthenticationFailedException("AADSTS700082: The refresh token has expired."));

        Assert.Contains("expired", message);
        Assert.Contains("--tenant", message);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void ServiceUnauthorized_PointsAtDataPlaneRbac(int status)
    {
        var ex = new RequestFailedException(status, "AuthorizationFailed");

        var message = FoundryErrors.Describe(ex);

        Assert.Contains("Cognitive Services OpenAI User", message);
        Assert.Contains("Owner alone is not enough", message);
        Assert.True(FoundryErrors.IsAuthFailure(ex));
    }

    [Fact]
    public void ClientResultUnauthorized_PointsAtDataPlaneRbac()
    {
        var ex = new ClientResultException("PermissionDenied", new FakeResponse(401), null!);

        Assert.Contains("Cognitive Services OpenAI User", FoundryErrors.Describe(ex));
        Assert.True(FoundryErrors.IsAuthFailure(ex));
    }

    [Fact]
    public void ServerError_StaysGenericAndIsNotAuth()
    {
        var ex = new ClientResultException("boom", new FakeResponse(500), null!);

        Assert.Contains("Check the server logs", FoundryErrors.Describe(ex));
        Assert.False(FoundryErrors.IsAuthFailure(ex));
    }

    [Fact]
    public async Task CachingTokenCredential_AcquiresOnceWhileFresh()
    {
        var inner = new CountingCredential(expiresIn: TimeSpan.FromHours(1));
        var cached = new CachingTokenCredential(inner);
        var context = new TokenRequestContext([FoundryClientFactory.TokenScope]);

        await cached.GetTokenAsync(context, CancellationToken.None);
        await cached.GetTokenAsync(context, CancellationToken.None);
        cached.GetToken(context, CancellationToken.None);

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task CachingTokenCredential_RefreshesNearExpiry()
    {
        // Expires inside the 5-minute refresh margin, so every call re-acquires.
        var inner = new CountingCredential(expiresIn: TimeSpan.FromMinutes(1));
        var cached = new CachingTokenCredential(inner);
        var context = new TokenRequestContext([FoundryClientFactory.TokenScope]);

        await cached.GetTokenAsync(context, CancellationToken.None);
        await cached.GetTokenAsync(context, CancellationToken.None);

        Assert.Equal(2, inner.Calls);
    }

    private sealed class CountingCredential(TimeSpan expiresIn) : TokenCredential
    {
        public int Calls;

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            return new AccessToken("token", DateTimeOffset.UtcNow + expiresIn);
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(GetToken(requestContext, cancellationToken));
    }

    private sealed class FakeResponse(int status) : PipelineResponse
    {
        public override int Status => status;
        public override string ReasonPhrase => string.Empty;
        public override Stream? ContentStream { get; set; }
        public override BinaryData Content => BinaryData.FromString(string.Empty);
        protected override PipelineResponseHeaders HeadersCore => new FakeHeaders();
        public override void Dispose() { }
        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;
        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => new(Content);
    }

    private sealed class FakeHeaders : PipelineResponseHeaders
    {
        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator()
            => Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();
        public override bool TryGetValue(string name, out string? value) { value = null; return false; }
        public override bool TryGetValues(string name, out IEnumerable<string>? values) { values = null; return false; }
    }
}
