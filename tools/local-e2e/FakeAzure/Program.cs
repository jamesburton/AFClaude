using System.Text.Json;

// Fake Azure OpenAI chat-completions endpoint for AFClaude local E2E:
//  - request has a tool-role message  -> final answer echoing the tool content
//  - request mentions the probe file and carries tools -> scripted Read tool call
//  - anything else -> "OK"
// Logs every request/response pair to the log dir.

var probeMarker = "afclaude-probe.txt";
var probePath = Environment.GetEnvironmentVariable("FAKE_PROBE_PATH")
    ?? throw new InvalidOperationException("Set FAKE_PROBE_PATH");
var logDir = Environment.GetEnvironmentVariable("FAKE_LOG_DIR") ?? "fake-azure-logs";
Directory.CreateDirectory(logDir);
var seq = 0;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("https://127.0.0.1:41443");
var app = builder.Build();

async Task Handle(HttpContext http)
{
    string body;
    using (var reader = new StreamReader(http.Request.Body))
    {
        body = await reader.ReadToEndAsync();
    }
    var n = Interlocked.Increment(ref seq);
    File.WriteAllText(Path.Combine(logDir, $"{n:D3}-request.json"),
        $"{http.Request.Method} {http.Request.Path}{http.Request.QueryString}\n{body}");

    // Error-parity simulation: a deployment name containing "missing404" gets the
    // real Azure DeploymentNotFound shape, so status mapping is testable end to end.
    if (http.Request.Path.Value?.Contains("missing404") == true)
    {
        http.Response.StatusCode = 404;
        await http.Response.WriteAsJsonAsync(new
        {
            error = new { code = "DeploymentNotFound", message = "The API deployment for this resource does not exist." }
        });
        return;
    }

    using var doc = JsonDocument.Parse(body);
    string? lastToolContent = null;
    if (doc.RootElement.TryGetProperty("messages", out var messages))
    {
        foreach (var m in messages.EnumerateArray())
        {
            if (m.TryGetProperty("role", out var role) && role.GetString() == "tool")
            {
                lastToolContent = m.TryGetProperty("content", out var c) ? c.ToString() : "";
            }
        }
    }

    object message;
    string finish;
    string? textReply = null;
    var isToolCall = false;
    if (lastToolContent is not null)
    {
        textReply = $"TOOL RESULT RECEIVED >>>{lastToolContent}<<<";
        message = new { role = "assistant", content = textReply };
        finish = "stop";
    }
    else if (body.Contains(probeMarker)
        && doc.RootElement.TryGetProperty("tools", out var tools)
        && tools.ValueKind == JsonValueKind.Array
        && tools.GetArrayLength() > 0)
    {
        isToolCall = true;
        message = new
        {
            role = "assistant",
            content = (string?)null,
            tool_calls = new object[]
            {
                new
                {
                    id = "call_fake_read_1",
                    type = "function",
                    function = new { name = "Read", arguments = JsonSerializer.Serialize(new { file_path = probePath }) }
                }
            }
        };
        finish = "tool_calls";
    }
    else
    {
        textReply = "OK";
        message = new { role = "assistant", content = textReply };
        finish = "stop";
    }

    var wantsStream = doc.RootElement.TryGetProperty("stream", out var streamProp)
        && streamProp.ValueKind == JsonValueKind.True;

    if (!wantsStream)
    {
        var resp = new
        {
            id = $"chatcmpl-fake-{n}",
            @object = "chat.completion",
            created = 1700000000,
            model = "gpt-fake",
            choices = new object[] { new { index = 0, finish_reason = finish, message } },
            usage = new { prompt_tokens = 42, completion_tokens = 7, total_tokens = 49 }
        };
        File.WriteAllText(Path.Combine(logDir, $"{n:D3}-response.json"), JsonSerializer.Serialize(resp));
        await http.Response.WriteAsJsonAsync(resp);
        return;
    }

    // Genuine chat.completion.chunk SSE, split across multiple frames so the bridge's
    // incremental translation is actually exercised.
    http.Response.ContentType = "text/event-stream";
    async Task Chunk(object delta, string? finishReason = null, object? usage = null)
    {
        var chunk = new
        {
            id = $"chatcmpl-fake-{n}",
            @object = "chat.completion.chunk",
            created = 1700000000,
            model = "gpt-fake",
            choices = new object[] { new { index = 0, delta, finish_reason = finishReason } },
            usage,
        };
        await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
        await http.Response.Body.FlushAsync();
    }

    if (isToolCall)
    {
        var arguments = JsonSerializer.Serialize(new { file_path = probePath });
        var half = arguments.Length / 2;
        await Chunk(new
        {
            role = "assistant",
            tool_calls = new object[]
            {
                new { index = 0, id = "call_fake_read_1", type = "function", function = new { name = "Read", arguments = arguments[..half] } }
            }
        });
        await Chunk(new
        {
            tool_calls = new object[]
            {
                new { index = 0, function = new { arguments = arguments[half..] } }
            }
        });
    }
    else
    {
        var half = Math.Max(1, textReply!.Length / 2);
        await Chunk(new { role = "assistant", content = textReply[..half] });
        if (textReply.Length > half)
        {
            await Chunk(new { content = textReply[half..] });
        }
    }

    await Chunk(new { }, finish, new { prompt_tokens = 42, completion_tokens = 7, total_tokens = 49 });
    await http.Response.WriteAsync("data: [DONE]\n\n");
    await http.Response.Body.FlushAsync();
    File.WriteAllText(Path.Combine(logDir, $"{n:D3}-response.txt"), $"SSE finish={finish}");
}

app.MapPost("/openai/deployments/{dep}/chat/completions", Handle);
app.MapPost("/openai/v1/chat/completions", Handle);

// Native Anthropic Messages surface, as served for Claude deployments on Foundry.
// Mimics the real behaviour: 400 without an anthropic-version header; otherwise
// scripts the same probe-file scenario in Anthropic wire shapes (SSE when stream:true).
app.MapPost("/anthropic/v1/messages", async (HttpContext http) =>
{
    string body;
    using (var reader = new StreamReader(http.Request.Body))
    {
        body = await reader.ReadToEndAsync();
    }
    var n = Interlocked.Increment(ref seq);
    File.WriteAllText(Path.Combine(logDir, $"{n:D3}-anthropic-request.json"),
        $"POST /anthropic/v1/messages\nversion={http.Request.Headers["anthropic-version"]}\nauth={http.Request.Headers.Authorization}\n{body}");

    if (!http.Request.Headers.ContainsKey("anthropic-version"))
    {
        http.Response.StatusCode = 400;
        await http.Response.WriteAsJsonAsync(new
        {
            type = "error",
            error = new { type = "invalid_request_error", message = "anthropic-version: header is required" }
        });
        return;
    }

    // Real Foundry hard-rejects anthropic-beta flags it doesn't recognise (observed
    // live with Claude Code's advisor-tool flag) — mimic that so the E2E fails if
    // AFClaude ever stops stripping the header by default.
    if (http.Request.Headers.TryGetValue("anthropic-beta", out var beta))
    {
        http.Response.StatusCode = 400;
        await http.Response.WriteAsJsonAsync(new
        {
            type = "error",
            error = new { type = "invalid_request_error", message = $"Unexpected value(s) '{beta}' for the 'anthropic-beta' header." }
        });
        return;
    }

    using var doc = JsonDocument.Parse(body);

    // Real Foundry validates the body strictly and 400s on beta-gated fields
    // (observed live: "context_management: Extra inputs are not permitted") — mimic
    // it for ANY non-standard top-level key so the E2E catches future claude fields.
    var standardFields = new HashSet<string>
    {
        "model", "messages", "max_tokens", "system", "metadata", "stop_sequences",
        "stream", "temperature", "top_k", "top_p", "tools", "tool_choice",
        "thinking", "service_tier",
    };
    var extraField = doc.RootElement.EnumerateObject().Select(p => p.Name).FirstOrDefault(k => !standardFields.Contains(k));
    if (extraField is not null)
    {
        http.Response.StatusCode = 400;
        await http.Response.WriteAsJsonAsync(new
        {
            type = "error",
            error = new { type = "invalid_request_error", message = $"{extraField}: Extra inputs are not permitted" }
        });
        return;
    }

    var stream = doc.RootElement.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
    var isProbeCall = doc.RootElement.TryGetProperty("max_tokens", out var mt)
        && mt.ValueKind == JsonValueKind.Number && mt.GetInt32() == 1;

    string? toolResultText = null;
    if (doc.RootElement.TryGetProperty("messages", out var msgs))
    {
        foreach (var m in msgs.EnumerateArray())
        {
            if (m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in c.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_result")
                    {
                        toolResultText = block.TryGetProperty("content", out var rc) ? rc.ToString() : "";
                    }
                }
            }
        }
    }

    string? textReply = null;
    var stopReason = "end_turn";
    var isToolUse = false;
    if (toolResultText is not null)
    {
        textReply = $"TOOL RESULT RECEIVED >>>{toolResultText}<<<";
    }
    else if (!isProbeCall && body.Contains(probeMarker)
        && doc.RootElement.TryGetProperty("tools", out var tools)
        && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
    {
        isToolUse = true;
        stopReason = "tool_use";
    }
    else
    {
        textReply = "OK";
    }

    if (!stream)
    {
        object contentBlock = isToolUse
            ? new { type = "tool_use", id = "toolu_fake_1", name = "Read", input = new { file_path = probePath } }
            : new { type = "text", text = textReply };
        var resp = new
        {
            id = $"msg_fake_{n}",
            type = "message",
            role = "assistant",
            model = "claude-fake",
            content = new[] { contentBlock },
            stop_reason = stopReason,
            stop_sequence = (string?)null,
            usage = new { input_tokens = 42, output_tokens = 7 },
        };
        File.WriteAllText(Path.Combine(logDir, $"{n:D3}-anthropic-response.json"), JsonSerializer.Serialize(resp));
        await http.Response.WriteAsJsonAsync(resp);
        return;
    }

    http.Response.ContentType = "text/event-stream";
    async Task Sse(string ev, object data)
    {
        await http.Response.WriteAsync($"event: {ev}\ndata: {JsonSerializer.Serialize(data)}\n\n");
        await http.Response.Body.FlushAsync();
    }

    await Sse("message_start", new
    {
        type = "message_start",
        message = new
        {
            id = $"msg_fake_{n}",
            type = "message",
            role = "assistant",
            content = Array.Empty<object>(),
            model = "claude-fake",
            stop_reason = (string?)null,
            stop_sequence = (string?)null,
            usage = new { input_tokens = 42, output_tokens = 0 },
        }
    });

    if (isToolUse)
    {
        await Sse("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "tool_use", id = "toolu_fake_1", name = "Read", input = new { } } });
        await Sse("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = JsonSerializer.Serialize(new { file_path = probePath }) } });
    }
    else
    {
        await Sse("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } });
        await Sse("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = textReply } });
    }

    await Sse("content_block_stop", new { type = "content_block_stop", index = 0 });
    await Sse("message_delta", new { type = "message_delta", delta = new { stop_reason = stopReason, stop_sequence = (string?)null }, usage = new { output_tokens = 7 } });
    await Sse("message_stop", new { type = "message_stop" });
    File.WriteAllText(Path.Combine(logDir, $"{n:D3}-anthropic-response.txt"), $"SSE stop_reason={stopReason}");
});

app.MapFallback(async (HttpContext http) =>
{
    var body = string.Empty;
    if (http.Request.ContentLength > 0)
    {
        using var reader = new StreamReader(http.Request.Body);
        body = await reader.ReadToEndAsync();
    }
    File.AppendAllText(Path.Combine(logDir, "unmatched.log"),
        $"{http.Request.Method} {http.Request.Path}{http.Request.QueryString}\n{body}\n---\n");
    http.Response.StatusCode = 404;
});

app.Run();
