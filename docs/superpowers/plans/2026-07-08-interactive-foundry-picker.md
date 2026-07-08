# Interactive Azure Foundry Picker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an interactive `az`-driven subscription → resource → deployment picker (Spectre.Console TUI) for `launch` and `--http` modes, so a user without `Foundry__Endpoint`/`Foundry__Deployment` set can select them instead of hitting today's fail-fast error, with an option to persist the choice to a local config file.

**Architecture:** Three new focused files — `AzCli.cs` (shell out to `az ... -o json`, typed results, error classification), `FoundryConfigFile.cs` (JSON config model + load/save + precedence-friendly `TryLoad`), `FoundryConfigWizard.cs` (Spectre.Console selection flow, built on `AzCli` + `FoundryConfigFile`) — plus a small `CliArgs.cs` helper for AFClaude's own new flags. `Program.cs` gains a `ResolveFoundryConfigOverridesAsync` local function that runs before `FoundryClientFactory.Create`, feeding resolved values in as configuration overrides that never outrank an already-set env var.

**Tech Stack:** .NET 10, `Spectre.Console` (new dependency) for the TUI, `Spectre.Console.Testing` (test-only) for driving prompts in tests without a real terminal, xUnit (existing).

## Global Constraints

- Target framework: `net10.0`, `Nullable` and `ImplicitUsings` enabled (matches both existing `.csproj` files).
- The interactive wizard must never run in MCP stdio mode (no TTY — stdout is reserved for JSON-RPC) or when stdin/stdout are redirected in launch/http mode.
- An explicitly-named `--config <file>` that doesn't exist is always a hard failure: `Missing Config <file>` — never silently falls back to the wizard.
- Env vars (`Foundry__Endpoint`/`Foundry__Deployment`/`Foundry__Api`) always outrank a saved config file or wizard result for that specific key.
- `Foundry__Api=auto` is never written to a saved config file — only the concrete probed value (`anthropic`/`openai`).
- All new code follows the existing repo style: internal, sealed where applicable, top-level-statement `Program.cs` keeps using local static functions rather than extracting a class for wiring logic.
- `InternalsVisibleTo("AFClaude.Tests")` already covers `src/AFClaude` — no project file change needed for test visibility.

---

### Task 1: Add Spectre.Console dependencies

**Files:**
- Modify: `src/AFClaude/AFClaude.csproj`
- Modify: `tests/AFClaude.Tests/AFClaude.Tests.csproj`

**Interfaces:**
- Produces: `Spectre.Console` types (`IAnsiConsole`, `AnsiConsole`, `SelectionPrompt<T>`, `TextPrompt<T>`) available to `src/AFClaude`; `Spectre.Console.Testing.TestConsole` available to `tests/AFClaude.Tests`.

- [ ] **Step 1: Add the `Spectre.Console` package reference to the main project**

Edit `src/AFClaude/AFClaude.csproj`, inside the existing `<ItemGroup>` that lists `Azure.AI.OpenAI`, etc. (currently lines 31-37), add:

```xml
    <PackageReference Include="Spectre.Console" Version="0.49.1" />
```

- [ ] **Step 2: Add the `Spectre.Console.Testing` package reference to the test project**

Edit `tests/AFClaude.Tests/AFClaude.Tests.csproj`, inside the existing `<ItemGroup>` that lists `coverlet.collector`, `Microsoft.NET.Test.Sdk`, etc. (currently lines 12-17), add:

```xml
    <PackageReference Include="Spectre.Console.Testing" Version="0.49.1" />
```

- [ ] **Step 3: Restore and confirm both packages resolve**

Run: `dotnet restore AFClaude.slnx`
Expected: restore succeeds with no version-conflict errors. If `0.49.1` doesn't exist for either package by the time this runs, use `dotnet add <project> package Spectre.Console` / `Spectre.Console.Testing` (no version pin) to pick up whatever the current stable release is, then update both `<PackageReference>` lines to match the version it wrote — keep both packages on the same version.

- [ ] **Step 4: Build to confirm nothing broke**

Run: `dotnet build AFClaude.slnx`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/AFClaude/AFClaude.csproj tests/AFClaude.Tests/AFClaude.Tests.csproj
git commit -m "Add Spectre.Console dependency for the interactive Foundry picker"
```

---

### Task 2: `AzCli.cs` — typed `az` JSON shelling with error classification

**Files:**
- Create: `src/AFClaude/AzCli.cs`
- Test: `tests/AFClaude.Tests/AzCliTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed record AzSubscription(string Id, string Name)`
  - `internal sealed record AzCognitiveServicesAccountProperties(string Endpoint)`
  - `internal sealed record AzCognitiveServicesAccount(string Name, string Kind, string Location, string ResourceGroup, AzCognitiveServicesAccountProperties Properties)` with `.Endpoint` convenience property
  - `internal sealed record AzDeploymentModel(string Name, string Version)`
  - `internal sealed record AzDeploymentProperties(AzDeploymentModel Model)`
  - `internal sealed record AzDeployment(string Name, AzDeploymentProperties Properties)` with `.ModelName`/`.ModelVersion` convenience properties
  - `internal sealed class AzCliException(string message, Exception? inner = null) : Exception(message, inner)`
  - `internal static class AzCli` with:
    - `Task<List<AzSubscription>> ListSubscriptionsAsync(int timeoutSeconds, CancellationToken cancellationToken)`
    - `Task<List<AzCognitiveServicesAccount>> ListCognitiveServicesAccountsAsync(string subscriptionId, int timeoutSeconds, CancellationToken cancellationToken)`
    - `Task<List<AzDeployment>> ListDeploymentsAsync(string resourceGroup, string accountName, int timeoutSeconds, CancellationToken cancellationToken)`
    - `internal static AzCliException ClassifyFailure(string arguments, string stderr)` (exposed internally so it's unit-testable without spawning a real process)

- [ ] **Step 1: Write the failing tests for JSON deserialization and failure classification**

Create `tests/AFClaude.Tests/AzCliTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/AFClaude.Tests --filter AzCliTests`
Expected: FAIL — compile error, `AzCli`/`AzSubscription`/etc. do not exist yet.

- [ ] **Step 3: Implement `AzCli.cs`**

Create `src/AFClaude/AzCli.cs`:

```csharp
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

    // Exposed internally (not private) so the classification rule is unit-testable
    // without spawning a real 'az' process.
    internal static AzCliException ClassifyFailure(string arguments, string stderr)
        => stderr.Contains("az login", StringComparison.OrdinalIgnoreCase)
            ? new AzCliException("Azure CLI is not logged in. Run 'az login' and retry.")
            : new AzCliException($"'az {arguments}' failed: {stderr.Trim()}");

    // cmd.exe /c is required on Windows to resolve az.cmd (a batch-file shim) via
    // Process.Start without UseShellExecute; az itself isn't directly executable.
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/AFClaude.Tests --filter AzCliTests`
Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add src/AFClaude/AzCli.cs tests/AFClaude.Tests/AzCliTests.cs
git commit -m "Add AzCli: typed az CLI JSON shelling with error classification"
```

---

### Task 3: `FoundryConfigFile.cs` — config model, load/save, precedence rule

**Files:**
- Create: `src/AFClaude/FoundryConfigFile.cs`
- Test: `tests/AFClaude.Tests/FoundryConfigFileTests.cs`

**Interfaces:**
- Consumes: nothing from other new files.
- Produces:
  - `internal sealed record FoundryConfig(string Endpoint, string Deployment, string Api)`
  - `internal static class FoundryConfigFile` with:
    - `public const string DefaultFileName = "afclaude.config.json"`
    - `public static FoundryConfig? TryLoad(string? explicitPath)` — returns `null` only when `explicitPath` is `null` and the default file doesn't exist; throws `InvalidOperationException("Missing Config <explicitPath>")` when `explicitPath` is non-null and that file doesn't exist; throws `InvalidOperationException` on unparseable/empty JSON.
    - `public static void Save(string path, FoundryConfig config)`

- [ ] **Step 1: Write the failing tests**

Create `tests/AFClaude.Tests/FoundryConfigFileTests.cs`:

```csharp
using AFClaude;

namespace AFClaude.Tests;

public class FoundryConfigFileTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("afclaude-config-tests-").FullName;
    private readonly string _originalCwd = Directory.GetCurrentDirectory();

    public FoundryConfigFileTests() => Directory.SetCurrentDirectory(_tempDir);

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCwd);
        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void TryLoad_DefaultFileMissing_ReturnsNull()
    {
        Assert.Null(FoundryConfigFile.TryLoad(explicitPath: null));
    }

    [Fact]
    public void TryLoad_ExplicitFileMissing_ThrowsMissingConfig()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FoundryConfigFile.TryLoad("does-not-exist.json"));

        Assert.Equal("Missing Config does-not-exist.json", ex.Message);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var config = new FoundryConfig(
            "https://my-resource.cognitiveservices.azure.com/", "gpt-4.1", "openai");

        FoundryConfigFile.Save(FoundryConfigFile.DefaultFileName, config);
        var loaded = FoundryConfigFile.TryLoad(explicitPath: null);

        Assert.Equal(config, loaded);
    }

    [Fact]
    public void TryLoad_EmptyFile_ThrowsInvalidOperationException()
    {
        File.WriteAllText(FoundryConfigFile.DefaultFileName, "");

        Assert.Throws<InvalidOperationException>(() => FoundryConfigFile.TryLoad(explicitPath: null));
    }

    [Fact]
    public void TryLoad_ExplicitPath_LoadsThatFileNotTheDefault()
    {
        var config = new FoundryConfig("https://explicit.example.com/", "explicit-deployment", "anthropic");
        FoundryConfigFile.Save("custom.json", config);

        var loaded = FoundryConfigFile.TryLoad("custom.json");

        Assert.Equal(config, loaded);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/AFClaude.Tests --filter FoundryConfigFileTests`
Expected: FAIL — compile error, `FoundryConfigFile`/`FoundryConfig` do not exist yet.

- [ ] **Step 3: Implement `FoundryConfigFile.cs`**

Create `src/AFClaude/FoundryConfigFile.cs`:

```csharp
using System.Text.Json;

namespace AFClaude;

// The three Foundry settings the interactive picker resolves and can persist. Api is
// always a concrete value here ("anthropic"/"openai") — "auto" is never saved, since a
// saved config exists specifically to skip the probe next time.
internal sealed record FoundryConfig(string Endpoint, string Deployment, string Api);

internal static class FoundryConfigFile
{
    public const string DefaultFileName = "afclaude.config.json";

    // Returns null only when using the DEFAULT filename and it doesn't exist yet -- a
    // normal "nothing saved" state, not an error. An explicitly-named file that's
    // missing is always a hard failure: an explicit path means explicit intent.
    public static FoundryConfig? TryLoad(string? explicitPath)
    {
        var path = explicitPath ?? DefaultFileName;
        if (!File.Exists(path))
        {
            if (explicitPath is not null)
            {
                throw new InvalidOperationException($"Missing Config {explicitPath}");
            }
            return null;
        }

        var json = File.ReadAllText(path);
        try
        {
            return JsonSerializer.Deserialize<FoundryConfig>(json, JsonSerializerOptions.Web)
                ?? throw new InvalidOperationException($"Config file '{path}' is empty or invalid JSON.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Config file '{path}' is not valid JSON.", ex);
        }
    }

    public static void Save(string path, FoundryConfig config)
    {
        var options = new JsonSerializerOptions(JsonSerializerOptions.Web) { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(config, options));
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/AFClaude.Tests --filter FoundryConfigFileTests`
Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add src/AFClaude/FoundryConfigFile.cs tests/AFClaude.Tests/FoundryConfigFileTests.cs
git commit -m "Add FoundryConfigFile: saved-config model, load/save, missing-config rule"
```

---

### Task 4: `CliArgs.cs` — AFClaude's own new flags

**Files:**
- Create: `src/AFClaude/CliArgs.cs`
- Test: `tests/AFClaude.Tests/CliArgsTests.cs`

**Interfaces:**
- Produces: `internal static class CliArgs` with:
  - `public static bool HasSelectFlag(string[] args)`
  - `public static string? GetConfigPath(string[] args)`
  - `public static string[] StripAfClaudeFlags(string[] args)`

- [ ] **Step 1: Write the failing tests**

Create `tests/AFClaude.Tests/CliArgsTests.cs`:

```csharp
using AFClaude;

namespace AFClaude.Tests;

public class CliArgsTests
{
    [Theory]
    [InlineData("--select")]
    [InlineData("--configure")]
    [InlineData("--SELECT")]
    public void HasSelectFlag_DetectsEitherSpellingCaseInsensitive(string flag)
    {
        Assert.True(CliArgs.HasSelectFlag(["launch", flag]));
    }

    [Fact]
    public void HasSelectFlag_AbsentReturnsFalse()
    {
        Assert.False(CliArgs.HasSelectFlag(["launch", "--continue"]));
    }

    [Fact]
    public void GetConfigPath_ReturnsValueFollowingFlag()
    {
        Assert.Equal("myfile.json", CliArgs.GetConfigPath(["--config", "myfile.json", "--continue"]));
    }

    [Fact]
    public void GetConfigPath_AbsentReturnsNull()
    {
        Assert.Null(CliArgs.GetConfigPath(["--continue"]));
    }

    [Fact]
    public void GetConfigPath_FlagWithNoValueReturnsNull()
    {
        Assert.Null(CliArgs.GetConfigPath(["--config"]));
    }

    [Fact]
    public void StripAfClaudeFlags_RemovesSelectConfigureAndConfigWithValue()
    {
        string[] args = ["--select", "--config", "myfile.json", "--continue", "-p", "do the thing"];

        Assert.Equal(
            ["--continue", "-p", "do the thing"],
            CliArgs.StripAfClaudeFlags(args));
    }

    [Fact]
    public void StripAfClaudeFlags_NoAfClaudeFlags_LeavesArgsUntouched()
    {
        string[] args = ["--continue", "-p", "mentions --select in text"];

        Assert.Equal(args, CliArgs.StripAfClaudeFlags(args));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/AFClaude.Tests --filter CliArgsTests`
Expected: FAIL — compile error, `CliArgs` does not exist yet.

- [ ] **Step 3: Implement `CliArgs.cs`**

Create `src/AFClaude/CliArgs.cs`:

```csharp
namespace AFClaude;

// Parsing/removal helpers for AFClaude's own new CLI flags (--select, --configure,
// --config <file>), distinct from LaunchArgs.cs which translates flags meant for the
// `claude` child process itself.
internal static class CliArgs
{
    public static bool HasSelectFlag(string[] args)
        => args.Contains("--select", StringComparer.OrdinalIgnoreCase)
        || args.Contains("--configure", StringComparer.OrdinalIgnoreCase);

    public static string? GetConfigPath(string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, "--config", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    // Removes AFClaude's own flags before the remaining args are forwarded to another
    // process (the real `claude` binary in launch mode) that doesn't know about them.
    public static string[] StripAfClaudeFlags(string[] args)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--select", StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[i], "--configure", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase))
            {
                i++; // also skip its value
                continue;
            }
            result.Add(args[i]);
        }
        return [.. result];
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/AFClaude.Tests --filter CliArgsTests`
Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/AFClaude/CliArgs.cs tests/AFClaude.Tests/CliArgsTests.cs
git commit -m "Add CliArgs: parsing for --select/--configure/--config flags"
```

---

### Task 5: `FoundryConfigWizard.cs` — the interactive Spectre.Console flow

**Files:**
- Create: `src/AFClaude/FoundryConfigWizard.cs`
- Test: `tests/AFClaude.Tests/FoundryConfigWizardTests.cs`

**Interfaces:**
- Consumes:
  - `AzCli.ListSubscriptionsAsync`/`ListCognitiveServicesAccountsAsync`/`ListDeploymentsAsync` (Task 2)
  - `AzSubscription`, `AzCognitiveServicesAccount`, `AzDeployment` (Task 2)
  - `FoundryConfig`, `FoundryConfigFile.Save` (Task 3)
  - `FoundryClientFactory.Create(IConfiguration)`, `FoundryApiResolver.ResolveAsync`, `FoundryApi` enum (existing `FoundryClientFactory.cs`/`FoundryAnthropic.cs`)
- Produces: `internal static class FoundryConfigWizard` with:
  - `public static Task<FoundryConfig> RunAsync(int azTimeoutSeconds, string suggestedSaveFileName, CancellationToken cancellationToken)` — the entry point Program.cs calls (uses the real ambient `AnsiConsole.Console`)
  - `internal static AzSubscription PickSubscription(IAnsiConsole console, IReadOnlyList<AzSubscription> subscriptions)` — pure selection logic, no `az` call, unit-testable
  - `internal static AzCognitiveServicesAccount PickResource(IAnsiConsole console, IReadOnlyList<AzCognitiveServicesAccount> resources)`
  - `internal static AzDeployment PickDeployment(IAnsiConsole console, IReadOnlyList<AzDeployment> deployments)`
  - `internal static void OfferSave(IAnsiConsole console, FoundryConfig config, string suggestedFileName)`

- [ ] **Step 1: Write the failing tests for the pure selection/save logic**

Create `tests/AFClaude.Tests/FoundryConfigWizardTests.cs`:

```csharp
using AFClaude;
using Spectre.Console;
using Spectre.Console.Testing;

namespace AFClaude.Tests;

public class FoundryConfigWizardTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("afclaude-wizard-tests-").FullName;
    private readonly string _originalCwd = Directory.GetCurrentDirectory();

    public FoundryConfigWizardTests() => Directory.SetCurrentDirectory(_tempDir);

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCwd);
        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void PickSubscription_SingleSubscription_ReturnsItWithoutPrompting()
    {
        var console = new TestConsole();
        var subscriptions = new List<AzSubscription> { new("sub-1", "Only Subscription") };

        var picked = FoundryConfigWizard.PickSubscription(console, subscriptions);

        Assert.Equal("Only Subscription", picked.Name);
    }

    [Fact]
    public void PickSubscription_NoneFound_Throws()
    {
        var console = new TestConsole();

        Assert.Throws<InvalidOperationException>(
            () => FoundryConfigWizard.PickSubscription(console, []));
    }

    [Fact]
    public void PickSubscription_MultipleSubscriptions_PromptsAndReturnsSelection()
    {
        var console = new TestConsole();
        console.Interactive();
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        var subscriptions = new List<AzSubscription> { new("sub-1", "First"), new("sub-2", "Second") };

        var picked = FoundryConfigWizard.PickSubscription(console, subscriptions);

        Assert.Equal("Second", picked.Name);
    }

    [Fact]
    public void PickResource_NoneFound_Throws()
    {
        var console = new TestConsole();

        Assert.Throws<InvalidOperationException>(
            () => FoundryConfigWizard.PickResource(console, []));
    }

    [Fact]
    public void PickResource_SingleResource_ReturnsItWithoutPrompting()
    {
        var console = new TestConsole();
        var resources = new List<AzCognitiveServicesAccount>
        {
            new("qhub-infra-resource", "AIServices", "swedencentral", "rg-qhub-infra",
                new AzCognitiveServicesAccountProperties("https://qhub-infra-resource.cognitiveservices.azure.com/")),
        };

        var picked = FoundryConfigWizard.PickResource(console, resources);

        Assert.Equal("qhub-infra-resource", picked.Name);
    }

    [Fact]
    public void PickDeployment_NoneFound_Throws()
    {
        var console = new TestConsole();

        Assert.Throws<InvalidOperationException>(
            () => FoundryConfigWizard.PickDeployment(console, []));
    }

    [Fact]
    public void PickDeployment_SingleDeployment_ReturnsItWithoutPrompting()
    {
        var console = new TestConsole();
        var deployments = new List<AzDeployment>
        {
            new("gpt-4.1", new AzDeploymentProperties(new AzDeploymentModel("gpt-4.1", "2025-04-14"))),
        };

        var picked = FoundryConfigWizard.PickDeployment(console, deployments);

        Assert.Equal("gpt-4.1", picked.Name);
    }

    [Fact]
    public void OfferSave_DontSave_WritesNoFile()
    {
        var console = new TestConsole();
        console.Interactive();
        console.Input.PushKey(ConsoleKey.Enter); // first choice: "Don't save"
        var config = new FoundryConfig("https://example.com/", "gpt-4.1", "openai");

        FoundryConfigWizard.OfferSave(console, config, "afclaude.config.json");

        Assert.False(File.Exists("afclaude.config.json"));
    }

    [Fact]
    public void OfferSave_SaveToFile_WritesConfigWithChosenFileName()
    {
        var console = new TestConsole();
        console.Interactive();
        console.Input.PushKey(ConsoleKey.DownArrow); // move to "Save to file"
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("custom.json"); // accept/override the suggested filename
        var config = new FoundryConfig("https://example.com/", "gpt-4.1", "openai");

        FoundryConfigWizard.OfferSave(console, config, "afclaude.config.json");

        Assert.True(File.Exists("custom.json"));
        Assert.Equal(config, FoundryConfigFile.TryLoad("custom.json"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/AFClaude.Tests --filter FoundryConfigWizardTests`
Expected: FAIL — compile error, `FoundryConfigWizard` does not exist yet.

- [ ] **Step 3: Implement `FoundryConfigWizard.cs`**

Create `src/AFClaude/FoundryConfigWizard.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace AFClaude;

// Interactive subscription -> resource -> deployment picker for launch/--http modes.
// Only invoked (see Program.cs's ResolveFoundryConfigOverridesAsync) when
// Foundry__Endpoint/Foundry__Deployment aren't already resolvable some other way and a
// real terminal is attached. Selection/save logic is split out as testable pure
// functions taking an IAnsiConsole, so tests drive them with Spectre.Console.Testing's
// TestConsole instead of a real terminal; only the az-calling steps need the real thing.
internal static class FoundryConfigWizard
{
    public static async Task<FoundryConfig> RunAsync(
        int azTimeoutSeconds, string suggestedSaveFileName, CancellationToken cancellationToken)
    {
        var console = AnsiConsole.Console;

        var subscriptions = await AzCli.ListSubscriptionsAsync(azTimeoutSeconds, cancellationToken);
        var subscription = PickSubscription(console, subscriptions);

        var resources = await AzCli.ListCognitiveServicesAccountsAsync(subscription.Id, azTimeoutSeconds, cancellationToken);
        var resource = PickResource(console, resources);

        var deployments = await AzCli.ListDeploymentsAsync(resource.ResourceGroup, resource.Name, azTimeoutSeconds, cancellationToken);
        var deployment = PickDeployment(console, deployments);

        console.MarkupLine("Probing which API surface this deployment answers on...");
        var api = await ProbeApiAsync(resource.Endpoint, deployment.Name, cancellationToken);
        var config = new FoundryConfig(resource.Endpoint, deployment.Name, api);
        console.MarkupLine(api == "anthropic"
            ? "[green]Detected native Anthropic (Claude) deployment.[/]"
            : "[green]Detected OpenAI-compatible deployment.[/]");

        OfferSave(console, config, suggestedSaveFileName);
        return config;
    }

    internal static AzSubscription PickSubscription(IAnsiConsole console, IReadOnlyList<AzSubscription> subscriptions)
    {
        if (subscriptions.Count == 0)
        {
            throw new InvalidOperationException("No Azure subscriptions found for the logged-in 'az' account.");
        }
        if (subscriptions.Count == 1)
        {
            return subscriptions[0];
        }
        return console.Prompt(
            new SelectionPrompt<AzSubscription>()
                .Title("Select an Azure subscription:")
                .UseConverter(s => s.Name)
                .AddChoices(subscriptions));
    }

    internal static AzCognitiveServicesAccount PickResource(IAnsiConsole console, IReadOnlyList<AzCognitiveServicesAccount> resources)
    {
        if (resources.Count == 0)
        {
            throw new InvalidOperationException("No AIServices/OpenAI Cognitive Services resources found in that subscription.");
        }
        if (resources.Count == 1)
        {
            return resources[0];
        }
        return console.Prompt(
            new SelectionPrompt<AzCognitiveServicesAccount>()
                .Title("Select a Foundry/OpenAI resource:")
                .UseConverter(a => $"{a.Name} ({a.Location}, {a.ResourceGroup})")
                .AddChoices(resources));
    }

    internal static AzDeployment PickDeployment(IAnsiConsole console, IReadOnlyList<AzDeployment> deployments)
    {
        if (deployments.Count == 0)
        {
            throw new InvalidOperationException("No model deployments found on that resource.");
        }
        if (deployments.Count == 1)
        {
            return deployments[0];
        }
        return console.Prompt(
            new SelectionPrompt<AzDeployment>()
                .Title("Select a model deployment:")
                .UseConverter(d => $"{d.Name} -> {d.ModelName}/{d.ModelVersion}")
                .AddChoices(deployments));
    }

    internal static void OfferSave(IAnsiConsole console, FoundryConfig config, string suggestedFileName)
    {
        const string DontSave = "Don't save";
        const string SaveToFile = "Save to file";

        var choice = console.Prompt(
            new SelectionPrompt<string>()
                .Title("Save this configuration for future runs?")
                .AddChoices(DontSave, SaveToFile));

        if (choice == DontSave)
        {
            return;
        }

        var fileName = console.Prompt(
            new TextPrompt<string>("Save as:").DefaultValue(suggestedFileName));
        FoundryConfigFile.Save(fileName, config);
        console.MarkupLine($"[green]Saved to {fileName}.[/]");
    }

    // Reuses FoundryClientFactory.Create + the existing FoundryApiResolver rather than
    // reimplementing the probe: exercises the same code path launch mode's warm-up does.
    private static async Task<string> ProbeApiAsync(string endpoint, string deployment, CancellationToken cancellationToken)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Foundry:Endpoint"] = endpoint,
                ["Foundry:Deployment"] = deployment,
                ["Foundry:Api"] = "auto",
            })
            .Build();
        var foundry = FoundryClientFactory.Create(configuration);
        var api = await foundry.Api.ResolveAsync(cancellationToken);
        return api == FoundryApi.Anthropic ? "anthropic" : "openai";
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/AFClaude.Tests --filter FoundryConfigWizardTests`
Expected: PASS, 9 tests. If `TestConsole`/`SelectionPrompt`/`TextPrompt` member names differ slightly from what's shown here (check the actual installed `Spectre.Console`/`Spectre.Console.Testing` version's API — e.g. `console.Interactive()` or `PushTextWithEnter` signatures), adjust the calls to match; the assertions are what must hold, not the exact API shape guessed here.

- [ ] **Step 5: Commit**

```bash
git add src/AFClaude/FoundryConfigWizard.cs tests/AFClaude.Tests/FoundryConfigWizardTests.cs
git commit -m "Add FoundryConfigWizard: interactive subscription/resource/deployment picker"
```

---

### Task 6: Wire the picker into `Program.cs`

**Files:**
- Modify: `src/AFClaude/Program.cs`

**Interfaces:**
- Consumes: `CliArgs.HasSelectFlag`/`GetConfigPath`/`StripAfClaudeFlags` (Task 4), `FoundryConfigFile.TryLoad`/`DefaultFileName` (Task 3), `FoundryConfigWizard.RunAsync` (Task 5), `FoundryConfig` (Task 3).
- Produces: local function `Task<Dictionary<string, string?>> ResolveFoundryConfigOverridesAsync(IConfiguration configuration, string[] args, bool interactiveAllowed)`, used by `RunMcpAsync`, `RunHttpAsync`, `RunLaunchAsync`.

- [ ] **Step 1: Add the shared resolution function to `Program.cs`**

In `src/AFClaude/Program.cs`, add this local function near the other `static async Task Run...Async` functions (e.g. directly after `RunLaunchAsync`, before `BuildHttpApp`):

```csharp
// Resolves Foundry:Endpoint/Deployment/Api ahead of FoundryClientFactory.Create: env
// vars always win; otherwise an explicit/default saved config file; otherwise (only
// when interactiveAllowed and a real terminal is attached) the interactive picker.
// Returns overrides to layer on top of the caller's configuration -- never touches a
// key the caller's configuration already has a non-empty value for.
static async Task<Dictionary<string, string?>> ResolveFoundryConfigOverridesAsync(
    IConfiguration configuration, string[] args, bool interactiveAllowed)
{
    var overrides = new Dictionary<string, string?>();
    var selectRequested = CliArgs.HasSelectFlag(args);
    var explicitConfigPath = CliArgs.GetConfigPath(args);

    var hasEndpoint = !string.IsNullOrWhiteSpace(configuration["Foundry:Endpoint"]);
    var hasDeployment = !string.IsNullOrWhiteSpace(configuration["Foundry:Deployment"]);
    if (!selectRequested && hasEndpoint && hasDeployment)
    {
        return overrides; // nothing to resolve -- env vars/appsettings already cover it
    }

    // --select always re-runs the wizard, ignoring any existing saved config file
    // (this doubles as the "reset" case without needing a second flag).
    var resolved = selectRequested ? null : FoundryConfigFile.TryLoad(explicitConfigPath);

    if (resolved is null)
    {
        if (!interactiveAllowed || Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return overrides; // let FoundryClientFactory.Create's existing fail-fast fire
        }

        var suggestedFileName = explicitConfigPath ?? FoundryConfigFile.DefaultFileName;
        var timeoutSeconds = configuration.GetValue<int?>("Foundry:CliTimeoutSeconds") ?? 60;
        resolved = await FoundryConfigWizard.RunAsync(timeoutSeconds, suggestedFileName, CancellationToken.None);
    }

    if (!hasEndpoint)
    {
        overrides["Foundry:Endpoint"] = resolved.Endpoint;
    }
    if (!hasDeployment)
    {
        overrides["Foundry:Deployment"] = resolved.Deployment;
    }
    if (string.IsNullOrWhiteSpace(configuration["Foundry:Api"]))
    {
        overrides["Foundry:Api"] = resolved.Api;
    }
    return overrides;
}
```

- [ ] **Step 2: Wire it into `RunMcpAsync`**

In `src/AFClaude/Program.cs`, change `RunMcpAsync` (currently lines 23-42) from:

```csharp
static async Task RunMcpAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    // Stdio transport uses stdout exclusively for JSON-RPC; all logs must go to stderr.
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

    var foundry = FoundryClientFactory.Create(builder.Configuration);
```

to:

```csharp
static async Task RunMcpAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    // Stdio transport uses stdout exclusively for JSON-RPC; all logs must go to stderr.
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

    // interactiveAllowed: false -- MCP mode never has an operator present (Claude
    // launches it silently), so it can only passively load a saved config file, never
    // run the wizard.
    var mcpOverrides = await ResolveFoundryConfigOverridesAsync(builder.Configuration, args, interactiveAllowed: false);
    if (mcpOverrides.Count > 0)
    {
        builder.Configuration.AddInMemoryCollection(mcpOverrides);
    }

    var foundry = FoundryClientFactory.Create(builder.Configuration);
```

The rest of `RunMcpAsync` is unchanged.

- [ ] **Step 3: Wire it into `RunHttpAsync`**

Change `RunHttpAsync` (currently lines 45-49) from:

```csharp
static async Task RunHttpAsync(string[] args)
{
    var app = BuildHttpApp(args);
    await app.RunAsync();
}
```

to:

```csharp
static async Task RunHttpAsync(string[] args)
{
    var envConfig = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    var httpOverrides = await ResolveFoundryConfigOverridesAsync(envConfig, args, interactiveAllowed: true);
    var foundry = FoundryClientFactory.Create(
        new ConfigurationBuilder().AddEnvironmentVariables().AddInMemoryCollection(httpOverrides).Build());

    var app = BuildHttpApp(args, foundry: foundry);
    await app.RunAsync();
}
```

- [ ] **Step 4: Wire it into `RunLaunchAsync`, and strip AFClaude's own flags before forwarding to `claude`**

Change the start of `RunLaunchAsync` (currently lines 54-58) from:

```csharp
static async Task RunLaunchAsync(string[] claudeArgs)
{
    var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    var foundry = FoundryClientFactory.Create(config); // fail fast before starting anything
    var deployment = foundry.Deployment;
```

to:

```csharp
static async Task RunLaunchAsync(string[] claudeArgs)
{
    var envConfig = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    var launchOverrides = await ResolveFoundryConfigOverridesAsync(envConfig, claudeArgs, interactiveAllowed: true);
    var config = new ConfigurationBuilder().AddEnvironmentVariables().AddInMemoryCollection(launchOverrides).Build();
    var foundry = FoundryClientFactory.Create(config); // fail fast before starting anything
    var deployment = foundry.Deployment;
```

Then, further down in the same function, change the forwarding line (currently line 90) from:

```csharp
    foreach (var a in LaunchArgs.Translate(claudeArgs))
```

to:

```csharp
    foreach (var a in LaunchArgs.Translate(CliArgs.StripAfClaudeFlags(claudeArgs)))
```

This keeps `--select`/`--configure`/`--config <file>` from being forwarded to the real `claude` binary, which doesn't understand them.

- [ ] **Step 5: Build**

Run: `dotnet build AFClaude.slnx`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test AFClaude.slnx`
Expected: PASS, all tests (existing 57 + the new ones from Tasks 2-5).

- [ ] **Step 7: Commit**

```bash
git add src/AFClaude/Program.cs
git commit -m "Wire the interactive Foundry picker into launch/--http/MCP config resolution"
```

---

### Task 7: Documentation — README.md and PLAN.md

**Files:**
- Modify: `README.md`
- Modify: `PLAN.md`

- [ ] **Step 1: Document the new flags and behavior in README.md**

In `README.md`, immediately after the existing `## Configuration` section's table (after the line ending `...for now — needs an auth-mode option (the current path always uses AzureCliCredential)` — actually: insert right after the closing of the Configuration table, before the `No API keys are configured...` paragraph at line 77), add a new subsection:

```markdown
### Interactive setup (`launch` / `--http` only)

If `Foundry__Endpoint`/`Foundry__Deployment` aren't set (and no saved config file is
found — see below), `launch` and `--http` mode drop into an interactive picker
(`az account list` → `az cognitiveservices account list` → `az cognitiveservices
account deployment list`) instead of failing fast, as long as a real terminal is
attached (it never triggers under a redirected stdin/stdout, and never in the default
MCP stdio mode — Claude launches that one with no operator present). After picking a
deployment it probes which API surface it answers on (same logic as `Foundry__Api=auto`)
and offers to save the result.

| Flag | Effect |
|---|---|
| `--select` / `--configure` | Force the interactive picker to run now, regardless of env vars or an existing saved config file. |
| `--config <file>` | Load Foundry config from `<file>` instead of the default `afclaude.config.json` in the current directory. Fails fast with `Missing Config <file>` if it doesn't exist. Also used as the suggested save-target filename when the picker runs. |

Saved config files are plain JSON:

```json
{
  "Endpoint": "https://<resource>.cognitiveservices.azure.com/",
  "Deployment": "<deployment-name>",
  "Api": "anthropic"
}
```

Env vars always take priority over a saved config file for any key they set.
```

- [ ] **Step 2: Add a new phase entry to PLAN.md**

In `PLAN.md`, after the `## Phase 12 — Polish (remaining)` section and before `## Explicitly out of scope for now`, add:

```markdown
## Phase 13 — Interactive Azure Foundry picker — DONE

**Origin:** finding the right `Foundry__Endpoint`/`Foundry__Deployment` values required
manually running `az cognitiveservices account list`/`az cognitiveservices account
deployment list` and reading JSON. `launch` and `--http` now offer an interactive
Spectre.Console picker instead when those env vars (and no saved config file) are
present, walking subscription → resource → deployment via `az`, probing the API
surface (same logic as `Foundry__Api=auto`), and optionally persisting the result to a
local `afclaude.config.json` (or a `--config`-specified path).

- `AzCli.cs`: typed `az ... -o json` shelling (subscriptions, Cognitive Services
  accounts filtered to `AIServices`/`OpenAI` kind, deployments), with error
  classification for "az not installed" / "not logged in" / timeout.
- `FoundryConfigFile.cs`: the saved-config JSON model and load/save. An explicitly
  named `--config <file>` that doesn't exist is a hard failure (`Missing Config
  <file>`); the default `afclaude.config.json` simply not existing yet is not an
  error. Env vars always outrank a saved file for any key they set.
- `CliArgs.cs`: `--select`/`--configure` (force the picker now — this also covers
  "reset" when a saved file already exists, since it's ignored) and `--config <file>`.
- `FoundryConfigWizard.cs`: the Spectre.Console selection flow — single-item stages
  skip the prompt automatically; the save prompt offers "Save to file" (editable
  filename, default suggested) or "Don't save".
- The interactive picker only ever runs in `launch`/`--http` modes, and only when a
  real terminal is attached (`Console.IsInputRedirected`/`IsOutputRedirected` both
  false) — never in MCP stdio mode, whose stdout is reserved for JSON-RPC and which
  Claude launches with no operator present. MCP mode still passively loads a saved
  config file if one exists.
- Exit criteria — verified: unit tests for JSON deserialization against real `az`
  output shapes, config-file load/save/precedence, CLI flag parsing, and the pure
  selection/save logic (via `Spectre.Console.Testing`'s `TestConsole`) all pass. A
  manual walkthrough against a real `az login` session (TESTING.md) confirms the live
  subscription → resource → deployment flow end to end.
```

- [ ] **Step 3: Commit**

```bash
git add README.md PLAN.md
git commit -m "Document the interactive Foundry picker (README, PLAN.md Phase 13)"
```

---

### Task 8: Manual end-to-end verification against a real `az` session

**Files:**
- Modify: `TESTING.md` (add a new stage; read the existing file first to match its numbering/format before editing)

**Interfaces:** none — this is a manual verification task, not new production code.

- [ ] **Step 1: Read `TESTING.md` to find the next free stage number and match its existing format**

Run: view `TESTING.md` in full and note the last stage number used (Phase 8-11's real-Foundry stages are numbered there, e.g. "Stage 6c", "Stage 7b" — find the highest and pick the next free top-level stage number).

- [ ] **Step 2: Add the new manual stage**

Add a new stage section to `TESTING.md` following the file's existing format exactly (its own header style, prerequisites block, numbered steps, expected-output blocks), covering:
1. Unset `Foundry__Endpoint`/`Foundry__Deployment`, remove any local `afclaude.config.json`, run `dotnet run -- launch --version` (or the packed `dnx AFClaude` equivalent) from a real interactive terminal with `az login` already done against a subscription with a known Foundry/OpenAI resource.
2. Confirm the subscription/resource/deployment prompts appear (or are silently skipped if there's exactly one of a given kind) and that selecting through them resolves to a working `Foundry__Endpoint`/`Foundry__Deployment` pair.
3. Confirm the API-surface probe result (anthropic/openai) matches what's independently known about that deployment.
4. Choose "Save to file", confirm `afclaude.config.json` is written with the expected JSON, then re-run the same command and confirm the picker is skipped (config loaded silently) this time.
5. Run again with `--select` and confirm the picker re-runs even though the saved file exists.
6. Run with `--config missing-file.json` and confirm it fails fast with exactly `Missing Config missing-file.json`.

- [ ] **Step 3: Actually run through the six checks above on a machine with a real `az login` session, and record PASS/FAIL for each in the same style as the other stages in `TESTING.md` (see e.g. Phase 8's "Stages 1-6b all PASS" summary in PLAN.md for the expected level of detail)**

- [ ] **Step 4: If any check fails, fix the responsible file from Tasks 2-6, re-run the full automated suite (`dotnet test AFClaude.slnx`), then re-run the failed manual check before continuing**

- [ ] **Step 5: Commit the TESTING.md update (and PLAN.md's Phase 13 entry if the manual run changed its "verified" claim)**

```bash
git add TESTING.md PLAN.md
git commit -m "Add manual verification stage for the interactive Foundry picker"
```

---

## Self-Review Notes

- **Spec coverage:** trigger/precedence (Task 6), az-driven subscription→resource→deployment wizard (Task 5), config file format + `--config`/`--select` flags (Tasks 3, 4, 6), API-mode probe-and-save (Task 5), MCP passive config-file load (Task 6 Step 2), documentation (Task 7), manual real-`az` verification (Task 8) — all covered.
- **Placeholder scan:** no TBD/TODO; the one open variable (exact `Spectre.Console` version) has a concrete default plus a fallback instruction, not a placeholder.
- **Type consistency:** `FoundryConfig(Endpoint, Deployment, Api)` used identically across Tasks 3, 5, 6; `AzSubscription`/`AzCognitiveServicesAccount`/`AzDeployment` field names match between Task 2's definition and Task 5's consumption (`.Endpoint`, `.ResourceGroup`, `.ModelName`/`.ModelVersion`).
- **Scope:** single cohesive feature, matches the approved design doc 1:1; no unrelated refactors bundled in.
