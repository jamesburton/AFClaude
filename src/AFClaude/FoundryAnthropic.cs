using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;

namespace AFClaude;

// Which API surface the Foundry deployment is served on. Claude models deployed on
// Azure AI Foundry are NOT reachable via the Azure-OpenAI chat-completions route —
// they live on a native Anthropic Messages endpoint at {endpoint}/anthropic/v1/messages
// (confirmed live: the OpenAI route 404s "api_not_supported", the Anthropic route
// answers once an anthropic-version header is supplied).
internal enum FoundryApi
{
    OpenAI,
    Anthropic,
}

// Resolves the API surface once: an explicit Foundry__Api setting wins; otherwise a
// one-off probe against the Anthropic route decides, with the result cached for the
// process lifetime. A faulted probe (e.g. auth failure) is not cached, so the next
// request retries instead of pinning a wrong answer.
internal sealed class FoundryApiResolver(FoundryApi? configured, Func<CancellationToken, Task<bool>> probeAnthropic)
{
    private readonly object _lock = new();
    private Task<FoundryApi>? _resolved;

    public FoundryApi? Configured => configured;

    public Task<FoundryApi> ResolveAsync(CancellationToken cancellationToken)
    {
        if (configured is { } explicitApi)
        {
            return Task.FromResult(explicitApi);
        }

        lock (_lock)
        {
            if (_resolved is null || _resolved.IsFaulted || _resolved.IsCanceled)
            {
                // Detached from the caller's token: one caller's cancellation must not
                // poison the cached result for everyone else.
                _resolved = DetectAsync();
            }
            return _resolved;
        }
    }

    private async Task<FoundryApi> DetectAsync()
        => await probeAnthropic(CancellationToken.None) ? FoundryApi.Anthropic : FoundryApi.OpenAI;
}

// Direct client for the native Anthropic Messages surface on a Foundry resource.
// Requests pass through byte-faithfully except the model field, which is rewritten to
// the configured deployment (AFClaude is a single-deployment proxy, and Claude Code
// sends its own model aliases for background traffic).
internal sealed class FoundryAnthropicClient(
    HttpClient http,
    Uri endpoint,
    string deployment,
    TokenCredential credential,
    string betaMode = FoundryAnthropicClient.BetaStrip,
    string bodyMode = FoundryAnthropicClient.BodyStrict)
{
    public const string DefaultAnthropicVersion = "2023-06-01";

    // Request-body policy, the body-level twin of the anthropic-beta header policy:
    // Claude Code also sends beta-gated top-level FIELDS (observed live:
    // "context_management") and Foundry's strict schema 400s on unknown keys
    // ("Extra inputs are not permitted") instead of ignoring them like the real
    // Anthropic API. strict (default) keeps only the standard Messages API fields;
    // passthrough forwards the body untouched.
    public const string BodyStrict = "strict";
    public const string BodyPassthrough = "passthrough";

    private static readonly HashSet<string> StandardMessagesFields = new(StringComparer.Ordinal)
    {
        "model", "messages", "max_tokens", "system", "metadata", "stop_sequences",
        "stream", "temperature", "top_k", "top_p", "tools", "tool_choice",
        "thinking", "service_tier",
    };

    // anthropic-beta handling. Claude Code sends opt-in feature flags (e.g.
    // "advisor-tool-2026-03-01") when it believes it's talking to real Anthropic
    // infrastructure; Foundry's hosted Claude endpoint hard-rejects unknown values
    // with a 400 instead of ignoring them (observed live). Stripping is therefore the
    // safe default — beta features degrade gracefully when the server doesn't
    // advertise them.
    public const string BetaStrip = "strip";
    public const string BetaPassthrough = "passthrough";

    public string Deployment => deployment;

    public string BetaMode => betaMode;

    public async Task<HttpResponseMessage> ForwardAsync(
        string rawBody,
        string path,
        string? anthropicVersion,
        string? anthropicBeta,
        CancellationToken cancellationToken,
        Action<IReadOnlyList<string>>? onDroppedBodyFields = null)
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext([FoundryClientFactory.TokenScope]), cancellationToken);

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, $"anthropic/v1/{path}"))
        {
            Content = new StringContent(PrepareBody(rawBody, onDroppedBodyFields), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token.Token}");
        request.Headers.TryAddWithoutValidation("anthropic-version",
            string.IsNullOrEmpty(anthropicVersion) ? DefaultAnthropicVersion : anthropicVersion);

        // strip (default) drops the client's beta flags; passthrough forwards them;
        // any other configured value is sent as a literal replacement list.
        var beta = betaMode switch
        {
            BetaStrip => null,
            BetaPassthrough => anthropicBeta,
            _ => betaMode,
        };
        if (!string.IsNullOrEmpty(beta))
        {
            request.Headers.TryAddWithoutValidation("anthropic-beta", beta);
        }

        // ResponseHeadersRead so SSE streams flow through incrementally.
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    // 2xx on a 1-token request proves this deployment answers the Anthropic route.
    // 401/403 is an auth problem, not evidence of the API shape — surface it so
    // FoundryErrors classifies it rather than silently falling back to OpenAI.
    public async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = deployment,
            max_tokens = 1,
            messages = new[] { new { role = "user", content = "ping" } },
        });

        using var response = await ForwardAsync(body, "messages", null, null, cancellationToken);
        if ((int)response.StatusCode is 401 or 403)
        {
            throw new RequestFailedException(
                (int)response.StatusCode,
                $"Foundry rejected the API probe with HTTP {(int)response.StatusCode}.");
        }
        return response.IsSuccessStatusCode;
    }

    // Minimal ask for the MCP surface: one user turn, text blocks concatenated.
    public async Task<string> AskAsync(string prompt, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = deployment,
            max_tokens = 4096,
            system = "You are a local proxy agent. Preserve the caller's intent.",
            messages = new[] { new { role = "user", content = prompt } },
        });

        using var response = await ForwardAsync(body, "messages", null, null, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new RequestFailedException((int)response.StatusCode, text);
        }

        using var doc = JsonDocument.Parse(text);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var type) && type.GetString() == "text"
                    && block.TryGetProperty("text", out var t))
                {
                    sb.Append(t.GetString());
                }
            }
        }
        return sb.ToString();
    }

    // Rewrites model to the configured deployment and, in strict body mode, drops
    // top-level fields outside the standard Messages API (reported via the callback).
    internal string PrepareBody(string rawBody, Action<IReadOnlyList<string>>? onDroppedFields = null)
    {
        try
        {
            if (JsonNode.Parse(rawBody) is JsonObject obj)
            {
                obj["model"] = deployment;

                if (bodyMode == BodyStrict)
                {
                    var dropped = obj.Select(p => p.Key).Where(k => !StandardMessagesFields.Contains(k)).ToList();
                    foreach (var key in dropped)
                    {
                        obj.Remove(key);
                    }
                    if (dropped.Count > 0)
                    {
                        onDroppedFields?.Invoke(dropped);
                    }
                }

                return obj.ToJsonString();
            }
        }
        catch (JsonException)
        {
            // Unparseable bodies pass through untouched — the upstream 400 is the
            // caller's real answer.
        }
        return rawBody;
    }
}
