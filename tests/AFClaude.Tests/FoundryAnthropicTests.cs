using System.Net;
using System.Text.Json;
using Azure.Core;
using AFClaude;

namespace AFClaude.Tests;

public class FoundryAnthropicTests
{
    [Fact]
    public async Task Resolver_ExplicitConfiguration_SkipsProbe()
    {
        var probes = 0;
        var resolver = new FoundryApiResolver(FoundryApi.Anthropic, _ => { probes++; return Task.FromResult(false); });

        Assert.Equal(FoundryApi.Anthropic, await resolver.ResolveAsync(CancellationToken.None));
        Assert.Equal(0, probes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resolver_Auto_ProbesOnceAndCaches(bool probeResult)
    {
        var expected = probeResult ? FoundryApi.Anthropic : FoundryApi.OpenAI;
        var probes = 0;
        var resolver = new FoundryApiResolver(null, _ => { probes++; return Task.FromResult(probeResult); });

        Assert.Equal(expected, await resolver.ResolveAsync(CancellationToken.None));
        Assert.Equal(expected, await resolver.ResolveAsync(CancellationToken.None));
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task Resolver_FaultedProbe_RetriesNextCall()
    {
        var probes = 0;
        var resolver = new FoundryApiResolver(null, _ =>
        {
            probes++;
            return probes == 1
                ? Task.FromException<bool>(new InvalidOperationException("transient"))
                : Task.FromResult(true);
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(CancellationToken.None));
        Assert.Equal(FoundryApi.Anthropic, await resolver.ResolveAsync(CancellationToken.None));
        Assert.Equal(2, probes);
    }

    [Fact]
    public async Task Forward_TargetsAnthropicRouteWithAuthAndVersionHeaders()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"ok":true}""");
        var client = new FoundryAnthropicClient(
            new HttpClient(handler),
            new Uri("https://resource.services.ai.azure.com/"),
            "claude-sonnet-4-6",
            new StaticCredential("bearer-abc"));

        using var response = await client.ForwardAsync(
            """{"model":"claude-alias-from-client","max_tokens":5,"messages":[]}""",
            "messages", anthropicVersion: null, anthropicBeta: "advisor-tool-2026-03-01", CancellationToken.None);

        Assert.Equal("https://resource.services.ai.azure.com/anthropic/v1/messages", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer bearer-abc", handler.Request.Headers.GetValues("Authorization").Single());
        Assert.Equal(FoundryAnthropicClient.DefaultAnthropicVersion, handler.Request.Headers.GetValues("anthropic-version").Single());

        // Default mode strips the client's beta flags — Foundry hard-rejects unknown
        // values (observed live: 400 on Claude Code's advisor-tool flag).
        Assert.False(handler.Request.Headers.Contains("anthropic-beta"));

        // Single-deployment proxy: whatever model the client asked for is rewritten.
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("claude-sonnet-4-6", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Forward_PassthroughBetaMode_ForwardsClientFlags()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{}");
        var client = new FoundryAnthropicClient(
            new HttpClient(handler), new Uri("https://r.example/"), "dep", new StaticCredential("t"),
            FoundryAnthropicClient.BetaPassthrough);

        using var _ = await client.ForwardAsync("{}", "messages", null, "advisor-tool-2026-03-01", CancellationToken.None);

        Assert.Equal("advisor-tool-2026-03-01", handler.Request!.Headers.GetValues("anthropic-beta").Single());
    }

    [Fact]
    public async Task Forward_LiteralBetaMode_ReplacesClientFlags()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{}");
        var client = new FoundryAnthropicClient(
            new HttpClient(handler), new Uri("https://r.example/"), "dep", new StaticCredential("t"),
            "token-efficient-tools-2025-02-19");

        using var _ = await client.ForwardAsync("{}", "messages", null, "advisor-tool-2026-03-01", CancellationToken.None);

        Assert.Equal("token-efficient-tools-2025-02-19", handler.Request!.Headers.GetValues("anthropic-beta").Single());
    }

    [Fact]
    public async Task Forward_ClientSuppliedVersionHeaderWins()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{}");
        var client = new FoundryAnthropicClient(
            new HttpClient(handler), new Uri("https://r.example/"), "dep", new StaticCredential("t"));

        using var _ = await client.ForwardAsync("{}", "messages", "2024-06-01", null, CancellationToken.None);

        Assert.Equal("2024-06-01", handler.Request!.Headers.GetValues("anthropic-version").Single());
    }

    [Fact]
    public void RewriteModel_LeavesUnparseableBodiesUntouched()
    {
        var client = new FoundryAnthropicClient(
            new HttpClient(new CapturingHandler(HttpStatusCode.OK, "{}")),
            new Uri("https://r.example/"), "dep", new StaticCredential("t"));

        Assert.Equal("{not json", client.RewriteModel("{not json"));
    }

    [Fact]
    public async Task Probe_Unauthorized_ThrowsInsteadOfFallingBackToOpenAI()
    {
        var client = new FoundryAnthropicClient(
            new HttpClient(new CapturingHandler(HttpStatusCode.Forbidden, "denied")),
            new Uri("https://r.example/"), "dep", new StaticCredential("t"));

        await Assert.ThrowsAsync<Azure.RequestFailedException>(() => client.ProbeAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public async Task Probe_MapsStatusToApiShape(HttpStatusCode status, bool expectAnthropic)
    {
        var client = new FoundryAnthropicClient(
            new HttpClient(new CapturingHandler(status, "{}")),
            new Uri("https://r.example/"), "dep", new StaticCredential("t"));

        Assert.Equal(expectAnthropic, await client.ProbeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Ask_ConcatenatesTextBlocksFromResponse()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK,
            """{"content":[{"type":"text","text":"Hello "},{"type":"tool_use","id":"x","name":"n","input":{}},{"type":"text","text":"world"}]}""");
        var client = new FoundryAnthropicClient(
            new HttpClient(handler), new Uri("https://r.example/"), "dep", new StaticCredential("t"));

        Assert.Equal("Hello world", await client.AskAsync("hi", CancellationToken.None));
    }

    private sealed class StaticCredential(string token) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(token, DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(GetToken(requestContext, cancellationToken));
    }

    private sealed class CapturingHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
