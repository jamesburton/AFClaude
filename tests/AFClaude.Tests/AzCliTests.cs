using System.Text.Json;
using AFClaude;

namespace AFClaude.Tests;

public class AzCliTests
{
    [Fact]
    public void Subscriptions_DeserializeFromRealAzShape()
    {
        const string json = """
        [
          {
            "cloudName": "AzureCloud",
            "id": "11111111-1111-1111-1111-111111111111",
            "isDefault": true,
            "name": "fnz-qhub",
            "state": "Enabled",
            "tenantId": "22222222-2222-2222-2222-222222222222"
          }
        ]
        """;

        var subscriptions = JsonSerializer.Deserialize<List<AzSubscription>>(json, JsonSerializerOptions.Web)!;

        Assert.Single(subscriptions);
        Assert.Equal("11111111-1111-1111-1111-111111111111", subscriptions[0].Id);
        Assert.Equal("fnz-qhub", subscriptions[0].Name);
    }

    [Fact]
    public void CognitiveServicesAccounts_DeserializeFromRealAzShape()
    {
        const string json = """
        [
          {
            "kind": "AIServices",
            "location": "swedencentral",
            "name": "qhub-infra-resource",
            "properties": { "endpoint": "https://qhub-infra-resource.cognitiveservices.azure.com/" },
            "resourceGroup": "rg-qhub-infra"
          }
        ]
        """;

        var accounts = JsonSerializer.Deserialize<List<AzCognitiveServicesAccount>>(json, JsonSerializerOptions.Web)!;

        Assert.Single(accounts);
        Assert.Equal("qhub-infra-resource", accounts[0].Name);
        Assert.Equal("AIServices", accounts[0].Kind);
        Assert.Equal("swedencentral", accounts[0].Location);
        Assert.Equal("rg-qhub-infra", accounts[0].ResourceGroup);
        Assert.Equal("https://qhub-infra-resource.cognitiveservices.azure.com/", accounts[0].Endpoint);
    }

    [Fact]
    public void Deployments_DeserializeFromRealAzShape()
    {
        const string json = """
        [
          {
            "name": "gpt-4.1",
            "properties": {
              "model": { "format": "OpenAI", "name": "gpt-4.1", "version": "2025-04-14" }
            }
          }
        ]
        """;

        var deployments = JsonSerializer.Deserialize<List<AzDeployment>>(json, JsonSerializerOptions.Web)!;

        Assert.Single(deployments);
        Assert.Equal("gpt-4.1", deployments[0].Name);
        Assert.Equal("gpt-4.1", deployments[0].ModelName);
        Assert.Equal("2025-04-14", deployments[0].ModelVersion);
    }

    [Fact]
    public void ClassifyFailure_NotLoggedIn_PointsAtAzLogin()
    {
        var ex = AzCli.ClassifyFailure("account list", "ERROR: Please run 'az login' to setup account.");

        Assert.Contains("az login", ex.Message);
    }

    [Fact]
    public void ClassifyFailure_OtherError_IncludesArgumentsAndStderr()
    {
        var ex = AzCli.ClassifyFailure("cognitiveservices account list", "ERROR: something else broke");

        Assert.Contains("cognitiveservices account list", ex.Message);
        Assert.Contains("something else broke", ex.Message);
    }
}
