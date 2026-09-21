using System.ClientModel;
using System.ClientModel.Primitives;
using AFClaude;

namespace AFClaude.Tests;

public class MaxTokensParamResolverTests
{
    [Fact]
    public void Auto_StartsOptimisticWithLegacy()
    {
        var resolver = new MaxTokensParamResolver(configured: null);

        Assert.False(resolver.Current);
    }

    [Fact]
    public void Auto_LegacyRejection_RetriesAndCaches()
    {
        var resolver = new MaxTokensParamResolver(configured: null);

        Assert.True(resolver.ShouldRetryWithNew(attemptedWithNew: false));
        Assert.True(resolver.Current);
    }

    [Fact]
    public void Auto_ConcurrentLegacyRequests_EachGetARetry()
    {
        // Regression: requests that all went out with legacy before the first flip must
        // each be retried -- an earlier "only the flip winner retries" design failed the
        // losers even though the modern field would have worked.
        var resolver = new MaxTokensParamResolver(configured: null);

        Assert.True(resolver.ShouldRetryWithNew(attemptedWithNew: false));
        Assert.True(resolver.ShouldRetryWithNew(attemptedWithNew: false));
    }

    [Fact]
    public void Auto_RejectionOfModernField_IsNotRetried()
    {
        var resolver = new MaxTokensParamResolver(configured: null);

        Assert.False(resolver.ShouldRetryWithNew(attemptedWithNew: true)); // a retry can't loop
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_NeverSelfHeals(bool configuredValue)
    {
        var resolver = new MaxTokensParamResolver(configuredValue);

        Assert.Equal(configuredValue, resolver.Current);
        Assert.False(resolver.ShouldRetryWithNew(attemptedWithNew: false));
        Assert.Equal(configuredValue, resolver.Current); // unchanged
    }

    [Fact]
    public void IsMaxTokensRejected_MatchesRealAzureErrorText()
    {
        var ex = new ClientResultException(
            "Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.",
            new FakeResponse(400), null!);

        Assert.True(MaxTokensParamResolver.IsMaxTokensRejected(ex));
    }

    [Fact]
    public void IsMaxTokensRejected_WrongStatus_IsFalse()
    {
        var ex = new ClientResultException(
            "Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.",
            new FakeResponse(429), null!);

        Assert.False(MaxTokensParamResolver.IsMaxTokensRejected(ex));
    }

    [Fact]
    public void IsMaxTokensRejected_UnrelatedBadRequest_IsFalse()
    {
        var ex = new ClientResultException("Some other invalid request.", new FakeResponse(400), null!);

        Assert.False(MaxTokensParamResolver.IsMaxTokensRejected(ex));
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
