#requires -Version 7.2

<#
.SYNOPSIS
Replaces the local E2E PostgreSQL database with a transaction-consistent server snapshot.

.DESCRIPTION
Runs pg_dump inside the server's database container over SSH, downloads the custom-format
stream without exposing PostgreSQL, recreates the project-local database directory, restores
the dump, and prints its EF migration history without applying migrations.
Only PostgreSQL is started. Application services remain stopped. Run start-local.ps1
separately when ready to start the stack and apply pending migrations.

The dump contains private activity data. It is deleted by default after the restore.
#>
[CmdletBinding()]
param(
    [string] $SshDestination,

    [Alias('RemoteDir')]
    [ValidateNotNullOrEmpty()]
    [string] $RemoteDirectory = '/srv/heartbeat',

    [ValidateNotNullOrEmpty()]
    [string] $RemoteComposeFile = 'compose.yml',

    [ValidateNotNullOrEmpty()]
    [string] $RemoteEnvFile = '.env',

    [ValidateRange(1, 65535)]
    [int] $SshPort = 22,

    [string] $IdentityFile,

    [string] $ComposeFile,

    [string] $EnvFile,

    [switch] $KeepDump,

    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($SshDestination)) {
    $SshDestination = Read-Host 'SSH destination (for example user@example.com)'
    if ([string]::IsNullOrWhiteSpace($SshDestination)) {
        throw 'SSH destination is required.'
    }
}

if (-not $PSBoundParameters.ContainsKey('RemoteDirectory')) {
    $enteredRemoteDirectory = Read-Host "Remote directory [$RemoteDirectory]"
    if (-not [string]::IsNullOrWhiteSpace($enteredRemoteDirectory)) {
        $RemoteDirectory = $enteredRemoteDirectory
    }
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($ComposeFile)) {
    $ComposeFile = Join-Path $repositoryRoot 'compose.local.yml'
}
if ([string]::IsNullOrWhiteSpace($EnvFile)) {
    $EnvFile = Join-Path $repositoryRoot '.env.local'
}

function Resolve-RequiredFile {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description not found: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function ConvertTo-PosixShellArgument {
    param([Parameter(Mandatory)][string] $Value)

    $singleQuote = [string][char]39
    $doubleQuote = [string][char]34
    $escapedSingleQuote = $singleQuote + $doubleQuote + $singleQuote + $doubleQuote + $singleQuote
    return $singleQuote + $Value.Replace($singleQuote, $escapedSingleQuote) + $singleQuote
}

function Invoke-Docker {
    param([Parameter(Mandatory)][string[]] $Arguments)

    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Test-LocalDatabaseReady {
    $result = & docker @composeArguments exec -T db psql --username=heartbeat --dbname=heartbeat --tuples-only --no-align --command 'SELECT 1' 2>$null
    return ($LASTEXITCODE -eq 0 -and $result -and $result.Trim() -eq '1')
}

function Export-RemoteDatabase {
    param(
        [Parameter(Mandatory)][string] $Destination,
        [Parameter(Mandatory)][string] $RemoteCommand,
        [Parameter(Mandatory)][System.Management.Automation.ApplicationInfo] $SshCommand
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $SshCommand.Source
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    # Keep stderr and stdin attached to the current console. OpenSSH uses the console
    # to display and securely read password/host-key prompts while stdout carries dump bytes.
    $startInfo.RedirectStandardError = $false
    $startInfo.RedirectStandardInput = $false
    $startInfo.CreateNoWindow = $false

    $startInfo.ArgumentList.Add('-o')
    $startInfo.ArgumentList.Add('BatchMode=no')
    $startInfo.ArgumentList.Add('-o')
    $startInfo.ArgumentList.Add('NumberOfPasswordPrompts=3')
    $startInfo.ArgumentList.Add('-p')
    $startInfo.ArgumentList.Add($SshPort.ToString())
    if (-not [string]::IsNullOrWhiteSpace($IdentityFile)) {
        $startInfo.ArgumentList.Add('-i')
        $startInfo.ArgumentList.Add($IdentityFile)
    }
    $startInfo.ArgumentList.Add($SshDestination)
    $startInfo.ArgumentList.Add($RemoteCommand)

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stream = $null
    try {
        $stream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        if (-not $process.Start()) {
            throw 'Failed to start ssh.'
        }

        # Copy stdout as bytes. A PowerShell native pipeline can corrupt custom-format dumps
        # on older hosts by decoding and re-encoding the stream.
        $copyTask = $process.StandardOutput.BaseStream.CopyToAsync($stream)
        $null = $copyTask.GetAwaiter().GetResult()
        $process.WaitForExit()

        if ($process.ExitCode -ne 0) {
            throw "Remote pg_dump failed with exit code $($process.ExitCode). See the SSH error above."
        }
    }
    finally {
        if ($stream) { $stream.Dispose() }
        $process.Dispose()
    }
}

$ComposeFile = Resolve-RequiredFile -Path $ComposeFile -Description 'Local Compose file'
$EnvFile = Resolve-RequiredFile -Path $EnvFile -Description 'Local environment file'
if (-not [string]::IsNullOrWhiteSpace($IdentityFile)) {
    $IdentityFile = Resolve-RequiredFile -Path $IdentityFile -Description 'SSH identity file'
}

$null = Get-Command docker -CommandType Application -ErrorAction Stop
$sshCommand = Get-Command ssh -CommandType Application -ErrorAction Stop
$composeArguments = @('compose', '--file', $ComposeFile, '--env-file', $EnvFile)
$localDataRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '.local'))
$localDatabaseDirectory = [IO.Path]::GetFullPath((Join-Path $localDataRoot 'postgres-data'))
$directorySeparator = [IO.Path]::DirectorySeparatorChar
$localDataPrefix = $localDataRoot.TrimEnd($directorySeparator, [IO.Path]::AltDirectorySeparatorChar) + $directorySeparator
if (-not $localDatabaseDirectory.StartsWith($localDataPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to manage a database directory outside $localDataRoot"
}

Invoke-Docker -Arguments ($composeArguments + @('config', '--quiet'))

if (-not $Force) {
    Write-Warning "This replaces $localDatabaseDirectory with a snapshot containing private server data."
    $confirmation = Read-Host 'Type REPLACE to continue'
    if ($confirmation -cne 'REPLACE') {
        Write-Host 'Cancelled; no data was changed.'
        return
    }
}

$dumpPath = Join-Path ([IO.Path]::GetTempPath()) ("heartbeat-server-{0}.dump" -f [guid]::NewGuid().ToString('N'))
$containerDumpPath = '/tmp/heartbeat-server.dump'
$dumpCopiedToContainer = $false

$quotedDirectory = ConvertTo-PosixShellArgument $RemoteDirectory
$quotedComposeFile = ConvertTo-PosixShellArgument $RemoteComposeFile
$quotedEnvFile = ConvertTo-PosixShellArgument $RemoteEnvFile
$remoteCommand = @(
    'set -eu;'
    "cd -- $quotedDirectory;"
    "docker compose --file $quotedComposeFile --env-file $quotedEnvFile exec -T db"
    'pg_dump --username=heartbeat --dbname=heartbeat --format=custom --compress=6 --no-owner --no-privileges'
) -join ' '

try {
    Write-Host '[1/4] Streaming a transaction-consistent server snapshot over SSH...'
    Export-RemoteDatabase -Destination $dumpPath -RemoteCommand $remoteCommand -SshCommand $sshCommand

    $header = [byte[]]::new(5)
    $headerStream = [IO.File]::OpenRead($dumpPath)
    try {
        if ($headerStream.Read($header, 0, $header.Length) -ne $header.Length -or
            [Text.Encoding]::ASCII.GetString($header) -ne 'PGDMP') {
            throw 'The downloaded file is not a PostgreSQL custom-format dump.'
        }
    }
    finally {
        $headerStream.Dispose()
    }

    $sizeMiB = [Math]::Round((Get-Item -LiteralPath $dumpPath).Length / 1MB, 1)
    Write-Host "      Downloaded $sizeMiB MiB."

    Write-Host '[2/4] Recreating the project-local database directory...'
    Invoke-Docker -Arguments ($composeArguments + @('down', '--remove-orphans'))
    if (Test-Path -LiteralPath $localDatabaseDirectory) {
        Remove-Item -LiteralPath $localDatabaseDirectory -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Path $localDatabaseDirectory -Force
    Invoke-Docker -Arguments ($composeArguments + @('up', '--detach', '--no-deps', 'db'))

    $ready = $false
    $consecutiveSuccesses = 0
    for ($attempt = 1; $attempt -le 30; $attempt++) {
        if (Test-LocalDatabaseReady) {
            $consecutiveSuccesses++
            if ($consecutiveSuccesses -ge 3) {
                $ready = $true
                break
            }
        }
        else {
            $consecutiveSuccesses = 0
        }
        Start-Sleep -Seconds 2
    }
    if (-not $ready) {
        throw 'The local PostgreSQL container did not become ready within 60 seconds.'
    }

    Write-Host '[3/4] Restoring the snapshot into local PostgreSQL...'
    Invoke-Docker -Arguments ($composeArguments + @('cp', $dumpPath, "db:$containerDumpPath"))
    $dumpCopiedToContainer = $true
    Invoke-Docker -Arguments ($composeArguments + @(
        'exec', '-T', 'db',
        'pg_restore', '--username=heartbeat', '--dbname=heartbeat',
        '--single-transaction', '--exit-on-error', '--no-owner', '--no-privileges',
        $containerDumpPath
    ))

    Write-Host '[4/4] Reading the restored snapshot migration history (without applying migrations)...'
    $serverMigrations = @(
        & docker @composeArguments exec -T db psql --tuples-only --no-align --username=heartbeat --dbname=heartbeat `
            --command 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";'
    )
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not read __EFMigrationsHistory from the restored database.'
    }
    $serverMigrations = @($serverMigrations | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    Write-Host '      Snapshot migrations:'
    $serverMigrations | ForEach-Object { Write-Host $_ }
    Write-Host 'Local database refresh completed. No application migrations were applied.'
    Write-Host 'Only PostgreSQL is running; backend, frontend and Headless Hub remain stopped.'
    Write-Host 'Inspect this snapshot before running start-local.ps1; starting the backend applies pending migrations.'

}
finally {
    if ($dumpCopiedToContainer) {
        & docker @composeArguments exec -T db rm -f $containerDumpPath *> $null
    }

    if ($KeepDump) {
        if (Test-Path -LiteralPath $dumpPath) {
            Write-Warning "Sensitive server dump retained at: $dumpPath"
        }
    }
    elseif (Test-Path -LiteralPath $dumpPath) {
        Remove-Item -LiteralPath $dumpPath -Force
    }
}
