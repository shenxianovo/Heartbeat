#requires -Version 7.2
# Both entrypoints intentionally forward the same --project / --option syntax.
# Do not add PowerShell parameter binding here: it would reinterpret shared arguments.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$null = Get-Command node -CommandType Application -ErrorAction Stop
& node (Join-Path $PSScriptRoot 'start-local.mjs') @args
exit $LASTEXITCODE
