<#
.SYNOPSIS
    Keeps the Memory-MCP HTTP server running in the background, restarting it if it ever exits.

.DESCRIPTION
    Meant to be launched by the "MemoryMcpServer" Windows Scheduled Task at user logon, so the
    server at http://localhost:5004/mcp is always available without manually running
    `dotnet run --project src/MemoryMcp.Api` first. Uses the Development launch profile (same as a
    manual `dotnet run`), so it picks up appsettings.Development.json for the connection string and
    provider keys. Output is appended to .logs/memory-mcp-server.log for troubleshooting.
#>
[CmdletBinding()]
param()

$repoRoot = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $repoRoot "src/MemoryMcp.Api"
$logDir = Join-Path $repoRoot ".logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logFile = Join-Path $logDir "memory-mcp-server.log"

while ($true) {
    "$(Get-Date -Format o) starting: dotnet run --project $apiProject" | Out-File -FilePath $logFile -Append -Encoding utf8
    dotnet run --project $apiProject *>> $logFile
    "$(Get-Date -Format o) process exited with code $LASTEXITCODE, restarting in 5s" | Out-File -FilePath $logFile -Append -Encoding utf8
    Start-Sleep -Seconds 5
}
