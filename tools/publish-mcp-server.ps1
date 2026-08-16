<#
    Publishes the codeanalyzer CLI to .mcp\server\, which is where .mcp.json launches the
    MCP stdio server from.

    Why not launch it straight out of bin\Release? Because a live MCP server holds its own
    binaries open, and everything in bin\ is a build output: `dotnet build` of the solution
    then fails with MSB3021 ("the process cannot access the file"). Pointing an agent at
    this repo with this repo's own MCP server attached is exactly that case — you cannot
    rebuild the tool while the tool is watching. A published copy is not a build output, so
    a running server and a rebuild never meet.

    Re-run this after any change you want the server to see, then reconnect the server in
    your client. Building the solution does NOT refresh it — that is the point.

    Usage:  powershell -ExecutionPolicy Bypass -File tools\publish-mcp-server.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project  = Join-Path $repoRoot 'src\CodeAnalyzer.Cli\CodeAnalyzer.Cli.csproj'
$outDir   = Join-Path $repoRoot '.mcp\server'
$exe      = Join-Path $outDir 'codeanalyzer.exe'

# A server started from the previous publish holds these very files open. Name it rather
# than letting the copy fail with a lock error nobody can act on.
$running = @(Get-CimInstance Win32_Process -Filter "Name = 'codeanalyzer.exe'" |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath -ieq $exe })

if ($running.Count -gt 0) {
    $ids = ($running | ForEach-Object { $_.ProcessId }) -join ', '
    Write-Host "The published server is already running (PID $ids)." -ForegroundColor Yellow
    Write-Host "Disconnect it first, then run this again:" -ForegroundColor Yellow
    Write-Host "  in Claude Code, /mcp -> codeanalyzer -> disconnect (or close the client)" -ForegroundColor Yellow
    exit 1
}

Write-Host "Publishing $Configuration -> $outDir"
& dotnet publish $project -c $Configuration -o $outDir --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed ($LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Published. .mcp.json launches: .mcp/server/codeanalyzer.exe mcp --root ." -ForegroundColor Green
Write-Host "Reconnect the server in your client to pick this build up." -ForegroundColor Green
