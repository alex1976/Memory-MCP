<#
.SYNOPSIS
    Revokes one Memory-MCP API key.

.DESCRIPTION
    The key stops authenticating from the next request on. Only that credential is affected: the owner
    and their other keys keep working. To cut off a whole person, use scripts/deactivate-user.ps1.

    Revocation cannot be undone — mint a new key with scripts/create-api-key.ps1 instead. Revoking a key
    that is already revoked is a no-op.

    Runs the API project's --revoke-key command, so it reads the connection string exactly the way the
    app does (ConnectionStrings:Default in src/MemoryMcp.Api/appsettings*.json, or the
    ConnectionStrings__Default environment variable).

.PARAMETER Key
    The key id (GUID), or the key prefix printed when it was minted (e.g. mmcp_1a2b3c4). A prefix that
    matches several keys is refused and the candidates are listed.

.PARAMETER Configuration
    Build configuration for the one-shot run. Defaults to Release, because a Memory-MCP server left
    running from `dotnet run` holds a lock on the Debug output and would fail the build.

.EXAMPLE
    ./scripts/revoke-key.ps1 -Key mmcp_1a2b3c4
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Key,

    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $repoRoot "src/MemoryMcp.Api"

[Environment]::SetEnvironmentVariable("Logging__LogLevel__Microsoft.EntityFrameworkCore", "Warning")

Write-Host "Revoking key $Key..." -ForegroundColor Yellow
dotnet run -c $Configuration --project $apiProject -- --revoke-key --key $Key

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
