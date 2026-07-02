using System.Text.Json;
using AFClaude;
using OpenAI.Chat;

namespace AFClaude.Tests;

public class AnthropicStreamingTests
{
    private static string J(object payload) => JsonSerializer.Serialize(payload);

    private static List<AnthropicStreamTranslator.SseEvent> Run(
        AnthropicStreamTranslator translator, params StreamingChatCompletionUpdate[] updates)
    {
        var events = new List<AnthropicStreamTranslator.SseEvent> { translator.Start() };
        foreach (var update in updates)
        {
            events.AddRange(translator.Translate(update));
        }
        events.AddRange(translator.Finish());
        return events;
    }

    [Fact]
    public void TextOnly_StreamsIncrementalTextDeltas()
    {
        var translator = new AnthropicStreamTranslator("msg_1", "gpt-test");
        var events = Run(translator,
            OpenAIChatModelFactory.StreamingChatCompletionUpdate(
                contentUpdate: new ChatMessageContent("Hel")),
            OpenAIChatModelFactory.StreamingChatCompletionUpdate(
                contentUpdate: new ChatMessageContent("lo")),
            OpenAIChatModelFactory.StreamingChatCompletionUpdate(
                finishReason: ChatFinishReason.Stop,
                usage: OpenAIChatModelFactory.ChatTokenUsage(outputTokenCount: 2, inputTokenCount: 10, totalTokenCount: 12)));

        Assert.Equal(
            ["message_start", "content_block_start", "content_block_delta", "content_block_delta", "content_block_stop", "message_delta", "message_stop"],
            events.Select(e => e.Name));
        Assert.Contains("\"text\":\"Hel\"", J(events[2].Payload));
        Assert.Contains("\"text\":\"lo\"", J(events[3].Payload));
        Assert.Contains("\"stop_reason\":\"end_turn\"", J(events[5].Payload));
        Assert.Contains("\"output_tokens\":2", J(events[5].Payload));
    }

    [Fact]
    public void ToolCall_StreamsToolUseBlockWithJsonDeltas()
    {
        var translator = new AnthropicStreamTranslator("msg_1", "gpt-test");
        var events = Run(translator,
            OpenAIChatModelFactory.StreamingChatCompletionUpdate(
                toolCallUpdates:
                [
                    OpenAIChatModelFactory.StreamingChatToolCallUpdate(
                        index: 0, toolCallId: "call_1", functionName: "Read",
                        functionArgumentsUpdate: BinaryData.FromString("""{"file_"""))
                ]),
            OpenAIChatModelFactory.StreamingChatCompletionUpdate(
                toolCallUpdates:
                [
                    OpenAIChatModelFactory.StreamingChatToolCallUpdate(
                        index: 0, functionArgumentsUpdate: BinaryData.FromString("""path":"x"}"""))
                ]),
            OpenAIChatModelFactory.StreamingChatCompletionUpdate(finishReason: ChatFinishReason.ToolCalls));

        Assert.Equal(
            ["message_start", "content_block_start", "content_block_delta", "content_block_delta", "content_block_stop", "message_delta", "message_stop"],
            events.Select(e => e.Name));
        var start = J(events[1].Payload);
        Assert.Contains("\"type\":\"tool_use\"", start);
        Assert.Contains("\"id\":\"call_1\"", start);
        Assert.Contains("\"name\":\"Read\"", start);
        Assert.Contains("input_json_delta", J(events[2].Payload));
        // The two argument fragments concatenate to the full JSON.
        Assert.Contains("{\\u0022file_", J(events[2].Payload).Replace("\\u0022", "\\u0022"));
        Assert.Contains("\"stop_reason\":\"tool_use\"", J(events[5].Payload));
    }

    [Fact]
    public void TextThenTool_UsesSequentialBlockIndices()
    {
        var translator = new AnthropicStreamTranslator("msg_1", "gpt-test");
        var events = Run(translator,
            OpenAIChatModelFactory.StreamingChatCompletionUpdate(
                contentUpdate: new ChatMessageContent("thinking...")),
            OpenAIChatModelFactory.StreamingChatCompletionUpdate(
                toolCallUpdates:
                [
                    OpenAIChatModelFactory.StreamingChatToolCallUpdate(
                        index: 0, toolCallId: "call_1", functionName: "Read",
                        functionArgumentsUpdate: BinaryData.FromString("{}"))
                ]),
            OpenAIChatModelFactory.StreamingChatCompletionUpdate(finishReason: ChatFinishReason.ToolCalls));

        Assert.Equal(
            ["message_start",
             "content_block_start", "content_block_delta", "content_block_stop",   // text block, index 0
             "content_block_start", "content_block_delta", "content_block_stop",   // tool block, index 1
             "message_delta", "message_stop"],
            events.Select(e => e.Name));
        Assert.Contains("\"index\":0", J(events[1].Payload));
        Assert.Contains("\"index\":1", J(events[4].Payload));
    }

    [Fact]
    public void EmptyCompletion_StillEmitsOneEmptyTextBlock()
    {
        var translator = new AnthropicStreamTranslator("msg_1", "gpt-test");
        var events = Run(translator,
            OpenAIChatModelFactory.StreamingChatCompletionUpdate(finishReason: ChatFinishReason.Stop));

        Assert.Equal(
            ["message_start", "content_block_start", "content_block_stop", "message_delta", "message_stop"],
            events.Select(e => e.Name));
    }
}
