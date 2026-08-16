# CodeAnalyzer

A Windows desktop tool for reading an unfamiliar codebase. Point it at a folder, tick the
subdirectories you care about, and it indexes every symbol it can find — functions, methods,
types, struct and class members, constants, macros, Verilog modules and ports — then lets you
search them and walk the graph of what calls what.

It works on C, C++, C#, Python, Verilog/SystemVerilog and HTML, in one uniform pipeline.

**Everything it shows is a fact taken from the source.** Where the syntax is not enough to be
certain — a call whose name matches several definitions, a reference into a library that is
not in the workspace — it says so rather than picking one. Ambiguous edges are drawn dashed
and labelled "one of N name matches"; unresolved references are listed as unresolved. Nothing
is inferred, defaulted in, or guessed.

## What it does

- **Fuzzy symbol search** — VS Code style, so `uwr` finds `uart_write`. Filter by kind
  (function, type, interface, constant, …). 12–29 ms per keystroke on a 240,000-symbol index.
- **Neighbourhood graph** — a focus symbol with its callers and callees, expanded on demand
  rather than dumped whole. Nodes carry their parameter list, their modifiers, their declared
  type or constant value, and which overload they are. Right-click to hide things.
- **Composition inspector** — a struct's or class's members with their types and initializers;
  a Verilog module's ports and parameters; what a type extends and what it implements.
- **Path tracer** — every *shortest* route between two symbols, with an explicit distinction
  between "there is no route" and "we stopped looking".
- **Cross-language constant tracing** — a command byte written `0xA5` in the C# that sends
  it, `165` in the C that receives it and `8'hA5` in the RTL that decodes it is one
  agreement spelled three ways, and no reference connects them. Search `=0xA5` to find all
  three; the detail pane lists them under SAME VALUE ELSEWHERE, and the Constants view
  ranks every value that spans a language boundary.
- **Dependency treemap** — folders and files sized by symbol count, coloured by how much of
  their referencing points outward.
- **Import/include wheel** — a chord diagram over top-level directories, from either the
  include/import graph or resolved symbol references.
- **Source preview** with syntax highlighting, jumping to the line of any symbol or call site.
- **Export & sharing** — the graph or paths canvas as PNG, JSON facts, or **Mermaid
  flowchart text** for a PR comment (solid = exact, dashed with the doubt in the label);
  any symbol's facts as a **markdown report** ("copy as LLM context": signature, callers
  and callees with confidence, call sites, I/O boundaries, same-value matches, source
  excerpt); the boundaries view as markdown tables.
- **Live updates** — a file watcher re-indexes and re-resolves as you edit, in tens of
  milliseconds, without touching the rest of the workspace.

## Requirements

- Windows 10 or 11, x64
- [.NET SDK 8.0 or later](https://dotnet.microsoft.com/download) (developed against 9.0.301;
  everything targets `net8.0-windows`)
- [WebView2 Evergreen runtime](https://developer.microsoft.com/microsoft-edge/webview2/) —
  preinstalled on Windows 11

No network access is needed at run time. The graph page is served from a virtual host mapped
to a local folder under a `default-src 'none'` content security policy, and every JavaScript
library it uses is vendored in the repo.

## Build and run

```bash
dotnet build "CodeAnalyzer.sln" -c Release
```

```bash
dotnet run --project src/CodeAnalyzer.App -c Release
```

Then **Ctrl+O** to open a workspace folder. `samples/c-demo` is a small C workspace kept for
exactly that.

### Tests

```bash
dotnet test "CodeAnalyzer.sln"
```

### Throughput benchmark

Generates a synthetic C workspace and times every stage, or runs against a real tree:

```bash
dotnet run --project tools/CodeAnalyzer.Bench -c Release -- --generate 20000 --db
```

```bash
dotnet run --project tools/CodeAnalyzer.Bench -c Release -- "C:\some\repo" --db
```

## Headless use: the `codeanalyzer` CLI and MCP server

The same index answers questions without the GUI. `codeanalyzer`
(`src/CodeAnalyzer.Cli`, built by the solution) opens the workspace's cache through its
own read-only connection, so it runs happily beside an open GUI — and it carries the same
honesty rules: every answer states when the index was built, ambiguous names come back as
a candidate list to pick from rather than a guess, and resolution confidence rides on
every edge (`~` = one of several name matches, `?` = cross-language).

```bash
codeanalyzer index "C:\some\repo"
```

| Command | Answers |
|---|---|
| `search <query>` | fuzzy symbol search (`--kinds fn,type,…`, `--limit N`) |
| `detail <symbol>` | one symbol's fact sheet — signature, members, overloads, unresolved refs |
| `report <symbol>` | the fact sheet as a markdown document — callers/callees with call sites, I/O boundaries, same-value matches, source excerpt |
| `callers <symbol>` / `callees <symbol>` | who references it / what it references (`--sites` adds each call's line and verbatim arguments) |
| `trace <from> <to>` | all shortest routes between two symbols, with "no route" kept distinct from "search budget hit" |
| `map` | repo overview: definitions ranked by distinct incoming references, cut to `--budget` chars |
| `outline <rel_path>` | one file's definitions in source order |
| `boundaries` | where data leaves/enters the workspace (I/O catalog + your marks) |
| `value <literal>` | definitions whose literal denotes this value, in any language (`0xA5` finds `165` and `8'hA5`) |
| `constants` | values defined in more than one language, ranked by how many agree (`--by-dir`, `--include-trivial`) |
| `mcp` | the MCP stdio server for AI agent clients |

A `<symbol>` argument is a name, `Container.Name`, `path/to/file.c:name`, or a `#id` from
a previous result. Every command takes `--root <path>` (default: current directory) and
`--json`. Reads never index implicitly — `index` is the only writer.

For AI agents, the MCP server exposes the same queries as tools
(`search_symbols`, `get_symbol`, `get_context`, `get_callers`, `get_callees`,
`trace_paths`, `repo_map`, `file_outline`, `io_boundaries`, `find_by_value`,
`shared_constants`, `reindex`). This
repo's `.mcp.json` registers it for Claude Code; elsewhere:

```bash
claude mcp add codeanalyzer -- codeanalyzer mcp --root "C:\some\repo"
```

## Layout

```
src/CodeAnalyzer.App/       WPF shell — views, view models, WebView2 host, wwwroot bundle
src/CodeAnalyzer.Core/      Domain, crawling, indexing, storage, resolution, search, graph
                            queries, file watching, session state. No WPF or WebView types.
src/CodeAnalyzer.Parsing/   The only project that references tree-sitter. One analyzer plus
                            a Queries/<language>/{symbols,refs}.scm pack per language.
src/CodeAnalyzer.Cli/       The codeanalyzer exe: subcommands + MCP stdio server over the
                            same cache, read-only.
tests/                      Core.Tests, Parsing.Tests and Cli.Tests
tools/CodeAnalyzer.Bench/   Throughput harness
samples/c-demo/             7-file C workspace for manual end-to-end testing
```

## How it works

**Parsing** is tree-sitter (TreeSitter.DotNet), driven by per-language `.scm` query packs
rather than hand-written parsers, so adding a language is a pack plus a registry entry. A
malformed file still yields a usable partial tree; a syntax error costs the declarations it
sits inside, not the file.

**Indexing** is a channel pipeline — one crawler, one parse worker per core, one database
writer batching into transactions. Re-indexing is incremental: size and timestamp screen
first, a content hash decides second, and only genuinely changed files are re-parsed.

**Storage** is one SQLite database per workspace, under
`%LOCALAPPDATA%\CodeAnalyzer\workspaces\<hash>\index.db`. It is a cache — deleting it costs
one re-index and nothing else, which is why a schema change simply rebuilds.

**Resolution** matches a reference to a definition by tiers, first non-empty winning: same
file → reachable through includes/imports → same language and top-level directory → same
language anywhere → any language (weak). Within the winning tier, a soft argument-count filter
picks the matching overload — soft meaning that if no overload's parameter count matches, every
candidate survives rather than the reference resolving to nothing. One candidate is drawn
solid, several dashed, cross-language dotted.

**Rendering** is WebView2 over a bundled page: Cytoscape.js for the graphs, d3 for the treemap
and wheel. The view models talk to an `IGraphViewService` interface and never see a WebView2
type, so the boundary stays mockable and the UI thread never blocks.

## Known limits

These are stated rather than hidden, because a tool that claims certainty it does not have is
worse than one that admits the gap.

- **Overloads differing only in parameter *type*** cannot be told apart by the resolver, since
  it never checks types. Calls to them stay "one of several name matches".
- **Overloads split across `partial` class files** are not grouped — a partial class is one
  row per file, so the halves look like two containers.
- **Two same-named functions in different C files** are deliberately not treated as an
  overload set. C has no overloading, and calling them one would be an invention.
- The bundled **C# grammar predates collection expressions** (`= []`), which costs the
  declarations containing them.
- The bundled **Verilog grammar** mis-parses a bare subroutine-call statement (`load(1);`).
  The call site is dropped rather than turned into a phantom variable. Calls inside an
  expression are fine.
- **C++ member visibility is not captured.** `public:` is a section label, not per-declaration
  syntax, and deriving each member's visibility from it would be inference.
- **A shared value is evidence, not proof.** Two definitions appear together because their
  literals denote the same number or the same characters — a baud rate and a buffer size
  that are both 9600 are numerically equal and nothing more. On a tree full of vendor
  headers, most matches are coincidences; the useful ones still have to be recognised by
  a reader. Only *defined* values participate: a literal passed straight to a call,
  `Send(0xA5)`, is a reference rather than a declaration.
- **Floats and character literals are deliberately not matched.** Cross-language float
  equality is a claim about representation, and calling `'A'` 65 asserts an encoding the
  source never states.
- **Keyboard shortcuts do not fire while the graph pane holds focus** — a WebView2 hosting
  constraint. Click elsewhere in the shell first.
- The database connection is behind a single lock, so a query and a write cannot overlap.
  Correct and cheap at the sizes measured.

## Performance

Measured on a synthetic corpus of 20,401 files / 241,606 symbols / 120,401 edges, Release,
16 workers:

| Stage | Time |
|---|---|
| Cold index (crawl + parse + write) | 7.7 s (~2,600 files/s) |
| Reference resolution (full) | 1.64 s |
| Live update (one file, incremental) | 59 ms |
| Fuzzy search keystroke | 12–29 ms |
| Graph neighbourhood / detail | 3.9 / 3.0 ms |
| Treemap level (root / drill) | 92 / 97 ms |
| Dependency wheel | 29–39 ms |
| Database size | 81 MB |

A warm reopen is near-instant: the cached index loads and only changed files are re-read.

## Licence

The vendored JavaScript libraries under `src/CodeAnalyzer.App/wwwroot/lib/` keep their own
licences, in `lib/licenses/`.
