# Field report — JGraph (third sitting)

| | |
|---|---|
| **Date** | 2026-08-18 |
| **Repo** | JGraph — a C#/.NET 8 plotting application with a MATLAB-compatible scripting language (~23 projects, WPF app plus a CLI) |
| **Languages** | C# 966 · XAML 15 · Python 14 · HTML 8 (from `stats`) |
| **Size** | 1,003 files / 63,129 symbols / 402,658 edges |
| **Tool build** | schema v26; index built 2026-08-18T05:17Z, 71.2 MB |
| **What the session was actually doing** | Planning and then implementing milestone M70, a *remediation* pass over what M69's audit measured: applying a leading-axes-handle argument across ~35 drawing verbs, giving five verbs the handle they never returned, and adding thirteen documented `image`/`imagesc` forms plus `pcolor(C)`, `subplot`'s trailing word and `tiledlayout('flow')`. |

Third report on this repo. The first two are `2026-08-17-jgraph.md` and `2026-08-17-jgraph-second.md`.
This one is worth writing because the work was **mechanical edits across many files**, which is a
different shape from the two audit sessions before it, and it stressed a different part of the tool.

## 1. `stats`, verbatim

No reindex was needed — the index was current at session start and refreshed itself once during it.

```
[index: E:\EE Projects\JGraph (63,129 definitions, 5 imperfect parses (see: errors), built 2026-08-18 05:17Z, 9 of 1,003 indexed files changed on disk — call reindex to refresh)]
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

**The exhaustive-partition row is 0 again — three sittings, three tool builds, same answer.** Two
things in this block are new since yesterday and both are improvements: the partition has gained a
row (`receiver names nothing this workspace declares`, 5,953) that was previously folded elsewhere,
and each language row now names the rule that takes most of what `external` leaves. The C# row's
`36.0% too common` is the single most useful number in the block for this corpus, because it says
the residue is bare-name calls rather than anything the resolver got wrong.

**Parse errors fell again: 16 → 5.**

## 2. What you asked it

| Question | Tool | Right? | Notes |
|---|---|---|---|
| Is there an axes-targeting helper already? | `search_symbols{TargetAxes}` | **no, but honestly** | Answered "no symbol matches 'TargetAxes' well" and listed three where the letters merely appear in order. The helper exists and is called `PeelAxes`. A grep for a *pattern* found it in one call. See §3. |
| What does `GraphObject` carry? | `file_outline` | yes | 26 definitions. Settled in one call whether MATLAB's common properties (`Selected`, `HitTest`, `Tag`, `UserData`) have anything real behind them — they map to `IsSelected`, `Selectable`, `Tag`, `UserData`. That answer shaped a whole milestone's scope decision. |
| Where is `PeelAxes` and how widely is it used? | `get_symbol` | yes | Signature, the full doc-comment, and `callers: 59`. The comment — "Every drawing verb takes one" — was the finding: it was written as though true and was not, which is exactly what the milestone then fixed. |
| What does `JgsGraphicsProperties.cs` define? | `file_outline` | yes | 85 definitions. Third session running that this one call is the highest-value query on this repo. |
| What is `Surface3D`'s signature? | `get_symbol` | yes | Full signature, 10 members, `callers: 4`, and — usefully — an `unresolved (3)` block naming the three lambda parameters it calls through. That is the honest answer for a higher-order function. |
| What does `probe-forms.py` define? | `file_outline` | yes | 20 definitions in a Python file. Let me reuse its sample table in a new verifier instead of re-deriving it. |

## 3. Where you fell back to grep — and why

| Wanted | Fell back to | Why the tool could not / did not |
|---|---|---|
| The axes-targeting helper, before I knew its name | `grep -o "PeelAxes\|AxesTarget\|TargetOrGca..."` | **The real gap this session.** I knew the *concept* and not the *name*. `search_symbols` matches names, so a concept query cannot land. An alternation grep over five guesses found it instantly. |
| Every registration site of ~35 builtins (`Define("surf", …)`) | `grep -rn 'Define[A-Za-z]*("surf"'` | String literals in a registration table, not declarations. Correctly outside a symbol index. This was most of the session's searching. |
| Whether `contour` is registered twice | `grep -rn '"contour"'` | Same reason — and it **found a real shadowed registration** the tool could not have. See §4. |
| Where `AxesModel`/`DataRange` live, for a missing `using` | `grep -rn "struct DataRange"` | Would have worked via `search_symbols{kind: class}`; grep was one call and I was already in a shell loop. Discoverability, not capability. |
| `JgsType`'s member spelling (`Str` vs `String`) | `grep -n "String" JgsValue.cs` | `get_symbol` on the enum would have listed members. My habit, not the tool's limit. |

## 4. Wrong answers

**None this session.** Every answer the tool gave was correct, including the three where the honest
answer was a negative.

Worth recording as the opposite of a wrong answer: `search_symbols{TargetAxes}` returned *no match*
and said so in a way that made the negative usable — "no symbol matches 'TargetAxes' well — in these
the letters merely appear in order", followed by the parenthetical that an exact match is a different
question. Two of the three prior reports' complaints were about confident wrongness. This is the
shape that avoids it.

The one thing neither the tool nor I could have found by reading: **`contour` and `contourf` were
each registered twice**, in `JgsBuiltins.cs` and again in `JgsBuiltins.Decorations.cs`, with the
later registration silently overwriting the earlier. That is a runtime fact about dictionary
insertion order, not a static one. It surfaced because a *verifier I wrote* reported a failure whose
error message came from the other body. A symbol index cannot see it, and should not be expected to.

## 5. Pitfalls

- **`search_symbols` is a name index, and a rename-shaped question needs a concept index.** I lost a
  round-trip looking for "the axes-target helper" under three names it does not have. The tool's
  reply was honest; the retrieval model simply cannot answer that question. Nothing to fix in the
  tool necessarily — but see §6 #1.
- **`get_symbol` needs `symbol`, not `name`.** The error message named the right parameter and listed
  the accepted forms, which fixed it on the next call with no schema fetch. That is a **direct
  improvement over the previous report's #1 finding** (`file_outline` returning a bare
  `An error occurred invoking 'file_outline'.`), and it retires that item.
- **My own:** I wrote in the plan that `SurfacePlot` "already carries a colour source for parametric
  surfaces, so this is routing an array into it". It does not — `parametric` is about geometry, and
  the colour is derived from Z. I found this by reading the file, after `get_symbol` had shown me the
  signature correctly. The tool showed me a true thing; the inference on top of it was mine and wrong.
  Same shape as the last two reports' self-inflicted item, which suggests the lesson is mine to learn.

## 6. What I would fix, ranked

| # | Fix | Grounds (measured) | Query that measured it |
|---|---|---|---|
| 1 | When `search_symbols` finds no good name match, offer the doc-comment text as a second pass — the concept is usually written in the comment even when it is absent from the name | 1 of my ~12 tool calls failed this way and cost a round-trip. `PeelAxes`'s doc-comment contains the words "axes handle" and "drawing verb"; the query was "TargetAxes". This repo carries prose doc-comments on 11,480 methods, so the text exists to search | `search_symbols{TargetAxes}` → 3 letters-in-order matches, none relevant; `grep -o "PeelAxes\|AxesTarget\|TargetOrGca\|PeelRuler"` → 62 sites in 19 files |
| 2 | Flag when two definitions in a workspace register the same string key into the same collection | 3 shadowed registrations found in this repo this session (`contour`, `contourf`, `contour3`), all dead code, all invisible to a symbol index. M69 settled a fourth of the same shape (`readmatrix`). That is 4 in two sittings | `grep -rn '"contour"' src/JGraph.Scripting/Jgs/*.cs` → 2 registration sites; confirmed at runtime by `[C,h] = contour(peaks(8))` answering `C is 2x126`, which only the second body can do |
| 3 | Nothing else. | The previous report's #1 (opaque bad-argument errors) is **fixed** — `get_symbol` with a wrong parameter now names the parameter and lists the accepted forms. #2 (the `Parallel.For` name collision) was not re-encountered. #3 (the 100-row cap printing no total) was not hit, as nothing exceeded the cap this session | `get_symbol{name: "PeelAxes"}` → `get_symbol needs 'symbol' — you passed 'name', which is not a parameter of get_symbol. get_symbol takes: symbol (…)` |

## 7. Checks

- [x] Nothing in §6 is on the known-limits list.
- [x] Every number in §6 was measured this session.
- [x] `stats` in §1 is the whole block.
- [x] The languages row came from `stats`.

## Verdict

**It earned its place, and for the third time the query that did it was `file_outline`.** On a
session that was mostly mechanical edits, the tool's value was concentrated in the few moments where
a scope decision hung on a fact about the code: `file_outline` on `GraphObject` decided whether
MATLAB's common graphics properties could be answered honestly, and `get_symbol` on `PeelAxes`
turned "there is probably a helper" into "there is one, it has 59 callers, and its own comment claims
a reach it does not have."

The bulk of the searching was for string literals in registration tables, which is a text question,
and grep is right for it. That is not a criticism — knowing which questions to route where is the
whole skill, and this session the split was clean.

Two of the previous report's three ranked items are retired: one fixed, one not reproduced. The new
top item is a genuine capability gap rather than a rough edge — a name index cannot answer a concept
query, and this codebase writes its concepts in prose comments that are already indexed.
