using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using AFClaude;
using Microsoft.Agents.AI;
using OpenAI.Chat;

if (args.Length > 0 && string.Equals(args[0], "launch", StringComparison.OrdinalIgnoreCase))
{
    await RunLaunchAsync(args[1..]);
}
else if (args.Contains("--http", StringComparer.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("AFClaude__Mode"), "http", StringComparison.OrdinalIgnoreCase))
{
    await RunHttpAsync(args);
}
else
{
    await RunMcpAsync(args);
}

// MCP stdio server — the primary Claude integration path. Exposes ask_foundry.
static async Task RunMcpAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    // Stdio transport uses stdout exclusively for JSON-RPC; all logs must go to stderr.
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

    var (chatClient, _) = FoundryClientFactory.Create(builder.Configuration);
    AIAgent agent = chatClient.AsAIAgent(
        instructions: "You are a local proxy agent. Preserve the caller's intent.");
    builder.Services.AddSingleton(agent);

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync();
}

// OpenAI-compatible HTTP proxy — secondary path for other OpenAI-compatible clients.
static async Task RunHttpAsync(string[] args)
{
    var app = BuildHttpApp(args);
    await app.RunAsync();
}

// launch: starts the HTTP host on a known local port, points Claude Code's own
// traffic at its Anthropic-compatible /v1/messages endpoint via ANTHROPIC_BASE_URL,
// and execs `claude` in the foreground with the remaining args forwarded to it.
static async Task RunLaunchAsync(string[] claudeArgs)
{
    var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    var (_, deployment) = FoundryClientFactory.Create(config); // fail fast before starting anything

    var port = config.GetValue<int?>("Launch:Port") ?? 31337;
    var baseUrl = $"http://127.0.0.1:{port}";

    var app = BuildHttpApp([], baseUrl);
    await app.StartAsync();
    Console.Error.WriteLine($"AFClaude proxy listening on {baseUrl} (Foundry deployment: {deployment})");

    var psi = new ProcessStartInfo("claude") { UseShellExecute = false };
    foreach (var a in claudeArgs)
    {
        psi.ArgumentList.Add(a);
    }
    psi.Environment["ANTHROPIC_BASE_URL"] = baseUrl;
    psi.Environment["ANTHROPIC_API_KEY"] = "afclaude-local";
    psi.Environment["ANTHROPIC_MODEL"] = deployment;

    Process claude;
    try
    {
        claude = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start 'claude'.");
    }
    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
    {
        await app.StopAsync();
        throw new InvalidOperationException(
            "Could not launch 'claude'. Is Claude Code installed and on PATH?", ex);
    }

    await claude.WaitForExitAsync();
    await app.StopAsync();
    Environment.ExitCode = claude.ExitCode;
}

static WebApplication BuildHttpApp(string[] args, string? bindUrl = null)
{
    var builder = WebApplication.CreateBuilder(args);
    if (bindUrl is not null)
    {
        builder.WebHost.UseUrls(bindUrl);
    }

    var (chatClient, deployment) = FoundryClientFactory.Create(builder.Configuration);
    builder.Services.AddSingleton(chatClient);
    builder.Services.AddSingleton(new DeploymentInfo(deployment));

    var app = builder.Build();

    app.MapGet("/v1/models", (DeploymentInfo info) => Results.Json(new
    {
        @object = "list",
        data = new[]
        {
            new { id = info.Deployment, @object = "model", owned_by = "azure-foundry" }
        }
    }));

    app.MapPost("/v1/chat/completions", async (
        OpenAiChatRequest request,
        ChatClient chatClient,
        DeploymentInfo info,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var messages = request.Messages.Select(ToChatMessage).ToList();

            var completion = await chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken);

            var text = completion.Value.Content.Count > 0
                ? completion.Value.Content[0].Text
                : string.Empty;

            return Results.Json(new
            {
                id = $"chatcmpl-{Guid.NewGuid():N}",
                @object = "chat.completion",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model = request.Model ?? info.Deployment,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = text },
                        finish_reason = "stop"
                    }
                }
            });
        }
        catch (Exception ex) when (FoundryErrors.IsAuthFailure(ex))
        {
            logger.LogError(ex, "Chat completion auth failure");
            return Results.Json(
                new { error = new { message = FoundryErrors.Describe(ex), type = "authentication_error", code = (string?)null } },
                statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Chat completion failed");
            return Results.Json(
                new { error = new { message = FoundryErrors.Describe(ex), type = "server_error", code = (string?)null } },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    });

    // Anthropic Messages API-compatible endpoint — what Claude Code's ANTHROPIC_BASE_URL
    // actually needs (it does not speak the OpenAI shape above). Bridges Anthropic
    // tools/tool_use/tool_result to Azure OpenAI function-calling in both directions.
    app.MapPost("/v1/messages", async (
        HttpContext http,
        AnthropicMessagesRequest request,
        ChatClient chatClient,
        DeploymentInfo info,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        var messages = AnthropicBridge.ToChatMessages(request);
        var options = AnthropicBridge.ToOptions(request);

        ChatCompletion completion;
        try
        {
            completion = await chatClient.CompleteChatAsync(messages, options, cancellationToken);
        }
        catch (Exception ex) when (FoundryErrors.IsAuthFailure(ex))
        {
            logger.LogError(ex, "Anthropic-compatible completion auth failure");
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await http.Response.WriteAsJsonAsync(
                new { type = "error", error = new { type = "authentication_error", message = FoundryErrors.Describe(ex) } },
                cancellationToken);
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Anthropic-compatible completion failed");
            http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await http.Response.WriteAsJsonAsync(
                new { type = "error", error = new { type = "api_error", message = FoundryErrors.Describe(ex) } },
                cancellationToken);
            return;
        }

        var blocks = AnthropicBridge.ToOutBlocks(completion);
        if (blocks.Count == 0)
        {
            blocks.Add(new AnthropicTextBlock(string.Empty));
        }

        var stopReason = AnthropicBridge.MapStopReason(
            completion.FinishReason, blocks.Any(b => b is AnthropicToolUseBlock));
        var inputTokens = completion.Usage?.InputTokenCount ?? 0;
        var outputTokens = completion.Usage?.OutputTokenCount ?? 0;
        var messageId = $"msg_{Guid.NewGuid():N}";
        var model = string.IsNullOrEmpty(request.Model) ? info.Deployment : request.Model;

        if (request.Stream)
        {
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";

            await WriteSseAsync(http.Response, "message_start", new
            {
                type = "message_start",
                message = new
                {
                    id = messageId,
                    type = "message",
                    role = "assistant",
                    content = Array.Empty<object>(),
                    model,
                    stop_reason = (string?)null,
                    stop_sequence = (string?)null,
                    usage = new { input_tokens = inputTokens, output_tokens = 0 }
                }
            }, cancellationToken);

            for (var i = 0; i < blocks.Count; i++)
            {
                switch (blocks[i])
                {
                    case AnthropicTextBlock text:
                        await WriteSseAsync(http.Response, "content_block_start", new
                        {
                            type = "content_block_start",
                            index = i,
                            content_block = new { type = "text", text = "" }
                        }, cancellationToken);

                        await WriteSseAsync(http.Response, "content_block_delta", new
                        {
                            type = "content_block_delta",
                            index = i,
                            delta = new { type = "text_delta", text = text.Text }
                        }, cancellationToken);
                        break;

                    case AnthropicToolUseBlock toolUse:
                        await WriteSseAsync(http.Response, "content_block_start", new
                        {
                            type = "content_block_start",
                            index = i,
                            content_block = new { type = "tool_use", id = toolUse.Id, name = toolUse.Name, input = new { } }
                        }, cancellationToken);

                        await WriteSseAsync(http.Response, "content_block_delta", new
                        {
                            type = "content_block_delta",
                            index = i,
                            delta = new { type = "input_json_delta", partial_json = toolUse.ArgumentsJson }
                        }, cancellationToken);
                        break;
                }

                await WriteSseAsync(http.Response, "content_block_stop", new { type = "content_block_stop", index = i }, cancellationToken);
            }

            await WriteSseAsync(http.Response, "message_delta", new
            {
                type = "message_delta",
                delta = new { stop_reason = stopReason, stop_sequence = (string?)null },
                usage = new { output_tokens = outputTokens }
            }, cancellationToken);

            await WriteSseAsync(http.Response, "message_stop", new { type = "message_stop" }, cancellationToken);
            return;
        }

        await http.Response.WriteAsJsonAsync(new
        {
            id = messageId,
            type = "message",
            role = "assistant",
            content = blocks.Select(AnthropicBridge.ToJson).ToArray(),
            model,
            stop_reason = stopReason,
            stop_sequence = (string?)null,
            usage = new { input_tokens = inputTokens, output_tokens = outputTokens }
        }, cancellationToken);
    });

    return app;
}

static async Task WriteSseAsync(HttpResponse response, string eventName, object data, CancellationToken cancellationToken)
{
    await response.WriteAsync($"event: {eventName}\n", cancellationToken);
    await response.WriteAsync($"data: {JsonSerializer.Serialize(data)}\n\n", cancellationToken);
    await response.Body.FlushAsync(cancellationToken);
}

static ChatMessage ToChatMessage(OpenAiMessage message) => message.Role switch
{
    "system" => new SystemChatMessage(message.Content),
    "assistant" => new AssistantChatMessage(message.Content),
    _ => new UserChatMessage(message.Content),
};

internal sealed record DeploymentInfo(string Deployment);

internal sealed record OpenAiChatRequest(
    string? Model,
    List<OpenAiMessage> Messages,
    bool? Stream = false);

internal sealed record OpenAiMessage(string Role, string Content);
