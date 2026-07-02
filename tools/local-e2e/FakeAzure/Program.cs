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
    if (lastToolContent is not null)
    {
        message = new { role = "assistant", content = $"TOOL RESULT RECEIVED >>>{lastToolContent}<<<" };
        finish = "stop";
    }
    else if (body.Contains(probeMarker)
        && doc.RootElement.TryGetProperty("tools", out var tools)
        && tools.ValueKind == JsonValueKind.Array
        && tools.GetArrayLength() > 0)
    {
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
        message = new { role = "assistant", content = "OK" };
        finish = "stop";
    }

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
}

app.MapPost("/openai/deployments/{dep}/chat/completions", Handle);
app.MapPost("/openai/v1/chat/completions", Handle);

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
