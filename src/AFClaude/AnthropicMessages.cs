using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AFClaude;

internal sealed record AnthropicMessagesRequest(
    string? Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    List<AnthropicMessageIn> Messages,
    JsonElement System = default,
    bool Stream = false);

internal sealed record AnthropicMessageIn(string Role, JsonElement Content);

// Chat-only translation: concatenates text-type content blocks and ignores
// tool_use/tool_result/image/etc. Claude Code's tool-driven behavior (Read, Edit,
// Bash, ...) will not function against this endpoint yet — see PLAN.md.
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
