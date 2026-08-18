# Field report — JGraph (fourth sitting)

| | |
|---|---|
| **Date** | 2026-08-18 |
| **Repo** | JGraph — a C#/.NET 8 plotting application with a MATLAB-compatible scripting language (~23 projects, WPF app plus a CLI) |
| **Languages** | C# 966 · XAML 15 · Python 14 · HTML 8 (from `stats`) |
| **Size** | 1,003 files / 63,129 symbols / 402,658 edges |
| **Tool build** | schema v26; index built 2026-08-18T05:17Z, 71.2 MB |
| **What the session was actually doing** | Finishing milestone M70 — waves D, E and F. A reduction dimension (`sum(A,[1 2])`) across the reduction family, a surface colour-data grid through the model and its serialization, `errorbar`'s missing forms, then re-running both probers, regenerating three coverage documents, and writing ADR 0070 plus a 14-section stress script. |

Fourth report on this repo. The first three are `2026-08-17-jgraph.md`,
`2026-08-17-jgraph-second.md` and `2026-08-18-jgraph-third.md`.

**This is a thin report, honestly labelled.** The session used the tool three times in roughly
eighty tool calls. That is itself the finding, and §5 says why: the work was almost entirely
*editing* code whose location was already known from the previous sitting, and the questions that
did come up were about string literals, argument shapes and runtime behaviour — none of which a
symbol index answers. I am not padding it out.

## 1. `stats`, verbatim

No reindex this session; the index was two builds stale by the end (17 of 1,003 files changed) and
I did not refresh it, because nothing I asked depended on the files I was editing.

```
[index: E:\EE Projects\JGraph (63,129 definitions, 5 imperfect parses (see: errors), built 2026-08-18 05:17Z, 17 of 1,003 indexed files changed on disk — call reindex to refresh)]
files: 1003 (C# 966 · XAML 15 · Python 14 · HTML 8) · 5 imperfect parses (see: errors)
symbols: 63,129 (Variable 25,346 · Parameter 17,703 · Method 11,480 · Field 2,582 · Property 2,241 · Class 1,210 · Namespace 966 · EnumMember 638 · Constant 372 · MarkupElement 184 · Enum 141 · ResourceKey 119 · Function 81 · Interface 41 · Struct 22 · Typedef 3)
references: 412,927 · 85,026 carry a receiver (20.6%) · 65,798 carry arguments (15.9%)
resolution, per reference:
  resolved uniquely    219,285   53.1%
  ambiguous             30,089    7.3%  (6.1 candidates each)
  unresolved           163,553   39.6%
by reference kind:
  Use        310,529  uniq  52.2%  amb   7.9%  unres  39.8%  ·  43.3% external
  Call        65,417  uniq  52.8%  amb   8.0%  unres  39.1%  ·  84.6% external
  TypeUse     33,100  uniq  66.8%  amb   0.5%  unres  32.7%  ·  94.9% external
  Import       2,963  uniq   0.0%  amb   0.0%  unres 100.0%  · 100.0% external
  Inherit        518  uniq  60.4%  amb   0.0%  unres  39.6%  · 100.0% external
  Resource       208  uniq   7.2%  amb   0.0%  unres  92.8%  · 100.0% external
  Binding        173  uniq  38.7%  amb  38.2%  unres  23.1%  ·  92.5% external
  Handler         19  uniq 100.0%  amb   0.0%  unres   0.0%
by language:
  C#       408,599  uniq  53.4%  amb   7.3%  unres  39.3%  ·  54.3% external  ·  36.0% too common
  Python     3,829  uniq  30.1%  amb   2.4%  unres  67.5%  ·  54.9% external  ·  24.9% too common
  XAML         412  uniq  26.7%  amb  16.7%  unres  56.6%  ·  98.7% external  ·   1.3% too common
  HTML          87  uniq   0.0%  amb   0.0%  unres 100.0%  · 100.0% external
why the 160,590 unresolved were refused (the other 2,963 are include/import, settled as file dependencies):
  no workspace definition of a compatible kind            85,948   53.5%
  receiver names nothing this workspace declares           5,953    3.7%
  name too common to guess, and no receiver given         58,468   36.4%
  receiver named no type holding that member               1,409    0.9%
  a local, or a member of a scope not written in           8,812    5.5%
  refused by no rule above — a gap in this partition           0    0.0%
edges: 402,658 (Unique 219,078 · Ambiguous 182,890 · Weak 690)
  285 references resolve only by a cross-language name match — listings mark those '?'
file dependencies: 0 of 2,948 name a workspace file (2,963 include/import references, deduplicated)
database: 71.2 MB
```

**The exhaustive-partition row is 0 for the fourth time**, on a corpus outside the tool's own repo.
Every number in this block is identical to yesterday's, which is expected — the index was not
rebuilt — and is recorded so the comparison is possible rather than assumed.

## 2. What you asked it

Three calls. That is the whole list.

| Question | Tool | Right? | Notes |
|---|---|---|---|
| Where is the `Reduce` helper the reductions share? | `search_symbols{Reduce}` | **yes, and better than I asked** | Returned 20 matches ranked, and the top two were the two different `Reduce` methods that matter — `JgsBuiltins.Reduce` in `.Statistics.cs:690` and a second in `JgsBuiltins.cs:3395`. A grep would have given me those plus `MeshOperations.Reduce`, `Hessenberg.Reduce`, `Boundaries.Reduce` and `ReduceHaze` with no ordering. The kind and signature in the listing let me discard five of them without opening a file. |
| What does `ErrorBarPlot` carry? | `file_outline` | **yes, and it decided the wave's scope** | 29 definitions, including `_errorNeg` and `_errorPos` as *separate* fields. That answered "is the asymmetric error-bar form a model change or an argument change" in one call — it is an argument change, the model has held two arrays since M6. Had it been one field, that part of the milestone would have been a different size. |
| What does `SurfacePlot` carry, and where does its colour come from? | `file_outline` | yes | 143 definitions. Showed `_texture`, `_colormap`, `_autoScaleColor`, `Palette(...)`, `ResolveColorRange()` — the whole colour path in one listing, which is what I needed to add a `CData` grid without hunting. Fourth session running that `file_outline` is the highest-value call on this repo. |

## 3. Where you fell back to grep — and why

| Wanted | Fell back to | Why the tool could not / did not |
|---|---|---|
| Every registration of `sum`, `prod`, `any`, `all`, `cummax`, `vecnorm`, `issorted` | `grep -n 'Define("sum"\|...'` | String literals in a registration table. Correctly outside a symbol index, and this was most of the session's searching — as it was last session. |
| Which verbs route through `Surface3D` | `grep -o 'Surface3D("[a-z0-9]*"'` | Same shape: the discriminator is a string literal argument, not a symbol. Gave me the exact seven names in one call. |
| Whether the `.graph` DTO has a surface colour field | `grep -n -B6 'public double\[\]\[\] Z'` | I needed the two *adjacent* Z fields distinguished (surface vs contour) to anchor an edit. That is a positional question about text. |
| The generated coverage docs' prose | `sed -n`, `git diff` | Reading generated Markdown. Never a tool question. |
| Whether `caxis(ax, …)` actually applies | **the CLI, not grep** | See §5 — this is the interesting one. |

## 4. Wrong answers

**None.** Three calls, three correct answers, one of which (`ErrorBarPlot`) was load-bearing for a
scope decision.

## 5. Pitfalls

- **My own, and the session's most expensive:** I built an automated widening of a prober's verb
  list, driven by the R2021b documentation dump's "target axes" argument role. It ran, produced 31
  new verbs, and was **wrong** — the dump describes *arguments*, and "takes a target axes" is a
  different question from "returns an object", so it had collected `axis`, `daspect`, `rlim` and 28
  other query verbs and would have reported all 31 as returning no handle. I caught it only because
  I printed the list before wiring it in. Reverted, replaced with a hand-written widening, and the
  reasoning written into the file. **No tool could have caught this**, and it is recorded here
  because the same shape — a plausible filter measuring the wrong property — is what a symbol index
  would be asked to catch if it ever grew a "find all X-like things" feature.
- **Four separate times, the failing thing was my test, not the code**: an `issorted` expectation
  where I mis-read which slice was ascending (twice), a `caxis` assertion on an axes with nothing
  colour-mapped, a `pcolor` assertion expecting a `SurfacePlot` where the build makes an
  `ImagePlot`, and a `surf(0)` preamble that was simply nonsense. Every one was settled by running
  the CLI and reading the actual answer. **The lesson is the repo's own standing rule** — probe
  before writing the test — and it is a lesson about runtime behaviour, which no static index
  addresses.
- **A real defect I shipped and only a test caught**: `tiledlayout('flow')` made one axes for four
  `nexttile` calls, because the cursor wrapped modulo a 1-by-1 grid before the growth check could
  see an overflow. Invisible to reading, invisible to an index; visible the moment something counted
  the axes.
- **Bash heredocs break on long Python payloads in this shell** — second session running. Writing
  the script to a file first is the reliable path. Not a CodeAnalyzer issue; noted because it shapes
  how much of a session is `Bash` versus file writes.

## 6. What I would fix, ranked

| # | Fix | Grounds (measured) | Query that measured it |
|---|---|---|---|
| 1 | **Still the previous report's #1**, unchanged and not re-measurable this session: when `search_symbols` finds no good name match, fall back to doc-comment text. I did not hit it this sitting because I already knew every name I needed from yesterday | 0 occurrences this session, so **no new evidence either way**. Carrying it forward rather than re-ranking it on nothing | — |
| 2 | Nothing new. | Three tool calls is too thin a sample to rank anything from. The previous report's #2 (flag duplicate string-key registrations) was **not re-encountered**: I edited registration tables all session and found no new shadowed pair, which is weak evidence the four found in M69/M70 were the whole set rather than a sample | `grep -rn 'Define\w*("surf"\|"mesh"\|"contour"' src/JGraph.Scripting/Jgs/*.cs` → one registration site each |
| 3 | Nothing. | — | — |

**A note on ranking from a thin session.** Three of the previous report's items were retired by
measurement; inventing a fourth from three tool calls would be exactly the "a rule named is not a
rule modelled" failure the template warns about. The honest output of this sitting is item 1 carried
forward unchanged and an explicit statement that nothing new was measured.

## 7. Checks

- [x] Nothing in §6 is on the known-limits list.
- [x] Every number in §6 was measured this session, or is explicitly marked as carried forward.
- [x] `stats` in §1 is the whole block.
- [x] The languages row came from `stats`.
- [x] The thinness of the session is stated in the header rather than disguised.

## Verdict

**It earned its keep three times in a session where it was barely applicable, and the middle one
paid for the rest.** `file_outline` on `ErrorBarPlot` answered a scope question — is the asymmetric
error-bar form a model change or an argument change — in a single call, and the answer (two separate
arrays already on the model since M6) turned a feared wave into an afternoon.

But the honest summary is that **M70's second half was not a code-navigation problem**. It was
editing known locations, running a CLI to find out what the build actually does, and writing
documents. The questions that arose were about string literals in registration tables, argument
shapes at runtime, and whether a documented MATLAB form works — three kinds of question a resolved
symbol graph is not for, and grep and the CLI are.

The session's largest mistake, an automated verb-list widening that would have libelled 31 verbs,
came from a *filter* that looked principled and measured the wrong property. That is worth carrying
into the tool's own design: the failure was not bad data, it was a plausible proxy. Any feature that
infers "things like X" from a structural role will be able to fail the same way, and the only thing
that caught it here was printing the list before trusting it.
