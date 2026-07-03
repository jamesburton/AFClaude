using System.ClientModel;
using Azure;
using Azure.Identity;

namespace AFClaude;

// One classified error: the HTTP status to return, the error `type` string for each
// wire shape (Anthropic /v1/messages vs OpenAI /v1/chat/completions), and the
// user-facing message. All surfaces derive their responses from this so the same
// underlying failure looks consistent everywhere.
internal readonly record struct FoundryErrorInfo(int Status, string AnthropicType, string OpenAiType, string Message);

internal static class FoundryErrors
{
    public static FoundryErrorInfo Classify(Exception ex)
    {
        // Credential-layer failures have no HTTP status of their own.
        if (ex is CredentialUnavailableException or AuthenticationFailedException)
        {
            return new(401, "authentication_error", "authentication_error", Describe(ex));
        }

        return HttpStatus(ex) switch
        {
            401 => new(401, "authentication_error", "authentication_error", Describe(ex)),
            403 => new(403, "permission_error", "permission_error", Describe(ex)),
            404 => new(404, "not_found_error", "invalid_request_error",
                "The Foundry resource or deployment was not found (HTTP 404) — check Foundry__Endpoint and Foundry__Deployment." + Detail(ex)),
            400 => new(400, "invalid_request_error", "invalid_request_error",
                "Azure rejected the request as invalid (HTTP 400)." + Detail(ex)),
            413 => new(413, "request_too_large", "invalid_request_error",
                "The request was too large for the deployment (HTTP 413)." + Detail(ex)),
            429 => new(429, "rate_limit_error", "rate_limit_error",
                "The deployment is rate-limiting requests (HTTP 429) — retry with backoff." + Detail(ex)),
            >= 500 and var upstream => new(500, "api_error", "server_error",
                $"Azure returned an upstream error (HTTP {upstream}). This is usually transient — retry."),
            _ => new(500, "api_error", "server_error", Describe(ex)),
        };
    }

    // Upstream 4xx messages are the service's own API error text (never a stack
    // trace), and they usually name the actual problem — worth surfacing, trimmed.
    private static string Detail(Exception ex)
        => string.IsNullOrWhiteSpace(ex.Message)
            ? string.Empty
            : $" Upstream detail: {(ex.Message.Length > 300 ? ex.Message[..300] + "…" : ex.Message)}";

    // Real-Foundry testing showed "your session may have expired" firing for two
    // failures that are NOT expired sessions: az CLI startup exceeding the credential's
    // ProcessTimeout, and a missing data-plane RBAC role (valid token, 401/403 from the
    // service). Each gets its own actionable message.
    public static string Describe(Exception ex) => ex switch
    {
        CredentialUnavailableException =>
            "Azure authentication is unavailable. Install the Azure CLI and run 'az login', then retry.",
        AuthenticationFailedException when IsCliTimeout(ex) =>
            "Timed out waiting for the Azure CLI ('az') to produce a token — usually slow 'az' startup, " +
            "not expired credentials ('az account get-access-token' from a shell will confirm). Retry, or raise " +
            "the timeout via the Foundry__CliTimeoutSeconds environment variable (default 60).",
        AuthenticationFailedException =>
            "Azure authentication failed — your 'az login' session may have expired or target the wrong tenant. " +
            "Run 'az login' (optionally with --tenant) and retry.",
        _ when HttpStatus(ex) is 401 or 403 =>
            "Azure rejected the request as unauthorized. Your 'az login' token is likely fine, but the account " +
            "lacks a data-plane role on the resource: grant 'Cognitive Services OpenAI User' on the Azure " +
            "OpenAI/Foundry resource (subscription Owner alone is not enough), then allow a minute for propagation.",
        _ => "The Foundry model request failed. Check the server logs for details.",
    };

    public static bool IsAuthFailure(Exception ex) =>
        ex is CredentialUnavailableException or AuthenticationFailedException
        || HttpStatus(ex) is 401 or 403;

    private static bool IsCliTimeout(Exception ex) =>
        ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        || ex.InnerException is OperationCanceledException;

    private static int? HttpStatus(Exception ex) => ex switch
    {
        ClientResultException clientResult => clientResult.Status,
        RequestFailedException requestFailed => requestFailed.Status,
        _ => null,
    };
}
