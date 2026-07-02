using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using OpenAI.Chat;

var builder = WebApplication.CreateBuilder(args);

var endpointValue = builder.Configuration["Foundry:Endpoint"];
var deployment = builder.Configuration["Foundry:Deployment"];

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
var chatClient = azureClient.GetChatClient(deployment);

AIAgent agent = chatClient.AsAIAgent(
    instructions: "You are a local proxy agent. Preserve the caller's intent.");

builder.Services.AddSingleton(agent);

var app = builder.Build();

app.MapGet("/", () => "AFClaude is running.");

app.Run();
