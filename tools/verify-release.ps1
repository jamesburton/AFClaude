# Verifies a published AFClaude version via dnx against a real Foundry deployment.
#   1. launch arg passthrough + the --yolo alias (claude hard-rejects unknown options,
#      so --yolo succeeding proves the translation) — safe, non-interactive.
#   2. bridge streaming: /v1/messages stream:true must produce MULTIPLE
#      content_block_delta frames (the pre-v0.4.0 burst emitted exactly one per block).
# Run from anywhere on a machine with az logged in and claude on PATH:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-release.ps1 -Version 0.4.0

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Endpoint = 'https://qhub-infra-resource.cognitiveservices.azure.com/',
    [string]$Deployment = 'gpt-4.1',
    [switch]$LaunchOnly
)

$ErrorActionPreference = 'Continue'
$env:Foundry__Endpoint = $Endpoint
$env:Foundry__Deployment = $Deployment

Write-Host "=== 1. launch passthrough + --yolo alias (AFClaude@$Version) ==="
$out = dotnet dnx "AFClaude@$Version" -y -- launch --yolo --version 2>&1
$ok1 = ($LASTEXITCODE -eq 0) -and (($out | Out-String) -match '\d+\.\d+')
Write-Host ("launch --yolo --version: " + $(if ($ok1) { 'PASS' } else { 'FAIL' }))
if (-not $ok1) { $out | Select-Object -Last 6 | ForEach-Object { Write-Host "  $_" } }

$ok2 = $true
if (-not $LaunchOnly) {
    Write-Host "=== 2. bridge streaming (/v1/messages stream:true vs $Deployment) ==="
    $env:ASPNETCORE_URLS = 'http://127.0.0.1:31399'
    $proxy = Start-Process dotnet -ArgumentList 'dnx', "AFClaude@$Version", '-y', '--', '--http' -PassThru -WindowStyle Hidden
    try {
        Start-Sleep -Seconds 25
        $body = @{
            model = $Deployment; max_tokens = 300; stream = $true
            messages = @(@{ role = 'user'; content = 'Count from 1 to 20, one number per line.' })
        } | ConvertTo-Json -Depth 5
        $resp = Invoke-WebRequest -Uri http://127.0.0.1:31399/v1/messages -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 120
        $deltas = ([regex]::Matches($resp.Content, 'content_block_delta')).Count
        $stopped = $resp.Content -match 'message_stop'
        $ok2 = ($deltas -ge 3) -and $stopped
        Write-Host "content_block_delta frames: $deltas; message_stop: $stopped"
        Write-Host ("streaming: " + $(if ($ok2) { 'PASS (incremental)' } else { 'FAIL' }))
    }
    catch {
        Write-Host "streaming: FAIL - $($_.Exception.Message)"
        $ok2 = $false
    }
    finally {
        taskkill /PID $proxy.Id /T /F 2>$null | Out-Null
    }
}

if ($ok1 -and $ok2) { Write-Host "VERIFY PASS (AFClaude@$Version)" -ForegroundColor Green; exit 0 }
Write-Host "VERIFY FAIL (AFClaude@$Version)" -ForegroundColor Red
exit 1
