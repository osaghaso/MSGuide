#requires -Version 7.0
[CmdletBinding()]
param([switch]$AcceptDownload)

$ErrorActionPreference = 'Stop'
if (!$AcceptDownload) {
    throw 'Downloading the local speech model requires explicit approval. Rerun with -AcceptDownload to download about 466 MiB. No microphone audio is uploaded.'
}
$directory = Join-Path $env:LOCALAPPDATA 'MSGuide\models'
$model = Join-Path $directory 'ggml-small.en.bin'
$sha256 = 'c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d'
if (Test-Path -LiteralPath $model) {
    if ((Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash -ne $sha256) {
        throw 'An existing model has an unexpected digest. It was not overwritten; remove or relocate it after reviewing its origin.'
    }
    Write-Host "Local speech model already verified: $model"
    return
}
[void](New-Item -ItemType Directory -Path $directory -Force)
$partial = Join-Path $directory ("speech-download-" + [Guid]::NewGuid().ToString('N') + '.partial')
try {
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin' `
        -OutFile $partial -TimeoutSec 600
    if ((Get-Item -LiteralPath $partial).Length -ne 487614201 -or
        (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $sha256) {
        throw 'The downloaded speech model failed size/digest verification.'
    }
    Move-Item -LiteralPath $partial -Destination $model
    Write-Host "Local speech model downloaded and verified: $model"
} finally {
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
}
