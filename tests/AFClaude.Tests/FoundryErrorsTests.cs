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

    // Error-surface parity: every surface derives status + wire-shape type + message
    // from Classify, so the same failure looks consistent everywhere.
    [Theory]
    [InlineData(400, 400, "invalid_request_error", "invalid_request_error")]
    [InlineData(403, 403, "permission_error", "permission_error")]
    [InlineData(404, 404, "not_found_error", "invalid_request_error")]
    [InlineData(413, 413, "request_too_large", "invalid_request_error")]
    [InlineData(429, 429, "rate_limit_error", "rate_limit_error")]
    [InlineData(503, 500, "api_error", "server_error")]
    public void Classify_MapsUpstreamStatusToBothWireShapes(
        int upstream, int expectedStatus, string anthropicType, string openAiType)
    {
        var info = FoundryErrors.Classify(new RequestFailedException(upstream, "boom"));

        Assert.Equal(expectedStatus, info.Status);
        Assert.Equal(anthropicType, info.AnthropicType);
        Assert.Equal(openAiType, info.OpenAiType);
        Assert.NotEmpty(info.Message);
    }

    [Fact]
    public void Classify_UpstreamDetailIsSurfacedForClientErrors()
    {
        var info = FoundryErrors.Classify(new RequestFailedException(400, "max_tokens exceeds model limit"));

        Assert.Contains("max_tokens exceeds model limit", info.Message);
    }

    [Fact]
    public void Classify_NotFound_PointsAtConfiguration()
    {
        var info = FoundryErrors.Classify(new RequestFailedException(404, "DeploymentNotFound"));

        Assert.Contains("Foundry__Deployment", info.Message);
    }

    [Fact]
    public void Classify_CredentialFailures_Are401WithClassifiedMessage()
    {
        var info = FoundryErrors.Classify(new CredentialUnavailableException("no az"));

        Assert.Equal(401, info.Status);
        Assert.Equal("authentication_error", info.AnthropicType);
        Assert.Contains("Install the Azure CLI", info.Message);
    }

    [Fact]
    public void Classify_UnknownException_IsGeneric500()
    {
        var info = FoundryErrors.Classify(new InvalidOperationException("weird"));

        Assert.Equal(500, info.Status);
        Assert.Equal("api_error", info.AnthropicType);
        Assert.Equal("server_error", info.OpenAiType);
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
