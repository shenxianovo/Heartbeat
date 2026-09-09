#requires -Version 7.2

<#
.SYNOPSIS
Starts the local end-to-end stack (Postgres + backend + frontend + headless Hub) from local source.

.DESCRIPTION
Builds and starts compose.local.yml. The backend auto-migrates the database on startup,
so this works both with an empty database and one seeded by refresh-local-data.ps1.
Uses the existing local database; never downloads or restores server data.

Prerequisites: Docker Desktop running, .env.local exists (copy from .env.local.example).
Use -Desktop to also run the native development Desktop in the foreground, or -DesktopOnly
to use an existing local stack without invoking Docker. Desktop uses this checkout's
.local/desktop Profile and http://localhost:8080, and opens its settings window.
Exit it from its menu; Compose stays running.
#>
[CmdletBinding()]
param(
    [string] $ComposeFile,
    [string] $EnvFile,
    [ValidateRange(1, 2147483647)]
    [int] $WaitTimeoutSeconds = 1800,
    [switch] $Desktop,
    [switch] $DesktopOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$desktopProject = $null
if ($Desktop -or $DesktopOnly) {
    if ($IsWindows) {
        $desktopProject = Join-Path $repositoryRoot 'collection/desktop/Heartbeat.Desktop.Windows/Heartbeat.Desktop.Windows.csproj'
    }
    elseif ($IsMacOS) {
        $desktopProject = Join-Path $repositoryRoot 'collection/desktop/Heartbeat.Desktop.Mac/Heartbeat.Desktop.Mac.csproj'
    }
    else {
        throw 'Desktop requires Windows or macOS.'
    }
    $null = Get-Command dotnet -CommandType Application -ErrorAction Stop
}

function Start-DevelopmentDesktop {
    $dataDirectory = Join-Path $repositoryRoot '.local/desktop'
    Write-Host "Starting development Desktop: $dataDirectory -> http://localhost:8080"
    Write-Host 'Exit Desktop from its menu to stop it; Compose services remain running.'
    $previousApiBaseUrl = $env:HEARTBEAT_API_BASE_URL
    $previousShowSettings = $env:HEARTBEAT_SHOW_SETTINGS_ON_START
    try {
        $env:HEARTBEAT_API_BASE_URL = 'http://localhost:8080'
        $env:HEARTBEAT_SHOW_SETTINGS_ON_START = '1'
        & dotnet run --project $desktopProject -- --data-directory $dataDirectory
        $desktopExitCode = $LASTEXITCODE
    }
    finally {
        $env:HEARTBEAT_API_BASE_URL = $previousApiBaseUrl
        $env:HEARTBEAT_SHOW_SETTINGS_ON_START = $previousShowSettings
    }
    exit $desktopExitCode
}

if ($DesktopOnly) {
    Start-DevelopmentDesktop
}

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

Write-Host '[2/3] Building and starting the local stack (backend applies pending database migrations)...'
& docker @composeArguments up --build --detach
if ($LASTEXITCODE -ne 0) {
    throw "docker compose up failed with exit code $LASTEXITCODE."
}

Write-Host '[3/3] Waiting for Analytics and the Headless Hub...'
$analyticsReady = $false
$hubReady = $false
$analyticsStatus = 0
$hubStatus = 0
$wait = [Diagnostics.Stopwatch]::StartNew()
$nextProgress = 0
$initialRestarts = @{}
while ($wait.Elapsed.TotalSeconds -lt $WaitTimeoutSeconds) {
    foreach ($service in @('backend', 'headless')) {
        $container = & docker @composeArguments ps --all --quiet $service
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($container)) {
            throw "$service failed: container is missing. Check docker compose logs using the same compose/env files."
        }
        $stateJson = & docker inspect --format '{{json .State}}' $container
        if ($LASTEXITCODE -ne 0) { throw "Cannot inspect $service container." }
        $state = $stateJson | ConvertFrom-Json
        $restarts = & docker inspect --format '{{.RestartCount}}' $container
        if ($LASTEXITCODE -ne 0) { throw "Cannot inspect $service restart count." }
        if (-not $initialRestarts.ContainsKey($service)) { $initialRestarts[$service] = $restarts }
        if ($state.Status -ne 'running' -or $state.OOMKilled -or $restarts -ne $initialRestarts[$service]) {
            throw "$service failed: state=$($state.Status), exit=$($state.ExitCode), OOM=$($state.OOMKilled), restarts=$restarts. Check docker compose logs using the same compose/env files."
        }
    }
    try {
        $response = Invoke-WebRequest -Uri 'http://127.0.0.1:8080/health' -TimeoutSec 2 -SkipHttpErrorCheck
        $analyticsStatus = $response.StatusCode
    }
    catch { $analyticsStatus = 0 }
    $analyticsReady = $analyticsStatus -eq 200

    try {
        $response = Invoke-WebRequest -Uri 'http://127.0.0.1:8080/hub/api/v1/collectors' -TimeoutSec 2 -SkipHttpErrorCheck
        $hubStatus = $response.StatusCode
    }
    catch { $hubStatus = 0 }
    $hubReady = $hubStatus -in 401, 403

    if ($analyticsReady -and $hubReady) { break }
    if ($wait.Elapsed.TotalSeconds -ge $nextProgress) {
        Write-Host "Waiting for startup/migrations: $([int]$wait.Elapsed.TotalSeconds)/${WaitTimeoutSeconds}s (Analytics: $analyticsStatus, Headless Hub: $hubStatus)."
        $nextProgress = $wait.Elapsed.TotalSeconds + 15
    }
    Start-Sleep -Seconds 1
}
if (-not $analyticsReady -or -not $hubReady) {
    throw "The local stack did not become ready within ${WaitTimeoutSeconds}s (Analytics: $analyticsStatus, Headless Hub: $hubStatus). The database may still be migrating; inspect logs before restarting. Check: docker compose --file '$ComposeFile' --env-file '$EnvFile' logs"
}

Write-Host "Local stack ready: http://localhost:8080"
if ($Desktop) {
    Start-DevelopmentDesktop
}
