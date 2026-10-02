<#
.SYNOPSIS
    Deactivates (or, with -Reactivate, reactivates) a Memory-MCP user.

.DESCRIPTION
    A deactivated user's keys are all rejected from the next request on, without finding or touching
    them one by one — the offboarding step that covers every credential the person ever minted. Their
    memories and documents stay, and keep their name.

    Reversible: -Reactivate makes their non-revoked keys work again. Keys revoked individually with
    scripts/revoke-key.ps1 stay revoked.

    Runs the API project's --deactivate-user / --activate-user command, so it reads the connection
    string exactly the way the app does (ConnectionStrings:Default in src/MemoryMcp.Api/appsettings*.json,
    or the ConnectionStrings__Default environment variable).

.PARAMETER Email
    Email of the user. Must already exist.

.PARAMETER Reactivate
    Undo a deactivation instead of performing one.

.PARAMETER Configuration
    Build configuration for the one-shot run. Defaults to Release, because a Memory-MCP server left
    running from `dotnet run` holds a lock on the Debug output and would fail the build.

.EXAMPLE
    ./scripts/deactivate-user.ps1 -Email alice@example.com

.EXAMPLE
    ./scripts/deactivate-user.ps1 -Email alice@example.com -Reactivate
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Email,

    [switch]$Reactivate,

    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $repoRoot "src/MemoryMcp.Api"

$verb = if ($Reactivate) { "--activate-user" } else { "--deactivate-user" }

[Environment]::SetEnvironmentVariable("Logging__LogLevel__Microsoft.EntityFrameworkCore", "Warning")

Write-Host "$(if ($Reactivate) { 'Reactivating' } else { 'Deactivating' }) $Email..." -ForegroundColor Yellow
dotnet run -c $Configuration --project $apiProject -- $verb --email $Email

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
