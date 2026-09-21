using System.Text.RegularExpressions;

namespace AFClaude;

// Strict body mode's reactive half. Foundry's hosted Claude endpoint validates request
// bodies more strictly than the real Anthropic API and 400s on anything it doesn't
// support -- but its error messages say exactly what it rejected (and, for tool types,
// exactly what it accepts). Rather than guessing up front, FoundryAnthropicClient sends
// the request optimistically, feeds any 400 through TryLearn, and retries with the
// learned restrictions; the knowledge is kept for the rest of the process, so only the
// first affected request pays the extra round trip. Learning from Foundry's own answer
// (instead of a hardcoded list) self-corrects in both directions: when Foundry starts
// accepting a new tool type, nothing here keeps stripping it.
internal sealed partial class FoundryRejectionLearner
{
    // Fallback for when Foundry rejects a tool type but its error doesn't carry a
    // parseable accepted-tags list. Copied from the live error (observed with
    // claude-sonnet-5: "tools.190: Input tag 'advisor_20260301' found using 'type' does
    // not match any of the expected tags: ...").
    internal static readonly IReadOnlySet<string> KnownToolTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "custom",
        "bash_20250124",
        "browser_toolset_20260801",
        "code_execution_20250522", "code_execution_20250825", "code_execution_20260120", "code_execution_20260521",
        "computer_toolset_20260801",
        "memory_20250818",
        "text_editor_20250124", "text_editor_20250429", "text_editor_20250728",
        "tool_search_tool_bm25", "tool_search_tool_bm25_20251119",
        "tool_search_tool_regex", "tool_search_tool_regex_20251119",
        "web_fetch_20250910", "web_fetch_20260209", "web_fetch_20260309", "web_fetch_20260318",
        "web_search_20250305", "web_search_20260209", "web_search_20260318",
    };

    // Never learned as droppable: without these the request is meaningless, so a
    // rejection naming one is a real error to surface, not something to strip.
    private static readonly HashSet<string> RequiredFields = new(StringComparer.Ordinal)
    {
        "model", "messages", "max_tokens",
    };

    private readonly object _lock = new();
    private IReadOnlySet<string>? _acceptedToolTypes;
    private readonly HashSet<string> _rejectedFields = new(StringComparer.Ordinal);

    /// <summary>
    /// Typed tool entries Foundry accepts, or null while nothing has been learned yet
    /// (in which case every tool is sent as-is).
    /// </summary>
    public IReadOnlySet<string>? AcceptedToolTypes
    {
        get
        {
            lock (_lock)
            {
                return _acceptedToolTypes;
            }
        }
    }

    /// <summary>Whether Foundry has rejected this top-level body field before.</summary>
    /// <param name="field">The top-level request body field name.</param>
    /// <returns><see langword="true"/> if the field should be dropped from future requests.</returns>
    public bool IsRejectedField(string field)
    {
        lock (_lock)
        {
            return _rejectedFields.Contains(field);
        }
    }

    /// <summary>
    /// Learns from a Foundry 400 response body. Recognises a rejected tool type
    /// (adopting Foundry's accepted-tags list, or <see cref="KnownToolTypes"/> when the
    /// list can't be parsed) and rejected top-level fields ("Extra inputs are not
    /// permitted").
    /// </summary>
    /// <param name="errorText">The raw 400 response body.</param>
    /// <returns>
    /// <see langword="true"/> only if something NEW was learned, i.e. a retry would
    /// send a different body; <see langword="false"/> means the error is real.
    /// </returns>
    public bool TryLearn(string errorText)
    {
        var learned = false;
        lock (_lock)
        {
            var toolTag = ToolTagRejection().Match(errorText);
            if (toolTag.Success)
            {
                var expected = QuotedTag().Matches(toolTag.Groups["expected"].Value)
                    .Select(m => m.Groups[1].Value)
                    .ToHashSet(StringComparer.Ordinal);
                expected.Add("custom");

                // The list must actually exclude the rejected tag, or it's not the list
                // we think it is -- fall back to the known list rather than trust it.
                IReadOnlySet<string> accepted = expected.Count > 1 && !expected.Contains(toolTag.Groups["tag"].Value)
                    ? expected
                    : KnownToolTypes;
                if (_acceptedToolTypes is null || !_acceptedToolTypes.SetEquals(accepted))
                {
                    _acceptedToolTypes = accepted;
                    learned = true;
                }
            }

            foreach (Match field in ExtraFieldRejection().Matches(errorText))
            {
                var name = field.Groups["field"].Value;
                if (!RequiredFields.Contains(name) && _rejectedFields.Add(name))
                {
                    learned = true;
                }
            }
        }
        return learned;
    }

    // The accepted list runs to the end of the message; stopping at a quote or line
    // break keeps it inside the JSON string it's embedded in.
    [GeneratedRegex("""Input tag '(?<tag>[^']+)' found using 'type' does not match any of the expected tags:(?<expected>[^"\r\n]*)""")]
    private static partial Regex ToolTagRejection();

    [GeneratedRegex("'([^']+)'")]
    private static partial Regex QuotedTag();

    // Top-level fields only: the lookbehind rejects dotted paths like
    // "tools.3.cache_control: Extra inputs...", which this layer can't safely strip.
    [GeneratedRegex(@"(?<![\w.])(?<field>[A-Za-z_]\w*): Extra inputs are not permitted")]
    private static partial Regex ExtraFieldRejection();
}
