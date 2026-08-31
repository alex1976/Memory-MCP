<#
.SYNOPSIS
    Mints an API key for an existing Memory-MCP user and grants it access to one or more spaces.

.DESCRIPTION
    A key is a credential *of* a user (create them first with scripts/create-user.ps1) — one key per
    person or per machine of that person, never one shared by a team, so that authorship stays
    meaningful and revoking one credential doesn't rotate everyone's.

    The plaintext key is printed once and never again: only its SHA-256 hash is stored. Send it as the
    X-Api-Key header, or as MEMORYMCP_API_KEY when running the server with --stdio.

    Access is per space and per key: a space this key holds no grant on behaves exactly like a space
    that does not exist. The grant is capped by the owner's role, so a Reader given a ReadWrite grant is
    still read-only there — the printed summary shows the effective level.

    Runs the API project's --create-api-key command, so it reads the connection string exactly the way
    the app does (ConnectionStrings:Default in src/MemoryMcp.Api/appsettings*.json, or the
    ConnectionStrings__Default environment variable).

.PARAMETER Email
    Email of the user the key belongs to. Must already exist.

.PARAMETER Space
    One or more spaces to grant, as '<space-key>' or '<space-key>:Read' / '<space-key>:ReadWrite'
    (ReadWrite is the default). Every space must already exist — the script lists the existing ones if
    a name doesn't match. Omit entirely to mint a key that authenticates but can reach nothing.

.PARAMETER DefaultSpace
    Which granted space to use when a request names none. Defaults to the first -Space given.

.PARAMETER Label
    What this credential is, not who owns it: "laptop", "ci", "claude-desktop". Prompted for when
    omitted, because it is the only thing that tells two of the same person's keys apart once the
    plaintext is gone — press Enter to mint the key without one.

.PARAMETER Configuration
    Build configuration for the one-shot run. Defaults to Release, because a Memory-MCP server left
    running from `dotnet run` holds a lock on the Debug output and would fail the build.

.EXAMPLE
    ./scripts/create-api-key.ps1 -Email alice@example.com -Space default -Label laptop

.EXAMPLE
    ./scripts/create-api-key.ps1 -Email alice@example.com -Space default, team:Read -DefaultSpace team
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Email,

    [string[]]$Space = @(),

    [string]$DefaultSpace,

    [string]$Label,

    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $repoRoot "src/MemoryMcp.Api"

# Asked for rather than left silently empty: once the plaintext key is gone, the label is what tells this
# person's laptop key from their CI key when one of them has to be revoked. A non-interactive session
# (CI, -NonInteractive) has no console to read from, so the prompt fails there rather than hanging —
# fall back to no label, which is what omitting it has always meant.
if (-not $Label) {
    try { $Label = (Read-Host "Label for this key - what it is, not who owns it, e.g. laptop, ci, claude-desktop (Enter for none)").Trim() }
    catch { $Label = "" }

    if (-not $Label) {
        Write-Host "Minting the key without a label." -ForegroundColor DarkGray
    }
}

$commandArgs = @("--create-api-key", "--email", $Email)
foreach ($grant in $Space) { $commandArgs += @("--space", $grant) }
if ($DefaultSpace) { $commandArgs += @("--default-space", $DefaultSpace) }
if ($Label) { $commandArgs += @("--label", $Label) }

if ($Space.Count -eq 0) {
    Write-Warning "No -Space given: the key will authenticate but reach no space. Add -Space <space-key> to grant access."
}

# The plaintext key is the only thing worth reading in this output; EF's Information-level command log
# would bury it under the SELECTs that found the user and the spaces.
[Environment]::SetEnvironmentVariable("Logging__LogLevel__Microsoft.EntityFrameworkCore", "Warning")

Write-Host "Minting an API key for $Email..." -ForegroundColor Yellow
dotnet run -c $Configuration --project $apiProject -- @commandArgs

# The command already explains any failure on stderr; propagate the code without a PowerShell stack trace.
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
