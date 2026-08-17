<#
    Installs the codeanalyzer CLI + MCP server to a machine-wide location so *other*
    Claude Code sessions, on other repositories, can use it.

    Why a second published copy, when tools\publish-mcp-server.ps1 already publishes one?
    Because the two copies answer to different masters. .mcp\server\ is the *development*
    copy: this repo's own .mcp.json launches it, and it is replaced every time you want the
    server to see a change you just made. The installed copy is the *field* copy: sessions
    on unrelated codebases hold it open for hours. If they were the same folder, every
    publish here would fail on a lock held by a session somewhere else, and every refresh
    here would silently change the tool other sessions are dogfooding mid-report.

    Two copies means one rule to remember instead: after a round lands here, re-run this
    script to promote the build into the field.

    Usage:
      powershell -ExecutionPolicy Bypass -File tools\install-codeanalyzer.ps1
      powershell -ExecutionPolicy Bypass -File tools\install-codeanalyzer.ps1 -SkipPath
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'CodeAnalyzer\bin'),
    [switch] $SkipPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project  = Join-Path $repoRoot 'src\CodeAnalyzer.Cli\CodeAnalyzer.Cli.csproj'
$exe      = Join-Path $InstallDir 'codeanalyzer.exe'

# A session on another repo holds this exe open for as long as its MCP server lives. Name
# the offenders rather than letting the copy fail on a lock error nobody can act on.
$running = @(Get-CimInstance Win32_Process -Filter "Name = 'codeanalyzer.exe'" |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath -ieq $exe })

if ($running.Count -gt 0) {
    $ids = ($running | ForEach-Object { $_.ProcessId }) -join ', '
    Write-Host "The installed server is running (PID $ids) — a session is using it." -ForegroundColor Yellow
    Write-Host "Close those sessions (or disconnect codeanalyzer in each), then run this again." -ForegroundColor Yellow
    exit 1
}

Write-Host "Publishing $Configuration -> $InstallDir"
& dotnet publish $project -c $Configuration -o $InstallDir --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed ($LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}

if (-not $SkipPath) {
    # User PATH only — never the machine one, which needs elevation and is not ours to edit.
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $entries  = @($userPath -split ';' | Where-Object { $_ })
    if ($entries -notcontains $InstallDir) {
        [Environment]::SetEnvironmentVariable('Path', (($entries + $InstallDir) -join ';'), 'User')
        Write-Host "Added $InstallDir to your user PATH (new shells only)." -ForegroundColor Green
    }
    else {
        Write-Host "$InstallDir is already on your user PATH." -ForegroundColor DarkGray
    }
}

Write-Host ""
# Smoke test: `cache` is the one read that needs no workspace index, so it proves the exe
# starts and finds its native tree-sitter DLLs without depending on anything being indexed.
& $exe cache --quiet | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Installed, but `"$exe cache`" exited $LASTEXITCODE — check the copy." -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host "Installed: $exe" -ForegroundColor Green
Write-Host ""
Write-Host "Register it for every Claude Code session (once per machine):" -ForegroundColor Cyan
Write-Host "  claude mcp add codeanalyzer --scope user -- `"$exe`" mcp"
Write-Host ""
Write-Host "No --root: the server defaults to the session's working directory, so one" -ForegroundColor DarkGray
Write-Host "registration serves every repository you open." -ForegroundColor DarkGray
Write-Host ""
Write-Host "Scope precedence is local > user > project — measured, not assumed. A user-scope" -ForegroundColor DarkGray
Write-Host "registration therefore SHADOWS this repo's .mcp.json. To keep developing against" -ForegroundColor DarkGray
Write-Host "the .mcp\server copy, register that one locally, which outranks both:" -ForegroundColor DarkGray
Write-Host "  claude mcp add codeanalyzer --scope local -- `"$repoRoot\.mcp\server\codeanalyzer.exe`" mcp --root ."
