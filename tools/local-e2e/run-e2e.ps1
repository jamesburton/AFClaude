# Local end-to-end regression for launch-mode tool use — no Azure needed.
#
# Drives the REAL `claude` CLI through `AFClaude launch` against a fake stack:
#   - fake-az\az.cmd : stub `az` returning a fake Entra token (prepended to PATH)
#   - FakeAzure\     : fake Foundry serving BOTH API surfaces —
#       * Azure-OpenAI chat-completions (function-calling shapes)
#       * native Anthropic /anthropic/v1/messages (Anthropic shapes incl. SSE,
#         400 without anthropic-version — mirrors real Foundry Claude deployments)
#
# Two legs, both must print the probe file's contents in claude's final answer:
#   openai    — Foundry__Api=openai: exercises the Anthropic<->OpenAI tool bridge
#   anthropic — Foundry__Api unset (auto): exercises API auto-detection (probe)
#               plus the native passthrough path, streaming included
#
# Prerequisites: `claude` on PATH; trusted ASP.NET dev cert (`dotnet dev-certs https --trust`).
# The fake endpoint binds https://127.0.0.1:41443.

param(
    [ValidateSet('openai', 'anthropic', 'both')]
    [string]$Api = 'both'
)

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

function Invoke-Leg([string]$leg) {
    $env:PATH = "$PSScriptRoot\fake-az;" + $env:PATH
    $env:Foundry__Endpoint = 'https://127.0.0.1:41443/'
    $env:Foundry__Deployment = if ($leg -eq 'anthropic') { 'claude-fake' } else { 'gpt-fake' }
    $env:Foundry__Api = if ($leg -eq 'anthropic') { '' } else { 'openai' }   # anthropic leg tests auto-detection
    $env:AFClaude__TraceDir = "$work\trace-$leg"

    Push-Location $work
    try {
        $out = dotnet run --project "$root\src\AFClaude\AFClaude.csproj" -c Release --no-build --no-launch-profile -- `
            launch -p "Read the file $work\afclaude-probe.txt and reply with only its contents." --allowedTools "Read" 2>&1
    }
    finally {
        Pop-Location
    }

    if (($out | Out-String) -match 'PROBE-VALUE-12345') {
        Write-Host "[$leg] PASS: probe value round-tripped claude -> bridge/passthrough -> tool -> final answer." -ForegroundColor Green
        return $true
    }
    Write-Host "[$leg] FAIL: probe value missing from claude output." -ForegroundColor Red
    $out | Select-Object -Last 12
    return $false
}

try {
    Start-Sleep -Seconds 6
    $legs = if ($Api -eq 'both') { @('openai', 'anthropic') } else { @($Api) }
    $ok = $true
    foreach ($leg in $legs) {
        if (-not (Invoke-Leg $leg)) { $ok = $false }
    }
    Write-Host "Traces and fake-model logs: $work"
    if (-not $ok) { exit 1 }
    Write-Host "E2E PASS ($($legs -join ' + '))" -ForegroundColor Green
    exit 0
}
finally {
    Stop-Process -Id $srv.Id -Force -Confirm:$false -ErrorAction SilentlyContinue
}
