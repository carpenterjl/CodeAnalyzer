# CodeAnalyzer

A Windows desktop tool for reading an unfamiliar codebase. Point it at a folder, tick the
subdirectories you care about, and it indexes every symbol it can find — functions, methods,
types, struct and class members, constants, macros, Verilog modules and ports — then lets you
search them and walk the graph of what calls what.

It works on C, C++, C#, Python, JavaScript, Verilog/SystemVerilog, HTML and XAML, in one uniform
pipeline.

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
honesty rules: every answer states when the index was built **and how many indexed files
have changed on disk since** ("3 of 177 indexed files changed on disk"), ambiguous names
come back as a candidate list to pick from rather than a guess, and resolution confidence
rides on every edge (`~` = one of several name matches, `?` = cross-language).

```bash
codeanalyzer index "C:\some\repo"
```

A refresh skips files whose size, timestamp and content hash are unchanged. That is right
about the only thing it can see — the file — and wrong whenever the *analyzer* changed
underneath it: edit a query pack and an untouched file is still read the old way, because
nothing about the file says otherwise. `index --full` re-parses everything and is the
answer while you are working on a pack. (Reference resolution needs no such flag: every
run rebuilds every edge from scratch, so a resolver change lands in full either way.)

| Command | Answers |
|---|---|
| `search <query>` | fuzzy symbol search (`--kinds fn,type,…`, `--limit N`, `--exact` for verbatim containment instead of subsequence) |
| `detail <symbol>` | one symbol's fact sheet — signature, members, overloads, unresolved refs |
| `report <symbol>` | the fact sheet as a markdown document — callers/callees with call sites, I/O boundaries, same-value matches, source excerpt |
| `callers <symbol>` / `callees <symbol>` | who references it / what it references (`--sites` adds each call's line and verbatim arguments) |
| `trace <from> <to>` | all shortest routes between two symbols, with "no route" kept distinct from "search budget hit" |
| `map` | repo overview: definitions ranked by distinct incoming references, cut to `--budget` chars |
| `outline <rel_path>` | one file's definitions in source order |
| `boundaries` | where data leaves/enters the workspace (I/O catalog + your marks) |
| `value <literal>` | definitions whose literal denotes this value, in any language (`0xA5` finds `165` and `8'hA5`) |
| `constants` | values defined in more than one language, ranked by how many agree (`--by-dir`, `--include-trivial`) |
| `errors` | files the parser could not fully read, the construct it stopped at, and a tally of the most common ones |
| `stats` | aggregate facts about the index: files by language, symbols by kind, and how well resolution is doing |
| `cache` | every workspace cache on this machine, live or stale; `--prune` deletes the stale ones |
| `index [path]` | build or refresh the workspace's index (`--full` re-parses every file instead of skipping unchanged ones) |
| `mcp` | the MCP stdio server for AI agent clients |

A `<symbol>` argument is a name, `Container.Name`, `path/to/file.c:name`, or a `#id` from
a previous result. A name shared only by a type and the members that type declares — a class
and its own constructor — resolves to the type rather than being reported as an ambiguity;
two same-named types anywhere still are one. Every command takes `--root <path>` (default:
current directory), `--json`, and `--quiet`. Reads never index implicitly — `index` is the
only writer.

Every command prints a one-line provenance header naming the index, its size, how much of it
the parser could not fully read, when it was built and how far it has drifted since. The
imperfect-parse count is there because every count this tool prints is drawn from what
parsed, and `get_callers` — whose entire answer is a count — used to say nothing about it
while `stats` and `errors` both did. The advice to re-index appears only when files have
actually moved; "nothing has changed" is not a reason to rebuild.

It goes to **stdout at a terminal** and to **stderr
when stdout is redirected or `--json` was asked for**, so a pipe only ever receives the
answer while a human never sees a successful query painted as an error by a shell that
colours stderr red. `--quiet` drops it entirely — and on `index`, drops the progress lines
while keeping the summary.

For AI agents, the MCP server exposes the same queries as tools
(`search_symbols`, `get_symbol`, `get_context`, `get_callers`, `get_callees`,
`trace_paths`, `repo_map`, `file_outline`, `io_boundaries`, `find_by_value`,
`shared_constants`, `parse_errors`, `stats`, `reindex`). This
repo's `.mcp.json` registers it for Claude Code.

### Using it on every other repository

Install a copy outside this tree and register it once, for all projects:

```bash
powershell -ExecutionPolicy Bypass -File tools\install-codeanalyzer.ps1
```

```bash
claude mcp add codeanalyzer --scope user -- "%LOCALAPPDATA%\CodeAnalyzer\bin\codeanalyzer.exe" mcp
```

No `--root`: it defaults to the working directory, and a client launches an stdio server in
the project directory, so one registration serves every repository you open. Nothing is
written into those repositories — the index lives under `%LOCALAPPDATA%\CodeAnalyzer\`, keyed
by a hash of the workspace path. A repo with no cache yet is not an error: every tool answers
with the way out, and `reindex` builds it.

**Scope precedence is `local` > `user` > `project`** — measured against the client, not assumed,
because the natural guess is wrong. A user-scope registration therefore *shadows* this repo's
`.mcp.json`. That matters here specifically: developing the tool means the server must launch
from `.mcp\server\`, so register that one at local scope, which outranks both:

```bash
claude mcp add codeanalyzer --scope local -- "<repo>\.mcp\server\codeanalyzer.exe" mcp --root .
```

The two published copies answer to different masters and that is the point. `.mcp\server\` is
the development copy, replaced whenever you want the server to see a change you just made. The
installed copy is the field copy, held open for hours by sessions on unrelated codebases. One
folder for both means every publish here fails on a lock held elsewhere, and every refresh here
silently changes the tool another session is mid-report on. Re-run `install-codeanalyzer.ps1`
to promote a landed build into the field.

Reports from those sessions collect in [`dogfood/`](dogfood/), which exists because twelve
rounds of this tool were ranked from a single corpus — this repository.

**Point the server at a published copy, never at `bin/`.** A running server holds its own
binaries open, so if it launches out of a build directory you cannot rebuild the project
that produced it — `dotnet build` fails with MSB3021 while the server is alive. That bites
hardest in exactly the case this tool is for: an agent reading a codebase it is also
editing. This repo's `.mcp.json` therefore launches `.mcp/server/codeanalyzer.exe`, a
published copy that no build writes to:

```bash
powershell -ExecutionPolicy Bypass -File tools\publish-mcp-server.ps1
```

Re-run that when you want the server to see your latest changes — building the solution
deliberately does not refresh it — then reconnect the server in your client.

## Checking WPF bindings

WPF reports a broken binding path as a runtime trace line and carries on, so a typo in a
`{Binding}` or a `Click` handler is invisible until somebody clicks the thing:

```bash
dotnet run --project tools/CodeAnalyzer.BindingCheck -c Release
```

It reflects over the **compiled** app assembly, which is the only way to see the bindable
surface: `[ObservableProperty] private string _query` becomes `Query` and
`[RelayCommand] Search()` becomes `SearchCommand`, both at build time, so a checker reading
the source would report every one of them missing.

The two halves have different strengths and the output says so. **Handlers are checked
soundly** — `x:Class` names exactly one type, and it either has the method or it does not.
**Bindings are checked against every view model and item type at once** and pass if any
carries the path: that catches the error worth catching, a name that exists nowhere, without
false-positiving on row templates whose real data context the file never names. It will not
catch a path that is valid on some other type than the one actually in play. Anything it
cannot read — a binding with an explicit `Source`, an indexer — is counted and reported as
unchecked, never as passed.

`--selftest` runs it against `tools/CodeAnalyzer.BindingCheck/selftest/Broken.xaml`, which
pairs four correct paths with four misspellings of the same paths, and passes only if
exactly the four wrong ones come back. A checker that has only ever printed "all resolved"
is indistinguishable from one that cannot find anything.

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
tools/CodeAnalyzer.BindingCheck/  Resolves every XAML {Binding} and event handler against
                            the compiled app, with a deliberately-wrong selftest fixture
samples/c-demo/             7-file C workspace for manual end-to-end testing
dogfood/                    Field reports from sessions using the tool on other codebases,
                            plus the template and the rules a report is held to
```

## How it works

**Parsing** is tree-sitter (TreeSitter.DotNet), driven by per-language `.scm` query packs
rather than hand-written parsers, so adding a language is a pack plus a registry entry. A
malformed file still yields a usable partial tree; a syntax error costs the declarations it
sits inside, not the file.

A file the parser stumbled on is usually not a broken file. `codeanalyzer errors` lists
them with the line and the construct, and leads with a tally, because the tally is normally
the whole story. For five rounds that tally read `57 × C# []` — every flagged C# file held
a C# 12 collection expression, which the bundled grammar predated. All of them compiled,
and all of them indexed: the cost was measured at **zero symbols**, on this repo and on the
shape a field report blamed for an undercount alike.

Zero symbols is not zero cost. A tool that reports 61 imperfect parses over a workspace
that compiles cleanly teaches its reader to distrust every count it prints, and the
2026-08-17 JGraph report shows where that ends — a correct observation (39 of the 41 files
using a type were flagged) reasoned into a cause that was not there. So the grammar was
replaced rather than explained again: [`grammars/csharp`](grammars/csharp) builds a current
tree-sitter-c-sharp from vendored source, and the tally is now `4 × XAML property element`.
Those four are `<Grid.RowDefinitions>` and friends, which the borrowed HTML grammar reads
as a tag plus an error, and they are not a defect in the source either.

A run also compares the index's **links per file** against the previous run's and says so
when it multiplies, naming the files that account for it. The measure is deliberately not
elapsed time: this machine routinely runs every stage 2–4× slow under unrelated load, so a
clock-based alarm would fire on a busy afternoon and be trained away long before it ever
caught anything. Density says the same thing loaded or quiet, and moves for one reason —
something got indexed whose names a caller list cannot mean anything about. A workspace that
honestly doubles in size moves both numbers and stays silent.

**Indexing** is a channel pipeline — one crawler, one parse worker per core, one database
writer batching into transactions. Re-indexing is incremental: size and timestamp screen
first, a content hash decides second, and only genuinely changed files are re-parsed.

**Storage** is one SQLite database per workspace, under
`%LOCALAPPDATA%\CodeAnalyzer\workspaces\<hash>\index.db`. It is a cache — deleting it costs
one re-index and nothing else, which is why a schema change simply rebuilds.

Set `CODEANALYZER_CACHE_ROOT` to put that tree somewhere else — a different drive, or a
scratch directory for a test run. Caches are never removed automatically, so a machine that
has indexed many short-lived directories accumulates them; each one records the workspace it
was built from in `meta.root_path`, which is what makes a stale one identifiable.
`codeanalyzer cache` lists them all, judged live or stale by that recorded origin, and
`cache --prune` deletes the stale ones — never the live or unjudgeable ones.

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
- **Two grammars are built here rather than taken from the package.**
  [`grammars/csharp`](grammars/csharp) is a current tree-sitter-c-sharp, because
  TreeSitter.DotNet's copy predates C# 12 collection expressions and 1.3.0 is the newest
  published version. [`grammars/xaml`](grammars/xaml) is tree-sitter-html compiled again with
  its tag table switched off, because `<Style>` is a raw-text element in HTML and XAML puts
  markup inside one. `Directory.Build.targets` copies both into every output. If a build falls
  back to the packaged copies, `AnEmptyCollectionExpressionIsReadWithoutComplaint` and
  `ADeclarationInsideAStyleElementSurvives` are what fail.
- **A C# local named `required` cannot be a receiver.** C# 11 made the word a member modifier
  and the vendored grammar reads it as one wherever it appears, so `required.AddRange(…)` lands
  in `errors` as an imperfect parse. Measured against seven contextual keywords — `file`,
  `scoped`, `record`, `init`, `dynamic` and `nint` are all fine, and `required` is not. Rename
  the local; it is not worth patching a grammar over.
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
- **JavaScript declarations state no types**, so nothing here does. An object literal is the
  language's record, namespace and configuration blob at once and the syntax cannot tell
  those apart, so a function-valued key is recorded as a method and every other key as a
  field with its verbatim value; keys written as strings are not captured at all. An import
  written without its extension (`from "./util"`) names no file the index can find and is
  left unresolved rather than having `.js` guessed onto it.
- **Minified bundles are not indexed.** A file named `*.min.js` is skipped: minification
  rewrites every local name to one or two letters, so the symbols are `t` and `e` and no
  caller list built from them means anything. Adding the JavaScript pack to this repo
  without that rule took it from 11,236 links to 306,922 and a re-index from 0.6 s to
  138 s, essentially all of it three vendored bundles. The rule is the naming convention
  only — no line-length heuristic, which would eventually refuse a real file.
- **XAML is read with a grammar derived from HTML's**, because the bundle has no XAML or XML
  one — [`grammars/xaml`](grammars/xaml) is tree-sitter-html compiled again with HTML's tag
  table switched off. The two languages agree on elements, attributes and quoted values, which
  is everything the pack reads: an element's `x:Name`, `Name` or `x:Key` is a declaration and
  its tag is the type, exactly as an `id` is in HTML. What they do **not** agree on is what
  certain tag names mean, and this entry used to say so and wave it away: "a `<Style>` is
  HTML's CSS `<style>`, so its `Setter`s arrive as one opaque blob; the Style's own `x:Key`
  survives, which is the part anything refers to". The first half was right and the second was
  never measured. Everything inside a `<Style>` was being discarded — 33 declarations in this
  repo's own `Themes/Controls.xaml`, every `x:Name` on a template part — with no error raised,
  because a raw-text element is not a parse failure. The variant grammar ends that; see its
  README for the one-line diff. A property element (`<Grid.RowDefinitions>`) is still valid
  XAML and invalid HTML, so the parser still reports it, and that one really does cost nothing:
  measured after the fix, `Controls.xaml` declares 92 names and indexes 93. A markup extension
  is still a single attribute value to this grammar,
  but it is read (M19.3): `{Binding Search.Query}` becomes a binding reference named by its
  first path segment and `{StaticResource PanelBrush}` a resource reference named by its key,
  each resolving at the confidence it deserves — a binding path is at best a cross-language
  name match, and the output marks it so. `x:Class` resolves too (M19.2): the root element is
  a declaration under its verbatim qualified name, it owns the reference, and the last
  segment is what the code-behind class matches — markup and code-behind share a graph. A
  key is its own symbol kind (M20.2): `x:Key` and `x:Name` are two namespaces rather than
  two spellings, so a `{StaticResource SearchBox}` can only reach a keyed resource and never
  the element that happens to share the word. Event handlers resolve too (M20.5), and they
  are the one place this pack guesses: XAML marks an event attribute with no syntax at all,
  so an attribute whose name reads like a routed event and whose value is a bare identifier
  is *nominated*, and the resolver then requires the target be a method on that file's own
  `x:Class`. A wrong nomination therefore finds nothing and is not reported — the trade is
  that a misspelled handler is silent, which the compiler already catches and a misspelled
  binding path does not. One honest limit remains: a member the MVVM source generator
  invents (`[ObservableProperty] string _query` → `Query`) exists in no source file, so
  bindings to it are listed as unresolved rather than resolved — the binding checker below
  remains the tool that can see those.
- **The drift count covers indexed files only.** It compares what the index already holds
  against disk, so it reports edits and deletions but never notices a file created since the
  last run; finding those means a full crawl, which is too much to spend before answering a
  query. Both wordings say "indexed files" for that reason.
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
