<#
    Rebuilds grammars\csharp\lib\tree-sitter-c-sharp.dll from the vendored source.

    You do not need to run this to build or use CodeAnalyzer — the DLL is committed, and
    Directory.Build.targets copies it over the NuGet package's older one. Run it only when
    the vendored grammar source changes.

    Requires a 64-bit gcc. Nothing else: no network, no node, no tree-sitter CLI. That is
    the whole reason src\parser.c is vendored despite being 32 MB of generated C — see
    grammars\csharp\README.md.

    Usage:
      powershell -ExecutionPolicy Bypass -File grammars\build-csharp-grammar.ps1
      powershell -ExecutionPolicy Bypass -File grammars\build-csharp-grammar.ps1 -Gcc "C:\path\to\gcc.exe"
#>
[CmdletBinding()]
param(
    [string] $Gcc
)

$ErrorActionPreference = 'Stop'

$grammarDir = Join-Path $PSScriptRoot 'csharp'
# Upstream source lives under vendor/ so the crawler prunes it: "vendor" is on
# IgnoreRules.IgnoredDirectoryNames, and without that this repo indexes 32 MB of generated
# C as if it were its own — 213 files instead of 208, and three parse errors from headers
# nobody here wrote.
$sourceDir  = Join-Path $grammarDir 'vendor\src'
$outDir     = Join-Path $grammarDir 'lib'
$outDll     = Join-Path $outDir 'tree-sitter-c-sharp.dll'

function Find-Gcc64 {
    # A 32-bit gcc compiles this happily and produces a DLL .NET cannot load at all
    # ("%1 is not a valid Win32 application"), so the target triple is checked, not the
    # presence of the exe. MinGW.org's gcc is i686-only and fails this test.
    $candidates = @()
    if ($Gcc) { $candidates += $Gcc }
    $candidates += (Get-Command gcc -ErrorAction SilentlyContinue | ForEach-Object { $_.Source })
    $candidates += Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages') `
        -Filter gcc.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*mingw64*' } | ForEach-Object { $_.FullName }
    $candidates += 'C:\msys64\mingw64\bin\gcc.exe'

    foreach ($candidate in ($candidates | Where-Object { $_ } | Select-Object -Unique)) {
        if (-not (Test-Path $candidate)) { continue }
        $triple = & $candidate -dumpmachine 2>$null
        if ($triple -like 'x86_64*') {
            return [pscustomobject]@{ Path = $candidate; Triple = $triple }
        }
        Write-Host "  skipping $candidate ($triple is not 64-bit)" -ForegroundColor DarkGray
    }
    return $null
}

Write-Host "Looking for a 64-bit gcc..."
$compiler = Find-Gcc64
if (-not $compiler) {
    Write-Host "No 64-bit gcc found. Install one (e.g. 'winget install BrechtSanders.WinLibs.POSIX.UCRT')" -ForegroundColor Red
    Write-Host "or pass -Gcc <path>. A 32-bit gcc will not do — see Find-Gcc64 above." -ForegroundColor Red
    exit 1
}
Write-Host "  using $($compiler.Path) ($($compiler.Triple))" -ForegroundColor DarkGray

New-Item -ItemType Directory -Force $outDir | Out-Null

# -static so the result carries no libgcc/libwinpthread dependency: the DLL is loaded by
# .NET on machines that have no MinGW at all.
$sources = @(
    (Join-Path $sourceDir 'parser.c'),
    (Join-Path $sourceDir 'scanner.c')
)
Write-Host "Compiling $($sources.Count) sources -> $outDll"
& $compiler.Path -O2 -shared -static -o $outDll @sources -I $sourceDir
if ($LASTEXITCODE -ne 0) {
    Write-Host "Compilation failed ($LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}

$size = [int]((Get-Item $outDll).Length / 1MB)
Write-Host "Built $outDll ($size MB)" -ForegroundColor Green
Write-Host "Rebuild the solution to copy it into every output, then re-index with --full." -ForegroundColor Cyan
