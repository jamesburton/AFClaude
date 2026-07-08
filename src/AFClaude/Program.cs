using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using AFClaude;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
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

    // interactiveAllowed: false -- MCP mode never has an operator present (Claude
    // launches it silently), so it can only passively load a saved config file, never
    // run the wizard.
    var mcpOverrides = await ResolveFoundryConfigOverridesAsync(builder.Configuration, args, interactiveAllowed: false);
    if (mcpOverrides.Count > 0)
    {
        builder.Configuration.AddInMemoryCollection(mcpOverrides);
    }

    var foundry = FoundryClientFactory.Create(builder.Configuration);
    AIAgent agent = foundry.ChatClient.AsAIAgent(
        instructions: "You are a local proxy agent. Preserve the caller's intent.");
    builder.Services.AddSingleton(agent);
    builder.Services.AddSingleton(foundry);

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync();
}

// OpenAI-compatible HTTP proxy — secondary path for other OpenAI-compatible clients.
static async Task RunHttpAsync(string[] args)
{
    var app = await BuildHttpAppAsync(args);
    try
    {
        await app.RunAsync();
    }
    catch (SocketException ex)
    {
        throw new InvalidOperationException(
            $"Could not bind the HTTP proxy ({ex.SocketErrorCode}). The configured address/port may be reserved by " +
            "Windows or already in use by another process -- pass a different address via --urls or ASPNETCORE_URLS.",
            ex);
    }
}

// launch: starts the HTTP host on a known local port, points Claude Code's own
// traffic at its Anthropic-compatible /v1/messages endpoint via ANTHROPIC_BASE_URL,
// and execs `claude` in the foreground with the remaining args forwarded to it.
static async Task RunLaunchAsync(string[] claudeArgs)
{
    var envConfig = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    var launchOverrides = await ResolveFoundryConfigOverridesAsync(envConfig, claudeArgs, interactiveAllowed: true);
    var configBuilder = new ConfigurationBuilder().AddEnvironmentVariables();
    if (launchOverrides.Count > 0)
    {
        configBuilder.AddInMemoryCollection(launchOverrides);
    }
    var config = configBuilder.Build();
    var foundry = FoundryClientFactory.Create(config); // fail fast before starting anything
    var deployment = foundry.Deployment;

    // Both spellings: AFClaude__Launch__Port (documented) and Launch__Port. Unset ->
    // bind to port 0 (OS-assigned) rather than a fixed default: Windows reserves
    // arbitrary TCP port ranges per machine/reboot (observed live: Hyper-V/WSL NAT
    // exclusions covering e.g. 31291-31390, which silently ate the old hardcoded 31337
    // default with a raw SocketException). Letting the OS pick sidesteps that whole
    // class of failure; only an explicit override can still collide, in which case we
    // fail with a clean message instead of the raw exception.
    var configuredPort = config.GetValue<int?>("AFClaude:Launch:Port") ?? config.GetValue<int?>("Launch:Port");
    var bindUrl = $"http://127.0.0.1:{configuredPort ?? 0}";

    var app = BuildHttpApp([], bindUrl, foundry);
    try
    {
        await app.StartAsync();
    }
    catch (SocketException ex)
    {
        await app.StopAsync();
        throw configuredPort is int explicitPort
            ? new InvalidOperationException(
                $"Could not bind the local proxy to port {explicitPort} ({ex.SocketErrorCode}), set via " +
                "AFClaude__Launch__Port/Launch__Port. The port may be reserved by Windows or already in use -- " +
                "pick a different port, or unset the variable to let AFClaude choose a free one automatically.",
                ex)
            : new InvalidOperationException(
                $"Could not bind the local proxy to an OS-assigned port ({ex.SocketErrorCode}).", ex);
    }

    // Read back the actual bound address -- port 0 above means Kestrel chose it.
    var baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    Console.Error.WriteLine($"AFClaude proxy listening on {baseUrl} (Foundry deployment: {deployment})");

    // Warm the Entra token before claude's first request: a cold `az` start can take
    // tens of seconds, and a broken-auth claude session is useless — better to fail
    // here with a classified message than let every claude turn 401.
    Console.Error.WriteLine("Acquiring Azure token via 'az' (first acquisition can take a while)...");
    try
    {
        await foundry.Credential.GetTokenAsync(
            new Azure.Core.TokenRequestContext([FoundryClientFactory.TokenScope]), CancellationToken.None);
        Console.Error.WriteLine("Azure token acquired.");

        var api = await foundry.Api.ResolveAsync(CancellationToken.None);
        Console.Error.WriteLine(api == FoundryApi.Anthropic
            ? "Native Anthropic (Claude) deployment detected — /v1/messages runs as a direct passthrough."
            : "OpenAI-compatible deployment — /v1/messages bridges Anthropic tool use to function-calling.");
    }
    catch (Exception ex)
    {
        await app.StopAsync();
        throw new InvalidOperationException(FoundryErrors.Classify(ex).Message, ex);
    }

    var psi = new ProcessStartInfo("claude") { UseShellExecute = false };
    foreach (var a in LaunchArgs.Translate(CliArgs.StripAfClaudeFlags(claudeArgs)))
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

// Resolves Foundry:Endpoint/Deployment/Api ahead of FoundryClientFactory.Create: env
// vars always win; otherwise an explicit/default saved config file; otherwise (only
// when interactiveAllowed and a real terminal is attached) the interactive picker.
// Returns overrides to layer on top of the caller's configuration -- never touches a
// key the caller's configuration already has a non-empty value for.
static async Task<Dictionary<string, string?>> ResolveFoundryConfigOverridesAsync(
    IConfiguration configuration, string[] args, bool interactiveAllowed)
{
    var overrides = new Dictionary<string, string?>();
    var selectRequested = CliArgs.HasSelectFlag(args);
    var explicitConfigPath = CliArgs.GetConfigPath(args);

    var hasEndpoint = !string.IsNullOrWhiteSpace(configuration["Foundry:Endpoint"]);
    var hasDeployment = !string.IsNullOrWhiteSpace(configuration["Foundry:Deployment"]);
    if (!selectRequested && hasEndpoint && hasDeployment)
    {
        return overrides; // nothing to resolve -- env vars/appsettings already cover it
    }

    // --select always re-runs the wizard, ignoring any existing saved config file
    // (this doubles as the "reset" case without needing a second flag).
    var resolved = selectRequested ? null : FoundryConfigFile.TryLoad(explicitConfigPath);

    if (resolved is null)
    {
        if (!interactiveAllowed || Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return overrides; // let FoundryClientFactory.Create's existing fail-fast fire
        }

        var suggestedFileName = explicitConfigPath ?? FoundryConfigFile.DefaultFileName;
        var timeoutSeconds = configuration.GetValue<int?>("Foundry:CliTimeoutSeconds") ?? 60;
        try
        {
            resolved = await FoundryConfigWizard.RunAsync(timeoutSeconds, suggestedFileName, CancellationToken.None);
        }
        catch (AzCliException)
        {
            throw; // already a clean, actionable message -- no reclassification needed
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(FoundryErrors.Classify(ex).Message, ex);
        }
    }

    if (!hasEndpoint)
    {
        overrides["Foundry:Endpoint"] = resolved.Endpoint;
    }
    if (!hasDeployment)
    {
        overrides["Foundry:Deployment"] = resolved.Deployment;
    }
    if (string.IsNullOrWhiteSpace(configuration["Foundry:Api"]))
    {
        overrides["Foundry:Api"] = resolved.Api;
    }
    return overrides;
}

// --http mode specifically: resolve Foundry config against the FULL WebApplication
// configuration stack (env vars, appsettings.json, command-line args, user-secrets),
// not just env vars, so those documented sources keep working and the interactive
// picker isn't spuriously triggered for someone relying on them. (launch mode has
// always been env-vars-only, unrelated to this fix.)
static async Task<WebApplication> BuildHttpAppAsync(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);
    var overrides = await ResolveFoundryConfigOverridesAsync(builder.Configuration, args, interactiveAllowed: true);
    if (overrides.Count > 0)
    {
        builder.Configuration.AddInMemoryCollection(overrides);
    }

    var foundry = FoundryClientFactory.Create(builder.Configuration);
    return BuildHttpApp(args, foundry: foundry, prebuiltBuilder: builder);
}

static WebApplication BuildHttpApp(string[] args, string? bindUrl = null, FoundryClient? foundry = null, WebApplicationBuilder? prebuiltBuilder = null)
{
    var builder = prebuiltBuilder ?? WebApplication.CreateBuilder(args);
    if (bindUrl is not null)
    {
        builder.WebHost.UseUrls(bindUrl);
    }

    foundry ??= FoundryClientFactory.Create(builder.Configuration);
    builder.Services.AddSingleton(foundry);
    builder.Services.AddSingleton(foundry.ChatClient);
    builder.Services.AddSingleton(new DeploymentInfo(foundry.Deployment));
    builder.Services.AddSingleton(new RequestTrace(builder.Configuration["AFClaude:TraceDir"]));

    var app = builder.Build();

    // claude's startup probe HEADs the base URL; answer instead of 404ing.
    app.MapMethods("/", ["GET", "HEAD"], () => Results.Text("AFClaude is running."));

    app.MapGet("/v1/models", (DeploymentInfo info) => Results.Json(new
    {
        @object = "list",
        data = new[]
        {
            new { id = info.Deployment, @object = "model", owned_by = "azure-foundry" }
        }
    }));

    // count_tokens exists only on the native Anthropic surface; on OpenAI-shaped
    // deployments it stays a 404 exactly as before.
    app.MapPost("/v1/messages/count_tokens", async (
        HttpContext http,
        FoundryClient foundry,
        RequestTrace trace,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        if (await foundry.Api.ResolveAsync(cancellationToken) != FoundryApi.Anthropic)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        string rawBody;
        using (var reader = new StreamReader(http.Request.Body))
        {
            rawBody = await reader.ReadToEndAsync(cancellationToken);
        }
        var seq = trace.Enabled ? trace.Next() : 0;
        trace.Write(seq, "count-tokens-request.json", rawBody);
        await ForwardAnthropicAsync(http, foundry.Anthropic, "messages/count_tokens", rawBody, trace, seq, logger, cancellationToken);
    });

    app.MapPost("/v1/chat/completions", async (
        HttpContext http,
        OpenAiChatRequest request,
        FoundryClient foundry,
        ChatClient chatClient,
        DeploymentInfo info,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        // OpenAI-shaped clients can't be served by a native Anthropic deployment
        // without a reverse bridge (not built — see PLAN.md); fail clearly.
        if (await foundry.Api.ResolveAsync(cancellationToken) == FoundryApi.Anthropic)
        {
            return Results.Json(
                new { error = new { message = "This Foundry deployment is a native Anthropic (Claude) deployment; /v1/chat/completions is not available. Use the Anthropic Messages endpoint (/v1/messages) or launch mode.", type = "invalid_request_error", code = (string?)null } },
                statusCode: StatusCodes.Status501NotImplemented);
        }

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
        catch (Exception ex)
        {
            var error = FoundryErrors.Classify(ex);
            logger.LogError(ex, "Chat completion failed ({Status})", error.Status);
            return Results.Json(
                new { error = new { message = error.Message, type = error.OpenAiType, code = (string?)null } },
                statusCode: error.Status);
        }
    });

    // Anthropic Messages API-compatible endpoint — what Claude Code's ANTHROPIC_BASE_URL
    // actually needs (it does not speak the OpenAI shape above). Bridges Anthropic
    // tools/tool_use/tool_result to Azure OpenAI function-calling in both directions.
    app.MapPost("/v1/messages", async (
        HttpContext http,
        FoundryClient foundry,
        ChatClient chatClient,
        DeploymentInfo info,
        RequestTrace trace,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        string rawBody;
        using (var reader = new StreamReader(http.Request.Body))
        {
            rawBody = await reader.ReadToEndAsync(cancellationToken);
        }

        var seq = trace.Enabled ? trace.Next() : 0;
        trace.Write(seq, "anthropic-request.json", rawBody);

        // Native Anthropic deployments (Claude on Foundry) take the passthrough path:
        // same wire format on both sides, so no translation — including streaming.
        FoundryApi api;
        try
        {
            api = await foundry.Api.ResolveAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            var error = FoundryErrors.Classify(ex);
            logger.LogError(ex, "Foundry API detection failed ({Status})", error.Status);
            http.Response.StatusCode = error.Status;
            await http.Response.WriteAsJsonAsync(
                new { type = "error", error = new { type = error.AnthropicType, message = error.Message } },
                cancellationToken);
            return;
        }

        if (api == FoundryApi.Anthropic)
        {
            await ForwardAnthropicAsync(http, foundry.Anthropic, "messages", rawBody, trace, seq, logger, cancellationToken);
            return;
        }

        AnthropicMessagesRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<AnthropicMessagesRequest>(rawBody, JsonSerializerOptions.Web);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Unparseable /v1/messages body");
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(
                new { type = "error", error = new { type = "invalid_request_error", message = "Request body is not a valid Anthropic Messages request." } },
                cancellationToken);
            return;
        }
        if (request?.Messages is null)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(
                new { type = "error", error = new { type = "invalid_request_error", message = "Request must include a messages array." } },
                cancellationToken);
            return;
        }

        var messages = AnthropicBridge.ToChatMessages(request);
        var options = AnthropicBridge.ToOptions(request);
        if (trace.Enabled)
        {
            trace.WriteAzureRequest(seq, messages, options);
        }

        var messageId = $"msg_{Guid.NewGuid():N}";
        var model = string.IsNullOrEmpty(request.Model) ? info.Deployment : request.Model;

        if (request.Stream)
        {
            await StreamBridgeAsync(http, chatClient, messages, options, messageId, model, trace, seq, logger, cancellationToken);
            return;
        }

        ChatCompletion completion;
        try
        {
            completion = await chatClient.CompleteChatAsync(messages, options, cancellationToken);
        }
        catch (Exception ex)
        {
            var error = FoundryErrors.Classify(ex);
            logger.LogError(ex, "Anthropic-compatible completion failed ({Status})", error.Status);
            http.Response.StatusCode = error.Status;
            await http.Response.WriteAsJsonAsync(
                new { type = "error", error = new { type = error.AnthropicType, message = error.Message } },
                cancellationToken);
            return;
        }

        if (trace.Enabled)
        {
            trace.WriteAzureResponse(seq, completion);
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

        if (trace.Enabled)
        {
            trace.WriteJson(seq, "anthropic-response.json", new
            {
                stream = false,
                stop_reason = stopReason,
                content = blocks.Select(AnthropicBridge.ToJson).ToArray(),
                usage = new { input_tokens = inputTokens, output_tokens = outputTokens },
            });
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

// Real incremental streaming for the bridge path: Azure's streaming chat completion
// is translated update-by-update into Anthropic SSE. Errors before the first update
// return a normal classified JSON error; mid-stream failures emit an Anthropic
// `error` event (headers are already sent by then).
static async Task StreamBridgeAsync(
    HttpContext http,
    ChatClient chatClient,
    List<ChatMessage> messages,
    ChatCompletionOptions options,
    string messageId,
    string model,
    RequestTrace trace,
    int seq,
    ILogger logger,
    CancellationToken cancellationToken)
{
    var translator = new AnthropicStreamTranslator(messageId, model);
    var traceSse = trace.Enabled ? new System.Text.StringBuilder() : null;
    var started = false;

    async Task EmitAsync(AnthropicStreamTranslator.SseEvent e)
    {
        var frame = $"event: {e.Name}\ndata: {JsonSerializer.Serialize(e.Payload)}\n\n";
        traceSse?.Append(frame);
        await http.Response.WriteAsync(frame, cancellationToken);
        await http.Response.Body.FlushAsync(cancellationToken);
    }

    async Task StartAsync()
    {
        started = true;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        await EmitAsync(translator.Start());
    }

    try
    {
        await foreach (var update in chatClient.CompleteChatStreamingAsync(messages, options, cancellationToken))
        {
            if (!started)
            {
                await StartAsync();
            }
            foreach (var e in translator.Translate(update))
            {
                await EmitAsync(e);
            }
        }

        if (!started)
        {
            await StartAsync();
        }
        foreach (var e in translator.Finish())
        {
            await EmitAsync(e);
        }
    }
    catch (Exception ex) when (!started)
    {
        var error = FoundryErrors.Classify(ex);
        logger.LogError(ex, "Streaming bridge completion failed before any output ({Status})", error.Status);
        http.Response.StatusCode = error.Status;
        await http.Response.WriteAsJsonAsync(
            new { type = "error", error = new { type = error.AnthropicType, message = error.Message } },
            cancellationToken);
        return;
    }
    catch (Exception ex)
    {
        var error = FoundryErrors.Classify(ex);
        logger.LogError(ex, "Streaming bridge failed mid-stream ({Status})", error.Status);
        await EmitAsync(new AnthropicStreamTranslator.SseEvent("error",
            new { type = "error", error = new { type = error.AnthropicType, message = error.Message } }));
    }

    if (traceSse is not null)
    {
        trace.Write(seq, "anthropic-response.sse.txt", traceSse.ToString());
    }
}

// Byte-faithful forward to the native Anthropic surface on the Foundry resource.
// Streaming responses (SSE) are relayed chunk-by-chunk — real incremental streaming.
static async Task ForwardAnthropicAsync(
    HttpContext http,
    FoundryAnthropicClient anthropic,
    string path,
    string rawBody,
    RequestTrace trace,
    int seq,
    ILogger logger,
    CancellationToken cancellationToken)
{
    var clientBeta = http.Request.Headers["anthropic-beta"].FirstOrDefault();
    if (!string.IsNullOrEmpty(clientBeta) && anthropic.BetaMode == FoundryAnthropicClient.BetaStrip)
    {
        logger.LogInformation(
            "Stripping client anthropic-beta '{Beta}' — Foundry rejects unknown beta flags (override via Foundry__AnthropicBeta)", clientBeta);
    }

    HttpResponseMessage upstream;
    try
    {
        upstream = await anthropic.ForwardAsync(
            rawBody,
            path,
            http.Request.Headers["anthropic-version"].FirstOrDefault(),
            clientBeta,
            cancellationToken,
            dropped => logger.LogInformation(
                "Dropping non-standard request field(s) {Fields} — Foundry rejects unknown body fields (override via Foundry__AnthropicBody)",
                string.Join(", ", dropped)));
    }
    catch (Exception ex)
    {
        var error = FoundryErrors.Classify(ex);
        logger.LogError(ex, "Anthropic passthrough failed before reaching Foundry ({Status})", error.Status);
        http.Response.StatusCode = error.Status;
        await http.Response.WriteAsJsonAsync(
            new { type = "error", error = new { type = error.AnthropicType, message = error.Message } },
            cancellationToken);
        return;
    }

    using (upstream)
    {
        http.Response.StatusCode = (int)upstream.StatusCode;
        var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
        http.Response.ContentType = contentType;

        if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            using var tee = trace.Enabled ? new MemoryStream() : null;
            await using var source = await upstream.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[8192];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await http.Response.Body.FlushAsync(cancellationToken);
                tee?.Write(buffer, 0, read);
            }
            if (tee is not null)
            {
                trace.Write(seq, "anthropic-response.sse.txt", System.Text.Encoding.UTF8.GetString(tee.ToArray()));
            }
        }
        else
        {
            var text = await upstream.Content.ReadAsStringAsync(cancellationToken);
            trace.Write(seq, "anthropic-response.json", text);
            await http.Response.WriteAsync(text, cancellationToken);
        }
    }
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
