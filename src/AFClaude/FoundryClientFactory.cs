using Azure.AI.OpenAI;
using Azure.Identity;
using OpenAI.Chat;

namespace AFClaude;

internal static class FoundryClientFactory
{
    public static (ChatClient ChatClient, string Deployment) Create(IConfiguration configuration)
    {
        var endpointValue = configuration["Foundry:Endpoint"];
        var deployment = configuration["Foundry:Deployment"];

        if (string.IsNullOrWhiteSpace(endpointValue) || !Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException(
                "Missing or invalid configuration 'Foundry:Endpoint'. Set the Foundry__Endpoint environment variable " +
                "to the Azure OpenAI/Foundry resource endpoint, e.g. https://<resource>.openai.azure.com/");
        }

        if (string.IsNullOrWhiteSpace(deployment))
        {
            throw new InvalidOperationException(
                "Missing configuration 'Foundry:Deployment'. Set the Foundry__Deployment environment variable " +
                "to the target deployment name.");
        }

        var azureClient = new AzureOpenAIClient(endpoint, new AzureCliCredential());
        return (azureClient.GetChatClient(deployment), deployment);
    }
}
