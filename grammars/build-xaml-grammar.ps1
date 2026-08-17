<#
    Rebuilds grammars\xaml\lib\tree-sitter-xaml.dll from the vendored source.

    This is tree-sitter-html compiled a second time under a different name, with one
    behaviour switched off: see grammars\xaml\README.md for what and why. The DLL is
    committed, so you do not need to run this to build or use CodeAnalyzer.

    Requires a 64-bit gcc and nothing else — no network, no node, no tree-sitter CLI,
    because parser.c is vendored generated C rather than regenerated here.

    Usage:
      powershell -ExecutionPolicy Bypass -File grammars\build-xaml-grammar.ps1
      powershell -ExecutionPolicy Bypass -File grammars\build-xaml-grammar.ps1 -Gcc "C:\path\to\gcc.exe"
#>
[CmdletBinding()]
param(
    [string] $Gcc
)

$ErrorActionPreference = 'Stop'

$grammarDir = Join-Path $PSScriptRoot 'xaml'
# Under vendor/ so the crawler prunes it — "vendor" is on IgnoreRules.IgnoredDirectoryNames.
# Without that this repo indexes the grammar it ships as if it were its own source.
$sourceDir  = Join-Path $grammarDir 'vendor\src'
$outDir     = Join-Path $grammarDir 'lib'
$outDll     = Join-Path $outDir 'tree-sitter-xaml.dll'

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

# TreeSitter.DotNet's Language(id) resolves "tree-sitter-<id>.dll" and calls
# "tree_sitter_<id>", so the exports have to say xaml. parser.c is byte-identical to
# upstream and is renamed here rather than edited — the preprocessor does not substitute
# inside a longer identifier, so each external-scanner entry point is named in full.
$renames = @(
    'tree_sitter_html=tree_sitter_xaml'
    'tree_sitter_html_external_scanner_create=tree_sitter_xaml_external_scanner_create'
    'tree_sitter_html_external_scanner_destroy=tree_sitter_xaml_external_scanner_destroy'
    'tree_sitter_html_external_scanner_scan=tree_sitter_xaml_external_scanner_scan'
    'tree_sitter_html_external_scanner_serialize=tree_sitter_xaml_external_scanner_serialize'
    'tree_sitter_html_external_scanner_deserialize=tree_sitter_xaml_external_scanner_deserialize'
) | ForEach-Object { "-D$_" }

# TREE_SITTER_XAML is what tag.h's single documented change hangs off; without it this
# build would be a byte-for-byte second copy of the HTML grammar.
$sources = @(
    (Join-Path $sourceDir 'parser.c'),
    (Join-Path $sourceDir 'scanner.c')
)
Write-Host "Compiling $($sources.Count) sources -> $outDll"
& $compiler.Path -O2 -shared -static -DTREE_SITTER_XAML @renames -o $outDll @sources -I $sourceDir
if ($LASTEXITCODE -ne 0) {
    Write-Host "Compilation failed ($LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}

$size = [math]::Round((Get-Item $outDll).Length / 1KB)
Write-Host "Built $outDll ($size KB)" -ForegroundColor Green
Write-Host "Rebuild the solution to copy it into every output, then re-index with --full." -ForegroundColor Cyan
