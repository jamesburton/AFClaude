using System.ClientModel;
using Azure;
using Azure.Identity;

namespace AFClaude;

internal static class FoundryErrors
{
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
