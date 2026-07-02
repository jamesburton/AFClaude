using AFClaude;
using Microsoft.Agents.AI;
using OpenAI.Chat;

var useHttp = args.Contains("--http", StringComparer.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("AFClaude__Mode"), "http", StringComparison.OrdinalIgnoreCase);

if (useHttp)
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
    var builder = WebApplication.CreateBuilder(args);

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

    await app.RunAsync();
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
