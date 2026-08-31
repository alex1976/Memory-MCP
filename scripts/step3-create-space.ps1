<#
.SYNOPSIS
    Creates a Memory-MCP space and grants existing API keys access to it.

.DESCRIPTION
    A space is the unit of isolation: no read or search ever crosses a space boundary, and a space a key
    holds no grant on behaves exactly like a space that does not exist. Creating one is therefore only
    half the job — until at least one key holds a grant, nobody can reach it. This script does both.

    Grants are per key, not per user, so -GrantTo accepts either. An email fans out to every credential
    that person holds (laptop, CI, agent), which is what onboarding someone to a space usually means; a
    key id or the prefix printed at mint time targets exactly one credential.

    The grant is capped by the owner's role: a Reader granted ReadWrite is still read-only there. The
    printed summary shows the effective level, so a capped grant is visible now rather than as a
    puzzling denial later.

    Runs the API project's --create-space command, so it reads the connection string exactly the way the
    app does (ConnectionStrings:Default in src/MemoryMcp.Api/appsettings*.json, or the
    ConnectionStrings__Default environment variable) — nothing is hardcoded here.

.PARAMETER Key
    The space key: the stable identifier tools take as containerTag. Unique — re-running for an existing
    key fails unless -AllowExisting is given.

.PARAMETER Name
    Human-readable name shown by listSpaces and the space picker. Prompted for when omitted, because it
    is what teammates read in the picker and a key like 'q3-launch' is a poor label for it. Press Enter
    at the prompt to fall back to the key. Not asked for with -AllowExisting, which leaves the existing
    space's name untouched.

.PARAMETER Description
    What the space is for. Optional, and only stored on creation.

.PARAMETER GrantTo
    One or more keys to grant, as '<target>' or '<target>:Read' / '<target>:ReadWrite' (ReadWrite is the
    default). A target is an owner's email (grants every key that person holds), an API key id (GUID), or
    the key prefix printed when it was minted. Omit to create the space with no grants.

.PARAMETER MakeDefault
    Make this space the default for every key granted — the one used when a request names no space. Each
    key can only have one default, so the previous one is cleared. Without this, the space still becomes
    the default for a key that has no other grant, since such a key would otherwise have no active space.

.PARAMETER AllowExisting
    Reuse the space if it already exists instead of failing, applying only the grants. This is how an
    existing space is opened to one more credential; -Name and -Description are ignored in that case.

.PARAMETER Configuration
    Build configuration for the one-shot run. Defaults to Release, because a Memory-MCP server left
    running from `dotnet run` holds a lock on the Debug output and would fail the build.

.EXAMPLE
    ./scripts/create-space.ps1 -Key engineering -Name "Engineering launch" -GrantTo alice@example.com

.EXAMPLE
    ./scripts/create-space.ps1 -Key legal -GrantTo alice@example.com, bob@example.com:Read -MakeDefault

.EXAMPLE
    ./scripts/create-space.ps1 -Key engineering -AllowExisting -GrantTo mmcp_1a2b3c4
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Key,

    [string]$Name,

    [string]$Description,

    [string[]]$GrantTo = @(),

    [switch]$MakeDefault,

    [switch]$AllowExisting,

    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $repoRoot "src/MemoryMcp.Api"

# Asked for rather than silently defaulted to the key: the name is what teammates pick from in the space
# selector, and it is only settable at creation. Skipped with -AllowExisting, which leaves the existing
# space's name untouched. A non-interactive session (CI, -NonInteractive) has no console to read from, so
# the prompt fails there rather than hanging — fall back to the key instead of aborting.
if (-not $Name -and -not $AllowExisting) {
    try { $Name = (Read-Host "Name for space '$Key' (Enter to use '$Key')").Trim() }
    catch { $Name = "" }

    if (-not $Name) {
        $Name = $Key
        Write-Host "Using '$Key' as the space name." -ForegroundColor DarkGray
    }
}

$commandArgs = @("--create-space", "--key", $Key)
if ($Name) { $commandArgs += @("--name", $Name) }
if ($Description) { $commandArgs += @("--description", $Description) }
foreach ($grant in $GrantTo) { $commandArgs += @("--grant", $grant) }
if ($MakeDefault) { $commandArgs += "--make-default" }
if ($AllowExisting) { $commandArgs += "--allow-existing" }

if ($GrantTo.Count -eq 0) {
    Write-Warning "No -GrantTo given: the space will exist but no key can reach it. Add -GrantTo <email> to grant access."
}

# EF's Information-level command log would bury the space id and the grant summary under the SELECTs
# that resolved the owners and their keys.
[Environment]::SetEnvironmentVariable("Logging__LogLevel__Microsoft.EntityFrameworkCore", "Warning")

$what = if ($AllowExisting) { "Granting space $Key" } else { "Creating space $Key" }
Write-Host "$what..." -ForegroundColor Yellow
dotnet run -c $Configuration --project $apiProject -- @commandArgs

# The command already explains any failure on stderr; propagate the code without a PowerShell stack trace.
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
