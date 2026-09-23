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
    public void PrepareBody_LeavesUnparseableBodiesUntouched()
    {
        var client = new FoundryAnthropicClient(
            new HttpClient(new CapturingHandler(HttpStatusCode.OK, "{}")),
            new Uri("https://r.example/"), "dep", new StaticCredential("t"));

        Assert.Equal("{not json", client.PrepareBody("{not json"));
    }

    // Regression: after v0.3.1 stripped the anthropic-beta HEADER, the real Claude Code
    // request still failed — Foundry also 400s on beta-gated BODY fields ("400
    // context_management: Extra inputs are not permitted", observed live).
    [Fact]
    public void PrepareBody_StrictMode_DropsNonStandardFieldsAndReportsThem()
    {
        var client = new FoundryAnthropicClient(
            new HttpClient(new CapturingHandler(HttpStatusCode.OK, "{}")),
            new Uri("https://r.example/"), "claude-sonnet-4-6", new StaticCredential("t"));

        IReadOnlyList<string>? dropped = null;
        var body = client.PrepareBody(
            """{"model":"alias","max_tokens":5,"messages":[],"stream":true,"temperature":1,"tools":[],"tool_choice":{"type":"auto"},"system":"s","metadata":{},"context_management":{"edits":[]},"mcp_servers":[]}""",
            d => dropped = d);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("claude-sonnet-4-6", doc.RootElement.GetProperty("model").GetString());
        Assert.False(doc.RootElement.TryGetProperty("context_management", out _));
        Assert.False(doc.RootElement.TryGetProperty("mcp_servers", out _));
        // Standard fields survive.
        Assert.True(doc.RootElement.TryGetProperty("tools", out _));
        Assert.True(doc.RootElement.TryGetProperty("stream", out _));
        Assert.Equal(["context_management", "mcp_servers"], dropped);
    }

    // Regression: Foundry 400s on typed tool entries it doesn't know ("tools.190: Input
    // tag 'advisor_20260301' found using 'type' does not match any of the expected
    // tags", observed live with claude-sonnet-5) -- stripping the beta header wasn't
    // enough, the advisor tool definition was still in the body.
    private const string AdvisorRejection = """
        {"type":"error","error":{"type":"invalid_request_error","message":"tools.3: Input tag 'advisor_20260301' found using 'type' does not match any of the expected tags: 'bash_20250124', 'custom', 'web_search_20250305'"}}
        """;

    private const string ToolsBody = """
        {"model":"m","max_tokens":5,"messages":[],"tools":[{"name":"Read","input_schema":{}},{"type":"custom","name":"c","input_schema":{}},{"type":"web_search_20250305","name":"web_search"},{"type":"advisor_20260301","name":"advisor"}],"tool_choice":{"type":"tool","name":"advisor"}}
        """;

    [Fact]
    public async Task Forward_ToolTypeRejection_StripsLearnedTypesAndRetries()
    {
        var handler = new ScriptedHandler((HttpStatusCode.BadRequest, AdvisorRejection), (HttpStatusCode.OK, "{}"));
        var client = new FoundryAnthropicClient(new HttpClient(handler), new Uri("https://r.example/"), "dep", new StaticCredential("t"));

        IReadOnlyList<string>? dropped = null;
        using var response = await client.ForwardAsync(ToolsBody, "messages", null, null, CancellationToken.None, d => dropped = d);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("advisor_20260301", handler.Bodies[0]); // optimistic first attempt

        using var retried = JsonDocument.Parse(handler.Bodies[1]);
        var names = retried.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString());
        Assert.Equal(["Read", "c", "web_search"], names);

        // A tool_choice forcing the dropped tool would itself be a 400 -- falls back to auto.
        Assert.Equal("auto", retried.RootElement.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.Equal(["tools[advisor_20260301]"], dropped);
    }

    [Fact]
    public async Task Forward_LearnedRejection_AppliesUpFrontToLaterRequests()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.BadRequest, AdvisorRejection), (HttpStatusCode.OK, "{}"), (HttpStatusCode.OK, "{}"));
        var client = new FoundryAnthropicClient(new HttpClient(handler), new Uri("https://r.example/"), "dep", new StaticCredential("t"));

        (await client.ForwardAsync(ToolsBody, "messages", null, null, CancellationToken.None)).Dispose();
        (await client.ForwardAsync(ToolsBody, "messages", null, null, CancellationToken.None)).Dispose();

        Assert.Equal(3, handler.Bodies.Count); // only the first request paid the retry
        Assert.DoesNotContain("advisor_20260301", handler.Bodies[2]);
    }

    [Fact]
    public async Task Forward_ExtraInputsRejection_DropsThatFieldAndRetries()
    {
        const string rejection = """{"type":"error","error":{"type":"invalid_request_error","message":"service_tier: Extra inputs are not permitted"}}""";
        var handler = new ScriptedHandler((HttpStatusCode.BadRequest, rejection), (HttpStatusCode.OK, "{}"));
        var client = new FoundryAnthropicClient(new HttpClient(handler), new Uri("https://r.example/"), "dep", new StaticCredential("t"));

        using var response = await client.ForwardAsync(
            """{"model":"m","max_tokens":5,"messages":[],"service_tier":"auto"}""", "messages", null, null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("service_tier", handler.Bodies[1]);
    }

    [Fact]
    public async Task Forward_UnrecognisedBadRequest_IsReturnedWithoutRetry()
    {
        const string rejection = """{"type":"error","error":{"type":"invalid_request_error","message":"messages: at least one message is required"}}""";
        var handler = new ScriptedHandler((HttpStatusCode.BadRequest, rejection));
        var client = new FoundryAnthropicClient(new HttpClient(handler), new Uri("https://r.example/"), "dep", new StaticCredential("t"));

        using var response = await client.ForwardAsync(
            """{"model":"m","max_tokens":5,"messages":[]}""", "messages", null, null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(handler.Bodies);
        Assert.Equal(rejection, await response.Content.ReadAsStringAsync()); // still readable after inspection
    }

    [Fact]
    public async Task Forward_PassthroughMode_NeverLearnsOrRetries()
    {
        var handler = new ScriptedHandler((HttpStatusCode.BadRequest, AdvisorRejection));
        var client = new FoundryAnthropicClient(
            new HttpClient(handler), new Uri("https://r.example/"), "dep", new StaticCredential("t"),
            bodyMode: FoundryAnthropicClient.BodyPassthrough);

        using var response = await client.ForwardAsync(ToolsBody, "messages", null, null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public void PrepareBody_PassthroughMode_KeepsNonStandardFields()
    {
        var client = new FoundryAnthropicClient(
            new HttpClient(new CapturingHandler(HttpStatusCode.OK, "{}")),
            new Uri("https://r.example/"), "dep", new StaticCredential("t"),
            bodyMode: FoundryAnthropicClient.BodyPassthrough);

        var body = client.PrepareBody("""{"model":"m","context_management":{"edits":[]}}""");

        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("context_management", out _));
        Assert.Equal("dep", doc.RootElement.GetProperty("model").GetString());
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

    [Fact]
    public void PrepareBody_SanitizesUnsupportedServerToolUseAndResultInHistory()
    {
        var client = new FoundryAnthropicClient(
            new HttpClient(new CapturingHandler(HttpStatusCode.OK, "{}")),
            new Uri("https://r.example/"), "dep", new StaticCredential("t"),
            bodyMode: FoundryAnthropicClient.BodyStrict);

        var rawBody = """
        {
          "model": "claude-sonnet-5",
          "messages": [
            {
              "role": "assistant",
              "content": [
                { "type": "text", "text": "Let me advise." },
                { "type": "server_tool_use", "id": "srv_1", "name": "advisor_20260301", "input": {"query":"test"} },
                { "type": "server_tool_use", "id": "srv_2", "name": "web_search", "input": {"query":"dotnet"} }
              ]
            },
            {
              "role": "user",
              "content": [
                { "type": "tool_result", "tool_use_id": "srv_1", "content": "advisory note" },
                { "type": "tool_result", "tool_use_id": "srv_2", "content": "search results" }
              ]
            }
          ]
        }
        """;

        var dropped = new List<string>();
        var sanitized = client.PrepareBody(rawBody, d => dropped = d.ToList());

        using var doc = JsonDocument.Parse(sanitized);
        var messages = doc.RootElement.GetProperty("messages");

        // Assistant content
        var assistantContent = messages[0].GetProperty("content");
        Assert.Equal(3, assistantContent.GetArrayLength());
        // block 1: converted to text
        Assert.Equal("text", assistantContent[1].GetProperty("type").GetString());
        Assert.Contains("advisor_20260301", assistantContent[1].GetProperty("text").GetString());
        // block 2: allowed server tool remains server_tool_use
        Assert.Equal("server_tool_use", assistantContent[2].GetProperty("type").GetString());
        Assert.Equal("web_search", assistantContent[2].GetProperty("name").GetString());

        // User content
        var userContent = messages[1].GetProperty("content");
        Assert.Equal(2, userContent.GetArrayLength());
        // block 0: tool_result matching srv_1 converted to text
        Assert.Equal("text", userContent[0].GetProperty("type").GetString());
        Assert.Contains("advisory note", userContent[0].GetProperty("text").GetString());
        // block 1: tool_result matching web_search remains tool_result
        Assert.Equal("tool_result", userContent[1].GetProperty("type").GetString());

        Assert.Contains("messages[*].server_tool_use[advisor_20260301]", dropped);
        Assert.Contains("messages[*].tool_result[advisor_20260301]", dropped);
    }

    private sealed class StaticCredential(string token) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(token, DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(GetToken(requestContext, cancellationToken));
    }

    // Replies with the scripted responses in order, recording each request body.
    private sealed class ScriptedHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var (status, body) = responses[Bodies.Count - 1];
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
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
