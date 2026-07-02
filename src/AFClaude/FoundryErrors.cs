using Azure.Identity;

namespace AFClaude;

internal static class FoundryErrors
{
    public static string Describe(Exception ex) => ex switch
    {
        CredentialUnavailableException => "Azure authentication is unavailable. Install the Azure CLI and run 'az login', then retry.",
        AuthenticationFailedException => "Azure authentication failed — your 'az login' session may have expired. Run 'az login' again and retry.",
        _ => "The Foundry model request failed. Check the server logs for details.",
    };

    public static bool IsAuthFailure(Exception ex) => ex is CredentialUnavailableException or AuthenticationFailedException;
}
