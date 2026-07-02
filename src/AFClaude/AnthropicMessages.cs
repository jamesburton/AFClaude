using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAI.Chat;

namespace AFClaude;

internal sealed record AnthropicMessagesRequest(
    string? Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    List<AnthropicMessageIn> Messages,
    JsonElement System = default,
    bool Stream = false,
    List<AnthropicToolIn>? Tools = null,
    [property: JsonPropertyName("tool_choice")] JsonElement ToolChoice = default,
    double? Temperature = null,
    [property: JsonPropertyName("top_p")] double? TopP = null,
    [property: JsonPropertyName("stop_sequences")] List<string>? StopSequences = null);

internal sealed record AnthropicMessageIn(string Role, JsonElement Content);

// A tool definition from the request's `tools` array. Built-in/server tool types
// (web_search etc.) carry no input_schema and are skipped by the bridge.
internal sealed record AnthropicToolIn(
    string? Name,
    string? Description,
    [property: JsonPropertyName("input_schema")] JsonElement InputSchema = default);

// One content block of the response we will send back, in Anthropic terms.
internal abstract record AnthropicOutBlock;

internal sealed record AnthropicTextBlock(string Text) : AnthropicOutBlock;

internal sealed record AnthropicToolUseBlock(string Id, string Name, string ArgumentsJson) : AnthropicOutBlock;

internal static class AnthropicContent
{
    public static string ExtractText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "text"
                && block.TryGetProperty("text", out var text))
            {
                sb.Append(text.GetString());
            }
        }

        return sb.ToString();
    }
}

// Bidirectional translation between the Anthropic Messages API shape and Azure
// OpenAI chat/function-calling. Covers text, tool definitions, tool_use blocks in
// assistant history, and tool_result blocks in user history. Blocks with no OpenAI
// counterpart (thinking, image, ...) are dropped.
internal static class AnthropicBridge
{
    public static List<ChatMessage> ToChatMessages(AnthropicMessagesRequest request)
    {
        var messages = new List<ChatMessage>();

        if (request.System.ValueKind is JsonValueKind.String or JsonValueKind.Array)
        {
            var systemText = AnthropicContent.ExtractText(request.System);
            if (!string.IsNullOrEmpty(systemText))
            {
                messages.Add(new SystemChatMessage(systemText));
            }
        }

        foreach (var m in request.Messages)
        {
            var isAssistant = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase);

            if (m.Content.ValueKind == JsonValueKind.String)
            {
                var text = m.Content.GetString() ?? string.Empty;
                messages.Add(isAssistant ? new AssistantChatMessage(text) : new UserChatMessage(text));
                continue;
            }

            if (m.Content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            if (isAssistant)
            {
                AddAssistantMessage(messages, m.Content);
            }
            else
            {
                AddUserMessage(messages, m.Content);
            }
        }

        return messages;
    }

    private static void AddAssistantMessage(List<ChatMessage> messages, JsonElement content)
    {
        var text = new StringBuilder();
        var toolCalls = new List<ChatToolCall>();

        foreach (var block in content.EnumerateArray())
        {
            switch (BlockType(block))
            {
                case "text":
                    if (block.TryGetProperty("text", out var t))
                    {
                        text.Append(t.GetString());
                    }
                    break;

                case "tool_use":
                    if (block.TryGetProperty("id", out var id) && block.TryGetProperty("name", out var name))
                    {
                        var arguments = block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
                            ? input.GetRawText()
                            : "{}";
                        toolCalls.Add(ChatToolCall.CreateFunctionToolCall(
                            id.GetString()!, name.GetString()!, BinaryData.FromString(arguments)));
                    }
                    break;
            }
        }

        if (toolCalls.Count > 0)
        {
            var message = new AssistantChatMessage(toolCalls);
            if (text.Length > 0)
            {
                message.Content.Add(ChatMessageContentPart.CreateTextPart(text.ToString()));
            }
            messages.Add(message);
        }
        else
        {
            messages.Add(new AssistantChatMessage(text.ToString()));
        }
    }

    private static void AddUserMessage(List<ChatMessage> messages, JsonElement content)
    {
        // Anthropic embeds tool_result blocks inside the next user message; OpenAI
        // wants them as standalone tool-role messages directly after the assistant
        // message that made the calls — so tool results are emitted before any text.
        var text = new StringBuilder();

        foreach (var block in content.EnumerateArray())
        {
            switch (BlockType(block))
            {
                case "text":
                    if (block.TryGetProperty("text", out var t))
                    {
                        text.Append(t.GetString());
                    }
                    break;

                case "tool_result":
                    if (block.TryGetProperty("tool_use_id", out var toolUseId))
                    {
                        var resultText = block.TryGetProperty("content", out var c)
                            ? AnthropicContent.ExtractText(c)
                            : string.Empty;
                        messages.Add(new ToolChatMessage(toolUseId.GetString()!, resultText));
                    }
                    break;
            }
        }

        if (text.Length > 0)
        {
            messages.Add(new UserChatMessage(text.ToString()));
        }
    }

    public static ChatCompletionOptions ToOptions(AnthropicMessagesRequest request)
    {
        var options = new ChatCompletionOptions();

        if (request.MaxTokens > 0)
        {
            options.MaxOutputTokenCount = request.MaxTokens;
        }
        if (request.Temperature is { } temperature)
        {
            options.Temperature = (float)temperature;
        }
        if (request.TopP is { } topP)
        {
            options.TopP = (float)topP;
        }
        foreach (var stop in request.StopSequences ?? [])
        {
            options.StopSequences.Add(stop);
        }

        foreach (var tool in request.Tools ?? [])
        {
            if (string.IsNullOrEmpty(tool.Name) || tool.InputSchema.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            options.Tools.Add(ChatTool.CreateFunctionTool(
                tool.Name, tool.Description, BinaryData.FromString(tool.InputSchema.GetRawText())));
        }

        if (request.ToolChoice.ValueKind == JsonValueKind.Object
            && request.ToolChoice.TryGetProperty("type", out var choiceType))
        {
            options.ToolChoice = choiceType.GetString() switch
            {
                "any" => ChatToolChoice.CreateRequiredChoice(),
                "none" => ChatToolChoice.CreateNoneChoice(),
                "tool" when request.ToolChoice.TryGetProperty("name", out var name)
                    => ChatToolChoice.CreateFunctionChoice(name.GetString()!),
                _ => ChatToolChoice.CreateAutoChoice(),
            };

            if (request.ToolChoice.TryGetProperty("disable_parallel_tool_use", out var disableParallel)
                && disableParallel.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                options.AllowParallelToolCalls = !disableParallel.GetBoolean();
            }
        }

        return options;
    }

    public static List<AnthropicOutBlock> ToOutBlocks(ChatCompletion completion)
    {
        var blocks = new List<AnthropicOutBlock>();

        var text = new StringBuilder();
        foreach (var part in completion.Content)
        {
            if (part.Text is { Length: > 0 })
            {
                text.Append(part.Text);
            }
        }
        if (text.Length > 0)
        {
            blocks.Add(new AnthropicTextBlock(text.ToString()));
        }

        foreach (var call in completion.ToolCalls)
        {
            var arguments = call.FunctionArguments?.ToString();
            blocks.Add(new AnthropicToolUseBlock(
                call.Id,
                call.FunctionName,
                string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments));
        }

        return blocks;
    }

    public static string MapStopReason(ChatFinishReason reason, bool hasToolUse) => hasToolUse
        ? "tool_use"
        : reason switch
        {
            ChatFinishReason.Length => "max_tokens",
            ChatFinishReason.ToolCalls or ChatFinishReason.FunctionCall => "tool_use",
            _ => "end_turn",
        };

    public static object ToJson(AnthropicOutBlock block) => block switch
    {
        AnthropicTextBlock text => new { type = "text", text = text.Text },
        AnthropicToolUseBlock toolUse => (object)new
        {
            type = "tool_use",
            id = toolUse.Id,
            name = toolUse.Name,
            input = ParseInput(toolUse.ArgumentsJson),
        },
        _ => throw new InvalidOperationException($"Unknown block type: {block.GetType().Name}"),
    };

    // The model's function-call arguments should be valid JSON, but a malformed
    // payload must degrade to an empty input, not a 500.
    private static JsonElement ParseInput(string argumentsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        }
        catch (JsonException)
        {
            return JsonSerializer.Deserialize<JsonElement>("{}");
        }
    }

    private static string? BlockType(JsonElement block)
        => block.ValueKind == JsonValueKind.Object
            && block.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null;
}
