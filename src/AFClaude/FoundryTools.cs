using System.ComponentModel;
using Microsoft.Agents.AI;
using ModelContextProtocol.Server;

namespace AFClaude;

[McpServerToolType]
internal sealed class FoundryTools(AIAgent agent, ILogger<FoundryTools> logger)
{
    [McpServerTool(Name = "ask_foundry")]
    [Description(
        "Ask the configured Azure AI Foundry model a question and return its response. " +
        "Use this to delegate a prompt to the Foundry-hosted model.")]
    public async Task<string> AskFoundryAsync(
        [Description("The prompt or question to send to the Foundry model.")]
        string prompt,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken);
            return response.Text;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ask_foundry failed");
            return FoundryErrors.Describe(ex);
        }
    }
}
