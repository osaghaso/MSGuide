#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)][int]$Port = 8765,
    [switch]$IntegrationTest,
    [switch]$CaptureTest,
    [switch]$SkipBuild,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$Copilot,
    [switch]$UiaOnly,
    [switch]$CameraFixture,
    [switch]$Shareable,
    [switch]$DeveloperTools
)

$ErrorActionPreference = 'Stop'
if ($IntegrationTest -and $CaptureTest) { throw 'Choose one test mode.' }
if ($Copilot -and ($IntegrationTest -or $CaptureTest)) {
    throw 'Copilot mode is for the interactive app, not deterministic test harnesses.'
}
if ($UiaOnly -and !$Copilot) { throw '-UiaOnly requires -Copilot for automatic task planning.' }
$root = Split-Path $PSScriptRoot -Parent
$python = Join-Path $root 'venv/Scripts/python.exe'
$project = Join-Path $root 'desktop/MSGuide.Desktop.csproj'
$desktop = Join-Path $root "desktop\bin\$Configuration\net10.0-windows10.0.19041.0\MSGuide.Desktop.exe"
if (!(Test-Path $python)) { throw 'Set up the Python environment first; see README.md.' }
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK is required.' }
$probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
try { $probe.Start() } catch { throw "Port $Port is already in use. Choose another -Port; existing processes will not be stopped." }
finally { $probe.Stop() }

Push-Location $root
$server = $null
$client = $null
$http = $null
try {
    if (!$SkipBuild) {
        & dotnet build $project --configuration $Configuration --nologo -p:RestoreLockedMode=true
        if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
    }
    if (!(Test-Path $desktop)) { throw 'Desktop binary missing. Run without -SkipBuild.' }
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $token = [Convert]::ToBase64String($bytes)
    [Array]::Clear($bytes, 0, $bytes.Length)
    $url = "http://127.0.0.1:$Port"
    $logDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MSGuide\logs'
    [void](New-Item -ItemType Directory -Path $logDirectory -Force)
    $backendLog = Join-Path $logDirectory 'backend.log'
    $desktopLog = Join-Path $logDirectory 'desktop.log'

    function New-LocalProcess([string]$File) {
        $info = [System.Diagnostics.ProcessStartInfo]::new()
        $info.FileName = $File
        $info.WorkingDirectory = $root
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.Environment['MSGUIDE_LOCAL_TOKEN'] = $token
        $info.Environment['MSGUIDE_API_URL'] = $url
        $info.Environment['MSGUIDE_DIAGNOSTIC_LOG'] = $backendLog
        $info.Environment['MSGUIDE_DESKTOP_LOG'] = $desktopLog
        $info.Environment['MSGUIDE_MODE'] = 'demo'
        $info.Environment['MSGUIDE_UIA_ONLY'] = if ($UiaOnly) { '1' } else { '0' }
        $info.Environment['PYTHONUTF8'] = '1'
        $info.Environment['MSGUIDE_DEVELOPER_TOOLS'] =
            if ($DeveloperTools -or $IntegrationTest -or $CaptureTest) { '1' } else { '0' }
        if ($CameraFixture) {
            $info.Environment['MSGUIDE_CAMERA_RECOVERY_MODE'] = 'fixture'
        }
        if ($Shareable) {
            $info.Environment['MSGUIDE_ALLOW_SCREEN_SHARE'] = '1'
        }
        if ($Copilot) {
            $copilotCommand = Get-Command copilot.exe -CommandType Application -ErrorAction Stop
            $info.Environment['MSGUIDE_GUIDANCE_PROVIDER'] = 'copilot-sdk'
            $info.Environment['MSGUIDE_SESSION_SCREEN_CONTEXT'] = '1'
            $info.Environment['MSGUIDE_SESSION_AUTOMATION'] = '1'
            $info.Environment['MSGUIDE_COPILOT_MODEL'] =
                if ([string]::IsNullOrWhiteSpace($env:MSGUIDE_COPILOT_MODEL)) {
                    'gpt-6-astra'
                } else { $env:MSGUIDE_COPILOT_MODEL }
            $info.Environment['MSGUIDE_COPILOT_REASONING_EFFORT'] =
                if ([string]::IsNullOrWhiteSpace($env:MSGUIDE_COPILOT_REASONING_EFFORT)) {
                    'low'
                } else { $env:MSGUIDE_COPILOT_REASONING_EFFORT }
            $info.Environment['MSGUIDE_COPILOT_CONTEXT_TIER'] =
                if ([string]::IsNullOrWhiteSpace($env:MSGUIDE_COPILOT_CONTEXT_TIER)) {
                    'default'
                } else { $env:MSGUIDE_COPILOT_CONTEXT_TIER }
            $info.Environment['COPILOT_CLI_PATH'] = $copilotCommand.Source
        }
        if ($IntegrationTest -or $CaptureTest) { $info.Environment['MSGUIDE_GUIDANCE_PROVIDER'] = 'demo' }
        return $info
    }
    $start = New-LocalProcess $python
    foreach ($arg in @('-m', 'uvicorn', 'src.main:app', '--host', '127.0.0.1', '--port', "$Port", '--no-access-log')) {
        $start.ArgumentList.Add($arg)
    }
    $server = [System.Diagnostics.Process]::Start($start)
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $http = [System.Net.Http.HttpClient]::new($handler)
    $http.Timeout = [TimeSpan]::FromSeconds(2)
    $ready = $false
    # Provider startup is bounded to 30 seconds; allow another 30 for Python/imports and readiness.
    $readinessSeconds = 60
    $deadline = [DateTime]::UtcNow.AddSeconds($readinessSeconds)
    $readinessStage = 'connect-health'
    $readinessError = 'none'
    Write-Host "Starting local API/provider; readiness budget is $readinessSeconds seconds. Diagnostics: $backendLog"
    # Bounded readiness check; wait on the owned child between connection attempts.
    while ([DateTime]::UtcNow -lt $deadline -and !$server.HasExited) {
        try {
            $json = $http.GetStringAsync("$url/health").GetAwaiter().GetResult() | ConvertFrom-Json
            $readinessStage = 'validate-health'
            if ($json.status -eq 'ok' -and $json.version -eq '0.2.0') {
                $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$url/v1/sessions")
                $readinessStage = 'authenticate-local-session'
                try {
                    $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token)
                    $reply = $http.SendAsync($request).GetAwaiter().GetResult()
                    try
                    {
                        $ready = $reply.IsSuccessStatusCode
                        $readinessError = "HTTP $([int]$reply.StatusCode)"
                    }
                    finally { $reply.Dispose() }
                } finally { $request.Dispose() }
                if ($ready) { break }
            }
        } catch { $readinessError = $_.Exception.GetType().Name }
        if ($server.WaitForExit(150)) { break }
    }
    if (!$ready) {
        $failure = if ($server.HasExited) { "child exited with code $($server.ExitCode)" }
            else { "$readinessSeconds-second readiness deadline exceeded" }
        throw "Local API startup failed: $failure; stage=$readinessStage; lastError=$readinessError. See $backendLog for provider startup diagnostics."
    }
    Write-Host "MSGuide ready at $url (local single-user mode). Diagnostic logs: $logDirectory"
    if ($UiaOnly) {
        Write-Host 'UIA-only task planning: screenshots are off. Omit -UiaOnly for tasks requiring visual context.'
    }
    if ($Shareable) {
        Write-Warning 'Shareable demo mode is on. MSGuide can appear when you share the full screen.'
    }
    $start = New-LocalProcess $desktop
    # Only the Python service receives remote model credentials.
    [void]$start.Environment.Remove('MSGUIDE_MODEL_API_KEY')
    [void]$start.Environment.Remove('COPILOT_GITHUB_TOKEN')
    if ($IntegrationTest) {
        foreach ($arg in @('--integration-test', '--test-results', 'desktop/obj/integration-results.json')) { $start.ArgumentList.Add($arg) }
    }
    if ($CaptureTest) {
        foreach ($arg in @('--capture-test', '--test-results', 'desktop/obj/capture-results.json')) { $start.ArgumentList.Add($arg) }
    }
    $client = [System.Diagnostics.Process]::Start($start)
    $client.WaitForExit()
    if ($client.ExitCode -ne 0) { throw "Desktop exited with code $($client.ExitCode)." }
} finally {
    if ($client -and !$client.HasExited) { $client.Kill($true); $client.WaitForExit() }
    if ($server -and !$server.HasExited) { $server.Kill($true); $server.WaitForExit() }
    if ($http) { $http.Dispose() }
    if ($client) { $client.Dispose() }
    if ($server) { $server.Dispose() }
    $token = $null
    Pop-Location
}
