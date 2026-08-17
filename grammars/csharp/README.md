# tree-sitter-c-sharp, vendored

The bundled grammar in TreeSitter.DotNet cannot parse a C# 12 **empty collection
expression** (`= []`). That is not a cosmetic gap. A parse error costs the declaration it
sits inside, so a field written

```csharp
private static readonly OptionSpec Sort = new(name, Flags: ["rows"], Names: [], Positionals: 2);
```

is dropped, and with it every reference in its initialiser. The 2026-08-17 JGraph field
report caught the consequence: `get_callers` on `OptionSpec` reported **22** referencing
methods when **41** files use it, because **39 of those 41 files were truncated at exactly
this construct** — and nothing in the answer said the count was partial.

TreeSitter.DotNet 1.3.0 is the newest published version, so there was no upgrade to take.

## What is here

| | |
|---|---|
| **Upstream** | [tree-sitter/tree-sitter-c-sharp](https://github.com/tree-sitter/tree-sitter-c-sharp) |
| **Version** | 0.23.5, commit `9150f7d56bb47f1a809fa23623f1ba1413e93fa9` |
| **Licence** | MIT — see [vendor/LICENSE](vendor/LICENSE). Kept beside the source, as with the vendored JavaScript libraries. |
| **Language ABI** | 15, the same as every other grammar in the bundle, so the packaged `tree-sitter.dll` runtime loads it unchanged |

```
vendor/grammar.js          the hand-written source, for reading
vendor/src/parser.c        32 MB of generated C — see "Why parser.c is committed"
vendor/src/scanner.c       the external scanner (hand-written)
vendor/src/grammar.json    generated
vendor/src/node-types.json generated; the .scm pack is written against these node names
vendor/src/tree_sitter/*.h headers parser.c and scanner.c include
vendor/LICENSE             MIT, kept beside the code it covers
lib/*.dll                  the built artifact, which is what actually gets loaded
```

**The upstream tree sits under `vendor/` for a reason the crawler enforces.** `vendor` is on
`IgnoreRules.IgnoredDirectoryNames`, so this repo does not index its own vendored grammar.
Without it, the first full re-index after adding these files read **213 files instead of 208**
and reported three parse errors from `#ifdef __cplusplus` in headers nobody here wrote — the
tool measuring 32 MB of generated C as though it were part of the project it is analysing.

## Why `parser.c` is committed despite being 32 MB

Because rebuilding must need **only a compiler**. The alternative — commit `grammar.js` and
regenerate — makes every rebuild depend on node, npm, the `tree-sitter` CLI and the network,
which is a heavier dependency than the file is large. It is the same reasoning that keeps
Cytoscape and d3 vendored under `wwwroot/lib/`: a thing the build cannot proceed without is
source, whether or not a generator produced it.

## Rebuilding

```bash
powershell -ExecutionPolicy Bypass -File grammars\build-csharp-grammar.ps1
```

You do not need to run this to build or use CodeAnalyzer — `lib/tree-sitter-c-sharp.dll` is
committed, and `Directory.Build.targets` copies it over the package's copy in every build and
publish output.

It needs a **64-bit** gcc. A 32-bit one compiles this without complaint and produces a DLL
.NET cannot load at all (`%1 is not a valid Win32 application`), so the script checks
`-dumpmachine` rather than merely finding `gcc` on PATH. `winget install
BrechtSanders.WinLibs.POSIX.UCRT` provides a suitable one.

After rebuilding, re-index with `--full`: a refresh skips files whose content is unchanged,
and every one of these files is unchanged — the *analyzer* is what moved.

## What changed in the trees

Verified before adopting, against the bundled grammar on the same inputs: ordinary methods,
raw string literals and primary constructors produce **identical** s-expressions. Empty
collection expressions parse cleanly instead of erroring. Non-empty `[1, 2, 3]`, which the
old grammar already accepted, now parses as a `collection_expression` rather than what the
older grammar made of it — a different tree, though both are error-free, and the `.scm` pack
queries neither.
