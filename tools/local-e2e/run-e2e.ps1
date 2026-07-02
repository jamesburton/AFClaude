# Local end-to-end regression for launch-mode tool bridging — no Azure needed.
#
# Drives the REAL `claude` CLI through `AFClaude launch` against a fake stack:
#   - fake-az\az.cmd        : stub `az` that returns a fake Entra token (prepended to PATH)
#   - FakeAzure\            : fake Azure OpenAI endpoint that scripts a Read tool call,
#                             then echoes the tool result back as the final answer
# PASS means the probe file's contents round-tripped:
#   claude request -> AFClaude bridge -> fake model tool_call -> bridge tool_use ->
#   claude executes Read -> tool_result -> bridge -> fake model echo -> claude prints it.
# This validates the whole Anthropic<->OpenAI tool translation against the real client.
#
# Prerequisites: `claude` on PATH; trusted ASP.NET dev cert (`dotnet dev-certs https --trust`).
# The fake endpoint binds https://127.0.0.1:41443.

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
$work = Join-Path ([IO.Path]::GetTempPath()) "afclaude-e2e-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force $work | Out-Null
Set-Content "$work\afclaude-probe.txt" -Value "PROBE-VALUE-12345"

dotnet build "$root\src\AFClaude\AFClaude.csproj" -c Release -v q | Out-Null
dotnet build "$PSScriptRoot\FakeAzure\FakeAzure.csproj" -c Release -v q | Out-Null

$env:FAKE_PROBE_PATH = "$work\afclaude-probe.txt"
$env:FAKE_LOG_DIR = "$work\fake-azure-logs"
$srv = Start-Process dotnet -ArgumentList 'run', '--project', "$PSScriptRoot\FakeAzure\FakeAzure.csproj", '-c', 'Release', '--no-build', '--no-launch-profile' -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Seconds 6

    $env:PATH = "$PSScriptRoot\fake-az;" + $env:PATH
    $env:Foundry__Endpoint = 'https://127.0.0.1:41443/'
    $env:Foundry__Deployment = 'gpt-fake'
    $env:AFClaude__TraceDir = "$work\trace"

    Push-Location $work
    try {
        $out = dotnet run --project "$root\src\AFClaude\AFClaude.csproj" -c Release --no-build --no-launch-profile -- `
            launch -p "Read the file $work\afclaude-probe.txt and reply with only its contents." --allowedTools "Read" 2>&1
    }
    finally {
        Pop-Location
    }

    if (($out | Out-String) -match 'PROBE-VALUE-12345') {
        Write-Host "E2E PASS: probe value round-tripped claude -> bridge -> tool_call -> Read -> tool_result -> final answer." -ForegroundColor Green
        Write-Host "Traces and fake-model logs: $work"
        exit 0
    }

    Write-Host "E2E FAIL: probe value missing from claude output. Inspect $work" -ForegroundColor Red
    $out | Select-Object -Last 12
    exit 1
}
finally {
    Stop-Process -Id $srv.Id -Force -Confirm:$false -ErrorAction SilentlyContinue
}
