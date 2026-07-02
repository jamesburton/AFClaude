using System.Text.Json;
using AFClaude;
using OpenAI.Chat;

namespace AFClaude.Tests;

public class AnthropicBridgeTests
{
    // Mirrors minimal-API JSON binding (camelCase, case-insensitive).
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static AnthropicMessagesRequest Parse(string json)
        => JsonSerializer.Deserialize<AnthropicMessagesRequest>(json, Web)!;

    private const string ToolConversationJson = """
        {
          "model": "gpt-test",
          "max_tokens": 512,
          "system": "You are a coding agent.",
          "tools": [
            {
              "name": "read_file",
              "description": "Read a file from disk",
              "input_schema": {
                "type": "object",
                "properties": { "path": { "type": "string" } },
                "required": ["path"]
              }
            },
            { "type": "web_search_20250305", "name": "web_search" }
          ],
          "tool_choice": { "type": "auto", "disable_parallel_tool_use": true },
          "messages": [
            { "role": "user", "content": "Read config.json please" },
            {
              "role": "assistant",
              "content": [
                { "type": "text", "text": "Reading it now." },
                { "type": "tool_use", "id": "toolu_01abc", "name": "read_file", "input": { "path": "config.json" } }
              ]
            },
            {
              "role": "user",
              "content": [
                { "type": "tool_result", "tool_use_id": "toolu_01abc", "content": [ { "type": "text", "text": "{\"key\": 1}" } ] },
                { "type": "text", "text": "Now summarize it." }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void ToChatMessages_TranslatesToolUseAndToolResultHistory()
    {
        var messages = AnthropicBridge.ToChatMessages(Parse(ToolConversationJson));

        Assert.Collection(messages,
            m =>
            {
                var system = Assert.IsType<SystemChatMessage>(m);
                Assert.Equal("You are a coding agent.", system.Content[0].Text);
            },
            m =>
            {
                var user = Assert.IsType<UserChatMessage>(m);
                Assert.Equal("Read config.json please", user.Content[0].Text);
            },
            m =>
            {
                var assistant = Assert.IsType<AssistantChatMessage>(m);
                var call = Assert.Single(assistant.ToolCalls);
                Assert.Equal("toolu_01abc", call.Id);
                Assert.Equal("read_file", call.FunctionName);
                Assert.Equal("""{ "path": "config.json" }""", call.FunctionArguments.ToString());
                Assert.Equal("Reading it now.", assistant.Content[0].Text);
            },
            m =>
            {
                var tool = Assert.IsType<ToolChatMessage>(m);
                Assert.Equal("toolu_01abc", tool.ToolCallId);
                Assert.Equal("""{"key": 1}""", tool.Content[0].Text);
            },
            m =>
            {
                var user = Assert.IsType<UserChatMessage>(m);
                Assert.Equal("Now summarize it.", user.Content[0].Text);
            });
    }

    [Fact]
    public void ToOptions_BridgesFunctionToolsAndSkipsBuiltIns()
    {
        var options = AnthropicBridge.ToOptions(Parse(ToolConversationJson));

        var tool = Assert.Single(options.Tools);
        Assert.Equal("read_file", tool.FunctionName);
        Assert.Equal("Read a file from disk", tool.FunctionDescription);
        Assert.Contains("\"path\"", tool.FunctionParameters.ToString());

        Assert.Equal(512, options.MaxOutputTokenCount);
        Assert.NotNull(options.ToolChoice);
        Assert.False(options.AllowParallelToolCalls);
    }

    [Theory]
    [InlineData("""{"type": "any"}""")]
    [InlineData("""{"type": "tool", "name": "read_file"}""")]
    [InlineData("""{"type": "none"}""")]
    public void ToOptions_MapsEveryToolChoiceShape(string toolChoiceJson)
    {
        var request = Parse($$"""
            {"model": "m", "max_tokens": 1, "messages": [], "tools": [], "tool_choice": {{toolChoiceJson}}}
            """);

        Assert.NotNull(AnthropicBridge.ToOptions(request).ToolChoice);
    }

    [Fact]
    public void ToOutBlocks_MapsTextAndFunctionCallsToAnthropicBlocks()
    {
        var completion = OpenAIChatModelFactory.ChatCompletion(
            id: "chatcmpl-1",
            finishReason: ChatFinishReason.ToolCalls,
            content: new ChatMessageContent("I'll read that file."),
            toolCalls:
            [
                ChatToolCall.CreateFunctionToolCall(
                    "call_1", "read_file", BinaryData.FromString("""{"path":"config.json"}"""))
            ]);

        var blocks = AnthropicBridge.ToOutBlocks(completion);

        Assert.Collection(blocks,
            b => Assert.Equal("I'll read that file.", Assert.IsType<AnthropicTextBlock>(b).Text),
            b =>
            {
                var toolUse = Assert.IsType<AnthropicToolUseBlock>(b);
                Assert.Equal("call_1", toolUse.Id);
                Assert.Equal("read_file", toolUse.Name);
                Assert.Equal("""{"path":"config.json"}""", toolUse.ArgumentsJson);
            });

        Assert.Equal("tool_use", AnthropicBridge.MapStopReason(
            completion.FinishReason, blocks.Any(b => b is AnthropicToolUseBlock)));
    }

    [Theory]
    [InlineData(ChatFinishReason.Stop, false, "end_turn")]
    [InlineData(ChatFinishReason.Length, false, "max_tokens")]
    [InlineData(ChatFinishReason.ToolCalls, true, "tool_use")]
    [InlineData(ChatFinishReason.Stop, true, "tool_use")]
    public void MapStopReason_CoversFinishReasons(ChatFinishReason reason, bool hasToolUse, string expected)
        => Assert.Equal(expected, AnthropicBridge.MapStopReason(reason, hasToolUse));

    [Fact]
    public void ToJson_ToolUseBlock_ParsesArgumentsIntoInputObject()
    {
        var json = JsonSerializer.Serialize(
            AnthropicBridge.ToJson(new AnthropicToolUseBlock("call_1", "read_file", """{"path":"x"}""")));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("tool_use", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("call_1", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("read_file", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("x", doc.RootElement.GetProperty("input").GetProperty("path").GetString());
    }

    [Fact]
    public void ToJson_ToolUseBlock_MalformedArgumentsDegradeToEmptyInput()
    {
        var json = JsonSerializer.Serialize(
            AnthropicBridge.ToJson(new AnthropicToolUseBlock("call_1", "read_file", "{not json")));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("input").ValueKind);
        Assert.Empty(doc.RootElement.GetProperty("input").EnumerateObject());
    }

    [Fact]
    public void ToChatMessages_PlainStringContentStillWorks()
    {
        var request = Parse("""
            {
              "model": "m",
              "max_tokens": 10,
              "messages": [
                { "role": "user", "content": "hi" },
                { "role": "assistant", "content": "hello" }
              ]
            }
            """);

        var messages = AnthropicBridge.ToChatMessages(request);

        Assert.Collection(messages,
            m => Assert.IsType<UserChatMessage>(m),
            m => Assert.IsType<AssistantChatMessage>(m));
    }
}
