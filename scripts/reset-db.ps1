<#
.SYNOPSIS
    Drops and recreates the Memory-MCP database, re-applying all EF Core migrations from scratch.

.DESCRIPTION
    DESTRUCTIVE: permanently deletes every space, API key, document, and memory currently in the
    database. Uses `dotnet ef database drop` + `dotnet ef database update`, so it reads the
    connection string exactly the way the app does (ConnectionStrings:Default in
    src/MemoryMcp.Api/appsettings*.json, or the ConnectionStrings__Default environment variable) —
    nothing is hardcoded here, so it works against whichever database your current config points at.

.PARAMETER Seed
    Also seed a fresh 'default' space + ReadWrite API key afterwards (via `dotnet run -- --seed`),
    printing the new plaintext API key once. Default: enabled.

.PARAMETER Force
    Skip the interactive confirmation prompt (e.g. for non-interactive use).

.EXAMPLE
    ./scripts/reset-db.ps1

.EXAMPLE
    ./scripts/reset-db.ps1 -Force -Seed:$false
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [bool]$Seed = $true
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$infraProject = Join-Path $repoRoot "src/MemoryMcp.Infrastructure"
$apiProject = Join-Path $repoRoot "src/MemoryMcp.Api"

if (-not $Force) {
    Write-Warning "This will PERMANENTLY DELETE the entire Memory-MCP database (all spaces, API keys, documents, memories)."
    $confirmation = Read-Host "Type 'yes' to continue"
    if ($confirmation -ne "yes") {
        Write-Host "Aborted, nothing was changed."
        exit 1
    }
}

Write-Host "Dropping database..." -ForegroundColor Yellow
dotnet ef database drop --force --project $infraProject --startup-project $apiProject
if ($LASTEXITCODE -ne 0) { throw "dotnet ef database drop failed (exit code $LASTEXITCODE)." }

Write-Host "Recreating database and applying migrations..." -ForegroundColor Yellow
dotnet ef database update --project $infraProject --startup-project $apiProject
if ($LASTEXITCODE -ne 0) { throw "dotnet ef database update failed (exit code $LASTEXITCODE)." }

if ($Seed) {
    Write-Host "Seeding a fresh 'default' space and API key..." -ForegroundColor Yellow
    dotnet run --project $apiProject -- --seed
    if ($LASTEXITCODE -ne 0) { throw "Seeding failed (exit code $LASTEXITCODE)." }
}

Write-Host "Done. Database reset from scratch." -ForegroundColor Green
