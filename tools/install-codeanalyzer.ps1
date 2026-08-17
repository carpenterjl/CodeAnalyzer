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

# A session on another repo holds this exe open for as long as its MCP server lives. A PID
# alone is not actionable, because the likeliest holder is the session running this script:
# it re-spawns the server whenever it resumes, so "close those sessions" is advice nobody can
# follow. Name the parent instead, and say which one is the reader's own.
$running = @(Get-CimInstance Win32_Process -Filter "Name = 'codeanalyzer.exe'" |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath -ieq $exe })

if ($running.Count -gt 0) {
    # Our own ancestry, so a holder parented anywhere up this chain can be called out as ours.
    $ancestors = @()
    $walk = $PID
    while ($walk -and $ancestors -notcontains $walk -and $ancestors.Count -lt 24) {
        $ancestors += $walk
        $walk = (Get-CimInstance Win32_Process -Filter "ProcessId = $walk").ParentProcessId
    }

    Write-Host "The installed server is running — it cannot be replaced while it is loaded." -ForegroundColor Yellow
    Write-Host ""

    $mine = $false
    foreach ($proc in $running) {
        $parent = $null
        if ($proc.ParentProcessId) {
            $parent = Get-CimInstance Win32_Process -Filter "ProcessId = $($proc.ParentProcessId)"
            # Windows reuses PIDs. A "parent" that started after its child is a different process.
            if ($parent -and $parent.CreationDate -gt $proc.CreationDate) { $parent = $null }
        }

        $owner = if ($parent) { "$($parent.Name) (PID $($parent.ProcessId))" } else { "parent has exited" }

        # Claude Code puts the session it resumed on its own command line — the one detail that
        # tells a reader which of several open sessions is holding the file.
        if ($parent -and $parent.CommandLine -match '--resume[= ]([0-9a-fA-F]{8})') {
            $owner += ", session $($Matches[1])"
        }

        if ($parent -and $ancestors -contains $parent.ProcessId) {
            $mine = $true
            $owner += "  <- THIS session, the one running this script"
        }

        Write-Host "  PID $($proc.ProcessId)  spawned by $owner" -ForegroundColor Yellow
    }

    Write-Host ""
    if ($mine) {
        Write-Host "Closing sessions will not clear your own: it re-spawns the server on resume." -ForegroundColor Yellow
        Write-Host "Stop the server process instead — the next codeanalyzer query starts a fresh" -ForegroundColor Yellow
        Write-Host "one on the new build:" -ForegroundColor Yellow
    }
    else {
        Write-Host "Close those sessions (or disconnect codeanalyzer in each), then run this again." -ForegroundColor Yellow
        Write-Host "Or stop the servers directly — each session starts a fresh one on its next query:" -ForegroundColor Yellow
    }
    Write-Host "  Stop-Process -Id $(($running | ForEach-Object { $_.ProcessId }) -join ', ') -Force"
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
