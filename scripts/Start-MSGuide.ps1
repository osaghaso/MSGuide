#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)][int]$Port = 8765,
    [switch]$IntegrationTest,
    [switch]$CaptureTest,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
if ($IntegrationTest -and $CaptureTest) { throw 'Choose one test mode.' }
$root = Split-Path $PSScriptRoot -Parent
$python = Join-Path $root 'venv/Scripts/python.exe'
$project = Join-Path $root 'desktop/MSGuide.Desktop.csproj'
$desktop = Join-Path $root 'desktop/bin/Debug/net10.0-windows/MSGuide.Desktop.exe'
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
        & dotnet build $project --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
    }
    if (!(Test-Path $desktop)) { throw 'Desktop binary missing. Run without -SkipBuild.' }
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $token = [Convert]::ToBase64String($bytes)
    [Array]::Clear($bytes, 0, $bytes.Length)
    $url = "http://127.0.0.1:$Port"

    function New-LocalProcess([string]$File) {
        $info = [System.Diagnostics.ProcessStartInfo]::new()
        $info.FileName = $File
        $info.WorkingDirectory = $root
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.Environment['MSGUIDE_LOCAL_TOKEN'] = $token
        $info.Environment['MSGUIDE_API_URL'] = $url
        $info.Environment['MSGUIDE_MODE'] = 'demo'
        $info.Environment['PYTHONUTF8'] = '1'
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
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    # Bounded readiness check; wait on the owned child between connection attempts.
    while ([DateTime]::UtcNow -lt $deadline -and !$server.HasExited) {
        try {
            $json = $http.GetStringAsync("$url/health").GetAwaiter().GetResult() | ConvertFrom-Json
            if ($json.status -eq 'ok' -and $json.version -eq '0.2.0') {
                $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$url/v1/sessions")
                try {
                    $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token)
                    $reply = $http.SendAsync($request).GetAwaiter().GetResult()
                    try { $ready = $reply.IsSuccessStatusCode } finally { $reply.Dispose() }
                } finally { $request.Dispose() }
                if ($ready) { break }
            }
        } catch { }
        if ($server.WaitForExit(150)) { break }
    }
    if (!$ready) { throw 'Local API did not become ready. Check dependency installation and optional provider configuration.' }
    Write-Host "MSGuide ready at $url (local single-user mode). No token is written to disk."
    $start = New-LocalProcess $desktop
    # Only the Python service receives remote model credentials.
    [void]$start.Environment.Remove('MSGUIDE_MODEL_API_KEY')
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
