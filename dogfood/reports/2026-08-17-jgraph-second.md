# Field report — JGraph (second sitting)

| | |
|---|---|
| **Date** | 2026-08-17 |
| **Repo** | JGraph — a C#/.NET 8 plotting application with a MATLAB-compatible scripting language (~23 projects, WPF app plus a CLI) |
| **Languages** | C# 966 · XAML 15 · Python 11 · HTML 8 (from `stats`) |
| **Size** | 1,000 files / 63,015 symbols / 418,649 links |
| **Tool build** | schema v26; index rebuilt 2026-08-17T06:14Z, 65.0 MB, from `codeanalyzer cache` |
| **What the session was actually doing** | Finishing milestone M69, an audit of MATLAB compatibility: measuring per-class graphics property coverage by execution, harvesting recorded divergences out of 69 ADRs into one index, writing a regression script for them, and fixing four interpreter defects (`global` scoping, `evalin('caller')`, and four graphics verbs that returned no handle). |

This is a **second** report on the same repo. The first
(`2026-08-17-jgraph.md`) was written before the tool was updated; its headline finding was a
`get_callers` undercount caused by the C# 12 collection-expression parse failure. That finding is
now **closed by measurement**, which is most of what this report is worth.

## 1. `stats`, verbatim

`reindex` was needed first — the cached index was schema v25 and this build reads v26, which the tool
said plainly rather than answering from a stale cache. It took **41.2s** for 1,000 files, refused
**0**, and reported 16 with syntax errors.

```
[index: E:\EE Projects\JGraph (63,015 definitions, 16 imperfect parses (see: errors), built 2026-08-17 06:14Z, 7 of 1,000 indexed files changed on disk — call reindex to refresh)]
files: 1000 (C# 966 · XAML 15 · Python 11 · HTML 8) · 16 imperfect parses (see: errors)
symbols: 63,015 (Variable 25,293 · Parameter 17,699 · Method 11,476 · Field 2,581 · Property 2,240 · Class 1,210 · Namespace 966 · EnumMember 638 · Constant 361 · MarkupElement 156 · Enum 141 · ResourceKey 115 · Function 73 · Interface 41 · Struct 22 · Typedef 3)
references: 411,926 · 84,858 carry a receiver (20.6%) · 65,403 carry arguments (15.9%)
resolution, per reference:
  resolved uniquely    222,638   54.0%
  ambiguous             32,345    7.9%  (6.1 candidates each)
  unresolved           156,943   38.1%
by reference kind:
  Use        309,992  uniq  52.7%  amb   8.2%  unres  39.1%  ·  44.1% external
  Call        65,236  uniq  56.3%  amb  10.5%  unres  33.2%  ·  99.2% external
  TypeUse     33,047  uniq  66.9%  amb   0.5%  unres  32.7%  ·  94.9% external
  Import       2,947  uniq   0.0%  amb   0.0%  unres 100.0%  · 100.0% external
  Inherit        518  uniq  60.4%  amb   0.0%  unres  39.6%  · 100.0% external
  Binding        107  uniq  62.6%  amb  28.0%  unres   9.3%  ·  70.0% external
  Resource        60  uniq  25.0%  amb   0.0%  unres  75.0%  · 100.0% external
  Handler         19  uniq 100.0%  amb   0.0%  unres   0.0%
by language:
  C#       408,530  uniq  54.2%  amb   7.9%  unres  37.9%  ·  56.3% external
  Python     3,111  uniq  30.1%  amb   2.9%  unres  67.0%  ·  54.2% external
  XAML         198  uniq  55.6%  amb  16.7%  unres  27.8%  ·  94.5% external
  HTML          87  uniq   0.0%  amb   0.0%  unres 100.0%  · 100.0% external
why the 153,996 unresolved were refused (the other 2,947 are include/import, settled as file dependencies):
  no workspace definition of a compatible kind            85,487   55.5%
  name too common to guess, and no receiver given         58,380   37.9%
  receiver named no type holding that member               1,434    0.9%
  a local, or a member of a scope not written in           8,695    5.6%
  refused by no rule above — a gap in this partition           0    0.0%
edges: 418,649 (Unique 222,458 · Ambiguous 195,791 · Weak 400)
  222 references resolve only by a cross-language name match — listings mark those '?'
file dependencies: 0 of 2,932 name a workspace file (2,947 include/import references, deduplicated)
database: 64.9 MB
```

**The exhaustive-partition row is 0 on this corpus**, as it was in the first report. Two runs, two
different tool builds, same answer: no reference was refused by a rule the tool cannot name.

## 2. What you asked it

| Question | Tool | Right? | Notes |
|---|---|---|---|
| Is the index current? | `stats` | yes | Refused to answer from a v25 cache and named the command to fix it. Best possible failure. |
| What still fails to parse? | `parse_errors` | yes | **16 files, down from 136.** The C# 12 collection-expression class is gone; what remains is 11 XAML attached-property forms and 5 C# files. |
| Who references `OptionSpec`? | `get_callers` | yes | The finding that headlined the last report. Now returns a full list, capped at 100 per direction with the cap stated. See §6. |
| What does `JgsGraphicsProperties.cs` define? | `file_outline` | yes | 85 definitions in source order. Found `NamesOf`, `TypeNameOf` and `AddReflected` in one call — this is the query that made the property prober possible, twice now. |
| Who calls `JgsHandleRegistry.For`? | `get_callers` | **almost** | 37 caller methods for 60 textual sites — consistent. One of the 37 is wrong; see §4. |
| Where is `JgsEnvironment` defined and what is its shape? | `search_symbols` (kind: class) | yes | Two results, both right, no noise. The `kind` filter is what made it clean — unfiltered short queries were noisy in the last report. |
| Where is `quiver` registered? | `find_by_value` | n/a | My misuse: it takes a `literal`, and `quiver` is a bareword. Grep instead. |

## 3. Where you fell back to grep — and why

| Wanted | Fell back to | Why the tool could not / did not |
|---|---|---|
| Every registration site of a JGS builtin name (`Define("quiver", …)`) | `grep -rn '"quiver"'` | These are string literals in a registration table, not declarations. Correctly outside a symbol index — this is a text question and grep is the right tool. |
| The `_globalNames` field's four use sites | `grep -n "_globalNames"` | Private field, four lines, one file. `get_callers` would have worked; grep was two seconds and I was already in the file. Discoverability, not capability. |
| How ADRs spell "divergence" | `grep -ril` over `docs/adr` | Prose in Markdown. Not indexed, correctly. |
| The true count of `OptionSpec` mentions and `JgsHandleRegistry.For` sites | `grep -ro … \| wc -l` | To check the tool's numbers for this report. `get_callers` counts *caller methods*; I wanted *sites*. `include_sites: true` exists and would have answered it — my miss. |
| Which drawing verbs return `JgsValue.Null` | `grep -n "return JgsValue.Null;"` | A question about a returned *value*, not a symbol relationship. Nothing in a symbol graph answers "what does this function return at each exit". |

## 4. Wrong answers

**One, and it wears its own warning.** `get_callers` on `JgsHandleRegistry.For` lists

```
#48268 ColumnMajor method src/JGraph.Numerics/LinearAlgebra/DenseProduct.cs:28 call~
```

The line is `Parallel.For(0, cols, c => MultiplyColumn(…))` — the .NET BCL, not the workspace
registry. The `~` marks it as one of several name matches, which is honest, but the reference
**carries a receiver** (`Parallel`) that names no workspace type. `stats` already has a rule for
exactly that shape — *"receiver named no type holding that member"*, 1,434 references — and it did
not apply here. Ranked in §6.

Nothing else was wrong. In particular the `OptionSpec` undercount from the first report is gone.

## 5. Pitfalls

- **`file_outline` takes `rel_path`, not `path` or `file`, and says neither.** Both wrong guesses
  returned the bare string `An error occurred invoking 'file_outline'.` with no mention of a
  parameter. I burned two round-trips and a `ToolSearch` fetching the schema. `find_by_value` cost a
  third the same way (it takes `literal`).
- **My own:** I asked `get_callers` for a count and it answers with a list of caller *methods*, not
  call *sites*. 37 vs 60 is not a discrepancy, and I nearly wrote it up as one before running the
  grep. `include_sites: true` is the right flag and I did not reach for it.
- **My own, and the same shape as last time:** the plan I was working from claimed a defect
  (`[a,b] = handles{1}(x)` takes one output) that I had derived from *reading* `EvaluateForOutputs`
  with the tool's help. Probing it found all five variants work. The tool showed me the code
  correctly; the inference on top of it was mine, and wrong.

## 6. What I would fix, ranked

| # | Fix | Grounds (measured) | Query that measured it |
|---|---|---|---|
| 1 | Name the offending parameter when a tool call has a bad argument, instead of `An error occurred invoking 'X'.` | 3 of my ~10 tool calls this session failed this way and cost 3 round-trips plus a `ToolSearch`; 2 were `file_outline`, 1 `find_by_value` | `file_outline{path:…}`, `file_outline{file:…}`, `find_by_value{value:"quiver"}` — all three returned the same opaque string |
| 2 | Apply the "receiver named no type holding that member" rule before falling back to a bare-name match, for calls that carry a receiver | 1 of 37 callers of `JgsHandleRegistry.For` is wrong (2.7%), and it is the only `Parallel.For` site in the repo — so the collision is with an external BCL member, exactly the case that rule exists for. 84,858 references (20.6%) carry a receiver, so the rule has reach | `get_callers{JgsHandleRegistry.For}` → 37 rows, 1 marked `~`; `grep -ro "Parallel\.For" src/ \| wc -l` → 1; `grep -ro "JgsHandleRegistry\.For" src/ \| wc -l` → 60 |
| 3 | When `get_callers` hits the 100 cap, print the total it capped | `get_callers{OptionSpec}` printed 100 rows and `… list capped at 100 per direction` with no total. The true figure is 41 files / 196 mentions — I could not tell 101 from 1,010 without grepping, and "how many" was the question the last report got wrong | `get_callers{OptionSpec}`; `grep -rl OptionSpec src/ --include=*.cs \| wc -l` → 41; `grep -ro OptionSpec src/ --include=*.cs \| wc -l` → 196 |

Nothing above is on the known-limits list. Two items from the previous report are **retired by
measurement rather than restated**: the C# 12 parse failure (136 → 16 imperfect parses) and the
`stats` header claiming staleness immediately after a successful reindex (it now reports
`1,000 indexed files unchanged on disk` with no instruction, and only asks for a reindex when files
have actually changed — it correctly said `7 of 1,000 changed` after I edited seven).

## 7. Checks

- [x] Nothing in §6 is on the known-limits list.
- [x] Every number in §6 was measured this session.
- [x] `stats` in §1 is the whole block.
- [x] The languages row came from `stats`.

## Verdict

**It earned its place, and the gap it left last time is closed.** The single query that shaped two
milestones is `file_outline` on a 2,300-line file: 85 definitions with signatures, in one call, from
which the whole property-prober design fell out. `get_callers` — the tool that gave me a confidently
wrong number in the first report and cost me an hour — was right this time on the same construct,
and I checked it rather than assuming.

What remains is smaller and duller than last time, which is the shape of a tool that is working: an
error message that does not name its bad parameter, a cap that does not print its total, and one
external-BCL name collision out of thirty-seven. None of those is worth routing around the tool for.
