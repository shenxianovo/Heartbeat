#requires -Version 7.2

<#
.SYNOPSIS
Starts the local end-to-end stack (Postgres + backend + frontend + headless Hub) from local source.

.DESCRIPTION
Builds and starts compose.local.yml. The backend auto-migrates the database on startup,
so this works both with an empty database and one seeded by refresh-local-data.ps1.

Prerequisites: Docker Desktop running, .env.local exists (copy from .env.local.example).
#>
[CmdletBinding()]
param(
    [string] $ComposeFile,
    [string] $EnvFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($ComposeFile)) {
    $ComposeFile = Join-Path $repositoryRoot 'compose.local.yml'
}
if ([string]::IsNullOrWhiteSpace($EnvFile)) {
    $EnvFile = Join-Path $repositoryRoot '.env.local'
}

if (-not (Test-Path -LiteralPath $ComposeFile -PathType Leaf)) {
    throw "Compose file not found: $ComposeFile"
}
if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) {
    throw ".env.local not found. Run: Copy-Item .env.local.example .env.local"
}

$null = Get-Command docker -CommandType Application -ErrorAction Stop

$composeArguments = @('compose', '--file', $ComposeFile, '--env-file', $EnvFile)

& docker compose version *> $null
if ($LASTEXITCODE -ne 0) {
    throw 'Docker Compose v2 is required (the "docker compose" command).'
}
& docker info *> $null
if ($LASTEXITCODE -ne 0) {
    throw 'Docker is not ready. Start Docker Desktop and wait for the engine to finish starting.'
}

Write-Host '[1/3] Validating the local stack configuration...'
& docker @composeArguments config --quiet
if ($LASTEXITCODE -ne 0) {
    throw "docker compose config failed with exit code $LASTEXITCODE."
}

Write-Host '[2/3] Building and starting the local stack...'
& docker @composeArguments up --build --detach
if ($LASTEXITCODE -ne 0) {
    throw "docker compose up failed with exit code $LASTEXITCODE."
}

Write-Host '[3/3] Waiting for Analytics and the Headless Hub...'
$analyticsReady = $false
$hubReady = $false
$analyticsStatus = 0
$hubStatus = 0
for ($attempt = 1; $attempt -le 60; $attempt++) {
    if (-not $analyticsReady) {
        try {
            $response = Invoke-WebRequest -Uri 'http://127.0.0.1:8080/health' -TimeoutSec 2 -SkipHttpErrorCheck
            $analyticsStatus = $response.StatusCode
            $analyticsReady = $analyticsStatus -eq 200
        }
        catch { $analyticsStatus = 0 }
    }

    if (-not $hubReady) {
        try {
            $response = Invoke-WebRequest -Uri 'http://127.0.0.1:8080/hub/api/v1/collectors' -TimeoutSec 2 -SkipHttpErrorCheck
            $hubStatus = $response.StatusCode
            $hubReady = $hubStatus -in 401, 403
        }
        catch { $hubStatus = 0 }
    }

    if ($analyticsReady -and $hubReady) { break }
    Start-Sleep -Seconds 1
}
if (-not $analyticsReady -or -not $hubReady) {
    throw "The local stack did not become ready within 60 seconds (Analytics: $analyticsStatus, Headless Hub: $hubStatus). Check: docker compose --file '$ComposeFile' --env-file '$EnvFile' logs"
}

Write-Host "Local stack ready: http://localhost:8080"
