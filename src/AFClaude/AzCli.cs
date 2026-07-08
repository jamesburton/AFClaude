using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace AFClaude;

internal sealed record AzSubscription(string Id, string Name);

internal sealed record AzCognitiveServicesAccountProperties(string Endpoint);

internal sealed record AzCognitiveServicesAccount(
    string Name, string Kind, string Location, string ResourceGroup, AzCognitiveServicesAccountProperties Properties)
{
    public string Endpoint => Properties.Endpoint;
}

internal sealed record AzDeploymentModel(string Name, string Version);

internal sealed record AzDeploymentProperties(AzDeploymentModel Model);

internal sealed record AzDeployment(string Name, AzDeploymentProperties Properties)
{
    public string ModelName => Properties.Model.Name;
    public string ModelVersion => Properties.Model.Version;
}

internal sealed class AzCliException(string message, Exception? inner = null) : Exception(message, inner);

// Thin wrapper for shelling out to `az ... -o json`, used only by the interactive config
// wizard. Classifies the two failure modes that matter here (not installed / not logged
// in / timed out) the same way FoundryErrors classifies the SDK's own credential
// exceptions, so both surfaces read consistently.
internal static class AzCli
{
    public static Task<List<AzSubscription>> ListSubscriptionsAsync(int timeoutSeconds, CancellationToken cancellationToken)
        => RunJsonListAsync<AzSubscription>("account list", timeoutSeconds, cancellationToken);

    public static Task<List<AzCognitiveServicesAccount>> ListCognitiveServicesAccountsAsync(
        string subscriptionId, int timeoutSeconds, CancellationToken cancellationToken)
        => RunJsonListAsync<AzCognitiveServicesAccount>(
            $"cognitiveservices account list --subscription {subscriptionId}", timeoutSeconds, cancellationToken);

    public static Task<List<AzDeployment>> ListDeploymentsAsync(
        string resourceGroup, string accountName, int timeoutSeconds, CancellationToken cancellationToken)
        => RunJsonListAsync<AzDeployment>(
            $"cognitiveservices account deployment list -g {resourceGroup} -n {accountName}", timeoutSeconds, cancellationToken);

    private static async Task<List<T>> RunJsonListAsync<T>(string arguments, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var json = await RunAsync(arguments, timeoutSeconds, cancellationToken);
        return JsonSerializer.Deserialize<List<T>>(json, JsonSerializerOptions.Web) ?? [];
    }

    internal static async Task<string> RunAsync(string arguments, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var psi = BuildStartInfo(arguments);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new AzCliException("Failed to start 'az'.");
        }
        catch (Win32Exception ex)
        {
            throw new AzCliException("Azure CLI ('az') was not found on PATH. Install it and run 'az login'.", ex);
        }

        using (process)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            string stdout;
            string stderr;
            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
                await process.WaitForExitAsync(linked.Token);
                stdout = await stdoutTask;
                stderr = await stderrTask;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                TryKill(process);
                throw new AzCliException($"Timed out waiting for 'az {arguments}' after {timeoutSeconds}s.");
            }

            if (process.ExitCode != 0)
            {
                throw ClassifyFailure(arguments, stderr);
            }

            return stdout;
        }
    }

    // Exposed internally (not private) so the classification rule is unit-testable
    // without spawning a real 'az' process.
    internal static AzCliException ClassifyFailure(string arguments, string stderr)
        => stderr.Contains("az login", StringComparison.OrdinalIgnoreCase)
            ? new AzCliException("Azure CLI is not logged in. Run 'az login' and retry.")
            : new AzCliException($"'az {arguments}' failed: {stderr.Trim()}");

    // cmd.exe /c is required on Windows to resolve az.cmd (a batch-file shim) via
    // Process.Start without UseShellExecute; az itself isn't directly executable.
    // `arguments` is interpolated into this single command string with no escaping:
    // safe here because callers only ever build it from subscription IDs (GUIDs) and
    // resource-group/account names (which Azure's own naming rules forbid shell
    // metacharacters in), sourced from the operator's own authenticated `az` output --
    // not from untrusted input.
    private static ProcessStartInfo BuildStartInfo(string arguments)
        => OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", $"az {arguments} -o json" } }
            : new ProcessStartInfo("az") { Arguments = $"{arguments} -o json" };

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort; the process may have already exited.
        }
    }
}
