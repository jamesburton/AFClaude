using OpenAI.Chat;

namespace AFClaude;

// Translates an OpenAI streaming chat completion into Anthropic SSE events,
// incrementally — the real-streaming replacement for the old coalesced burst on the
// bridge path. One Anthropic content block is open at a time: text deltas stream into
// a text block, each OpenAI tool-call index becomes a tool_use block whose argument
// fragments stream as input_json_delta. Callers write the returned events in order.
internal sealed class AnthropicStreamTranslator(string messageId, string model)
{
    internal sealed record SseEvent(string Name, object Payload);

    private enum OpenBlock
    {
        None,
        Text,
        Tool,
    }

    private int _blockIndex = -1;
    private OpenBlock _open = OpenBlock.None;
    private int _openToolIndex = -1;
    private bool _sawToolUse;
    private ChatFinishReason? _finishReason;
    private int _inputTokens;
    private int _outputTokens;

    public SseEvent Start() => new("message_start", new
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
            usage = new { input_tokens = 0, output_tokens = 0 },
        },
    });

    public IEnumerable<SseEvent> Translate(StreamingChatCompletionUpdate update)
    {
        foreach (var part in update.ContentUpdate)
        {
            if (part.Text is not { Length: > 0 } text)
            {
                continue;
            }

            if (_open != OpenBlock.Text)
            {
                foreach (var e in CloseOpenBlock())
                {
                    yield return e;
                }
                _open = OpenBlock.Text;
                _blockIndex++;
                yield return new SseEvent("content_block_start", new
                {
                    type = "content_block_start",
                    index = _blockIndex,
                    content_block = new { type = "text", text = "" },
                });
            }

            yield return new SseEvent("content_block_delta", new
            {
                type = "content_block_delta",
                index = _blockIndex,
                delta = new { type = "text_delta", text },
            });
        }

        foreach (var toolCall in update.ToolCallUpdates)
        {
            if (_open != OpenBlock.Tool || _openToolIndex != toolCall.Index)
            {
                foreach (var e in CloseOpenBlock())
                {
                    yield return e;
                }
                _open = OpenBlock.Tool;
                _openToolIndex = toolCall.Index;
                _sawToolUse = true;
                _blockIndex++;
                // OpenAI sends the id and function name on the first fragment of
                // each tool-call index; later fragments carry only argument bytes.
                yield return new SseEvent("content_block_start", new
                {
                    type = "content_block_start",
                    index = _blockIndex,
                    content_block = new
                    {
                        type = "tool_use",
                        id = toolCall.ToolCallId ?? $"call_{toolCall.Index}",
                        name = toolCall.FunctionName ?? string.Empty,
                        input = new { },
                    },
                });
            }

            var fragment = toolCall.FunctionArgumentsUpdate?.ToString();
            if (!string.IsNullOrEmpty(fragment))
            {
                yield return new SseEvent("content_block_delta", new
                {
                    type = "content_block_delta",
                    index = _blockIndex,
                    delta = new { type = "input_json_delta", partial_json = fragment },
                });
            }
        }

        if (update.FinishReason is { } finish)
        {
            _finishReason = finish;
        }
        if (update.Usage is { } usage)
        {
            _inputTokens = usage.InputTokenCount;
            _outputTokens = usage.OutputTokenCount;
        }
    }

    public IEnumerable<SseEvent> Finish()
    {
        foreach (var e in CloseOpenBlock())
        {
            yield return e;
        }

        // An empty completion still needs one (empty) text block for a valid message.
        if (_blockIndex < 0)
        {
            yield return new SseEvent("content_block_start", new
            {
                type = "content_block_start",
                index = 0,
                content_block = new { type = "text", text = "" },
            });
            yield return new SseEvent("content_block_stop", new { type = "content_block_stop", index = 0 });
        }

        yield return new SseEvent("message_delta", new
        {
            type = "message_delta",
            delta = new
            {
                stop_reason = AnthropicBridge.MapStopReason(_finishReason ?? ChatFinishReason.Stop, _sawToolUse),
                stop_sequence = (string?)null,
            },
            usage = new { input_tokens = _inputTokens, output_tokens = _outputTokens },
        });

        yield return new SseEvent("message_stop", new { type = "message_stop" });
    }

    private IEnumerable<SseEvent> CloseOpenBlock()
    {
        if (_open != OpenBlock.None)
        {
            yield return new SseEvent("content_block_stop", new { type = "content_block_stop", index = _blockIndex });
            _open = OpenBlock.None;
            _openToolIndex = -1;
        }
    }
}
