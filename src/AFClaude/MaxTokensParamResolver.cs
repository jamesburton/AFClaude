using System.ClientModel;

namespace AFClaude;

// Some Azure OpenAI-compatible deployments require the modern `max_completion_tokens`
// wire field instead of the legacy `max_tokens` the SDK defaults to (see
// FoundryClientFactory's Foundry:MaxTokensParam comment) -- and there's no reliable way
// to know which in advance; it depends on the deployment, not just the model family,
// and shifts as new model generations ship (observed live: gpt-5.6 needs the new field,
// gpt-4.1 doesn't). auto (default, `configured: null`) starts optimistic with legacy and
// self-heals the first time a request is rejected for exactly this reason, caching the
// result so every later request on this process skips straight to the right field.
internal sealed class MaxTokensParamResolver
{
    private readonly bool? _configured;
    private readonly object _lock = new();
    private bool? _resolved;

    public MaxTokensParamResolver(bool? configured)
    {
        _configured = configured;
        _resolved = configured;
    }

    public bool? Configured => _configured;

    // legacy (false) is the optimistic first guess while still unresolved in auto mode.
    public bool Current
    {
        get
        {
            lock (_lock)
            {
                return _resolved ?? false;
            }
        }
    }

    // Called only after a completion request fails with IsMaxTokensRejected.
    // attemptedWithNew is the field THAT request actually sent (captured when its
    // options were built, not re-read from Current): deciding on it rather than on
    // "did I win the flip" matters under concurrency -- Claude Code fires several
    // requests at once, and every one that went out with legacy before the first flip
    // deserves its own retry. Returns false when pinned explicitly (never self-heals)
    // or when the request already used the modern field (retrying would be futile --
    // the caller should treat the failure as real). A retry always sends the modern
    // field, so this can't loop.
    public bool ShouldRetryWithNew(bool attemptedWithNew)
    {
        if (_configured is not null || attemptedWithNew)
        {
            return false;
        }
        lock (_lock)
        {
            _resolved = true;
        }
        return true;
    }

    // Azure's real error text for this failure: "Unsupported parameter: 'max_tokens'
    // is not supported with this model. Use 'max_completion_tokens' instead." Matching
    // on both field names (rather than just the status code) avoids misclassifying an
    // unrelated 400.
    public static bool IsMaxTokensRejected(Exception ex)
        => ex is ClientResultException { Status: 400 } cre
           && cre.Message.Contains("max_tokens", StringComparison.OrdinalIgnoreCase)
           && cre.Message.Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase);
}
