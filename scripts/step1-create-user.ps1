<#
.SYNOPSIS
    Creates a new Memory-MCP user (a person) in the database.

.DESCRIPTION
    A user is a person, not a credential: this creates the account and its role, and nothing that can
    authenticate yet. Mint a credential for them afterwards with scripts/create-api-key.ps1 — one
    person may hold several keys (laptop, CI, agent).

    The role is a ceiling that applies in every space: a Writer may read and write wherever they hold a
    ReadWrite grant, a Reader may only read, whatever their grants say. Emails are normalized to lower
    case and are unique, so re-running this for an existing address fails rather than creating a second
    account for the same person.

    Runs the API project's --create-user command, so it reads the connection string exactly the way the
    app does (ConnectionStrings:Default in src/MemoryMcp.Api/appsettings*.json, or the
    ConnectionStrings__Default environment variable) — nothing is hardcoded here.

.PARAMETER Email
    The person's email address. Unique, normalized to lower case, and how create-api-key.ps1 finds them.
    Prompted for when omitted; it has no sensible default, so an empty answer is an error.

.PARAMETER Name
    Display name shown as the author on everything they write. Prompted for when omitted — press Enter
    to fall back to the email address.

.PARAMETER Role
    Writer (read and write) or Reader (read and search only, everywhere). Prompted for when omitted —
    press Enter for Writer.

.PARAMETER Configuration
    Build configuration for the one-shot run. Defaults to Release, because a Memory-MCP server left
    running from `dotnet run` holds a lock on the Debug output and would fail the build.

.EXAMPLE
    ./scripts/create-user.ps1 -Email alice@example.com -Name "Alice Rossi"

.EXAMPLE
    ./scripts/create-user.ps1 -Email bot@example.com -Name "Reporting Bot" -Role Reader

.EXAMPLE
    ./scripts/create-user.ps1
    # Asks for email, display name and role in turn.
#>
[CmdletBinding()]
param(
    [string]$Email,

    [string]$Name,

    [ValidateSet("Writer", "Reader")]
    [string]$Role,

    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

# Only what was not passed is asked for, so scripted callers are unaffected. A non-interactive session
# (CI, -NonInteractive) has no console to read from: the prompt fails rather than hanging, so this
# returns empty and each caller below decides whether that means a default or an error.
function Read-Answer([string]$Prompt) {
    try { return (Read-Host $Prompt).Trim() }
    catch { return "" }
}

if (-not $Email) {
    $Email = Read-Answer "Email of the user to create"

    if (-not $Email) {
        # The one value with no fallback: it is the account's unique identity and how create-api-key.ps1
        # finds them, so guessing one would create the wrong person. Written straight to stderr because
        # Write-Error under $ErrorActionPreference = "Stop" would bury it in a PowerShell stack trace.
        [Console]::Error.WriteLine("An email is required. Pass -Email, or answer the prompt in an interactive session.")
        exit 1
    }
}

if (-not $Name) {
    $Name = Read-Answer "Display name for $Email (Enter to use the email)"

    if (-not $Name) {
        Write-Host "Using $Email as the display name." -ForegroundColor DarkGray
    }
}

if (-not $Role) {
    while (-not $Role) {
        $answer = Read-Answer "Role for $Email - Writer (read and write) or Reader (read only) [Writer]"

        if (-not $answer) { $Role = "Writer" }
        elseif ($answer -eq "Writer") { $Role = "Writer" }
        elseif ($answer -eq "Reader") { $Role = "Reader" }
        else { Write-Host "Answer Writer or Reader, or press Enter for Writer." -ForegroundColor DarkGray }
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $repoRoot "src/MemoryMcp.Api"

$commandArgs = @("--create-user", "--email", $Email, "--role", $Role)
if ($Name) { $commandArgs += @("--name", $Name) }

# EF's Information-level command log would bury the created user's id under the SELECT that checked the
# email was free.
[Environment]::SetEnvironmentVariable("Logging__LogLevel__Microsoft.EntityFrameworkCore", "Warning")

Write-Host "Creating user $Email ($Role)..." -ForegroundColor Yellow
dotnet run -c $Configuration --project $apiProject -- @commandArgs

# The command already explains any failure on stderr; propagate the code without a PowerShell stack trace.
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
