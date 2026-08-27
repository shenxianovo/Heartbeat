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

Write-Host '[1/2] Building and starting the local stack...'
& docker @composeArguments up --build --detach
if ($LASTEXITCODE -ne 0) {
    throw "docker compose up failed with exit code $LASTEXITCODE."
}

Write-Host '[2/2] Waiting for http://localhost:8080...'
$ready = $false
for ($attempt = 1; $attempt -le 60; $attempt++) {
    try {
        $response = Invoke-WebRequest -Uri 'http://127.0.0.1:8080/' -TimeoutSec 2 -SkipHttpErrorCheck
        if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500) {
            $ready = $true
            break
        }
    }
    catch {}
    Start-Sleep -Seconds 1
}
if (-not $ready) {
    throw 'http://localhost:8080 did not become ready within 60 seconds. Check: docker compose -f compose.local.yml --env-file .env.local logs'
}

$runningServices = @(& docker @composeArguments ps --status running --services headless)
if ($LASTEXITCODE -ne 0 -or $runningServices -notcontains 'headless') {
    throw 'The headless Hub did not remain running. Check: docker compose -f compose.local.yml --env-file .env.local logs headless'
}

Write-Host "Local stack ready: http://localhost:8080"
