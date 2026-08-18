# Field report — OpenSim Studio

| | |
|---|---|
| **Date** | 2026-08-18 (index/cache UTC; 2026-08-17 local) |
| **Repo** | OpenSim Studio — an offline-first WPF/.NET 8 multi-physics CAE platform (FEA, thermal, PCB, RF/MoM, signal integrity, CFD) |
| **Languages** | C# 432 files · XAML 24 files (as `stats` reports) |
| **Size** | 456 files / 23,462 symbols / 127,784 links |
| **Tool build** | `codeanalyzer cache` → workspace cache 22.2 MB, indexed 2026-08-18T06:12:18Z (38 live caches, 755.9 MB total) |
| **What the session was actually doing** | A real capability-gap audit: compare a user's archived Ansys 2023 R1 projects (a static-structural 3-point-bend study and a Fluent conjugate-heat serpentine heat exchanger) against what this codebase can currently produce, and enumerate the missing functionality. The tool was used to answer "what fields does the static solver emit", "who consumes this type", "is this settings feature wired to the UI" — i.e. reachability and dead-seam questions, which is its strong suit. |

## 1. `stats`, verbatim

`reindex` was needed first (no cache existed for this workspace): **10.0 s**, 456 parsed, 0 failed, **4 with syntax errors**, 0 removed.

```
[index: C:\Users\Carpe\Desktop\Claude App Tests\OpenSimStudio (23,462 definitions, 4 imperfect parses (see: errors), built 2026-08-18 06:12Z, 456 indexed files unchanged on disk)]
files: 456 (C# 432 · XAML 24) · 4 imperfect parses (see: errors)
symbols: 23,462 (Variable 11,550 · Parameter 5,062 · Method 3,005 · Field 1,470 · Property 701 · Class 676 · Namespace 431 · Constant 259 · MarkupElement 162 · EnumMember 93 · Enum 22 · Interface 9 · Struct 9 · Typedef 7 · ResourceKey 6)
references: 143,253 · 32,450 carry a receiver (22.7%) · 18,404 carry arguments (12.8%)
resolution, per reference:
  resolved uniquely     74,399   51.9%
  ambiguous             12,652    8.8%  (4.2 candidates each)
  unresolved            56,202   39.2%
by reference kind:
  Use        112,211  uniq  52.5%  amb   9.6%  unres  37.9%  ·  49.6% external
  Call        17,978  uniq  49.3%  amb   6.9%  unres  43.7%  ·  93.6% external
  TypeUse     11,349  uniq  55.2%  amb   5.2%  unres  39.6%  · 100.0% external
  Import       1,157  uniq   0.0%  amb   0.0%  unres 100.0%  · 100.0% external
  Binding        410  uniq  75.1%  amb  15.4%  unres   9.5%  ·  87.2% external
  Inherit        117  uniq  50.4%  amb   0.0%  unres  49.6%  · 100.0% external
  Resource        16  uniq 100.0%  amb   0.0%  unres   0.0%
  Handler         15  uniq  80.0%  amb   0.0%  unres  20.0%  · 100.0% external
by language:
  C#     142,788  uniq  51.9%  amb   8.8%  unres  39.3%  ·  60.9% external  ·  24.0% too common
  XAML       465  uniq  77.4%  amb  13.5%  unres   9.0%  ·  88.1% external  ·  11.9% too common
why the 55,045 unresolved were refused (the other 1,157 are include/import, settled as file dependencies):
  no workspace definition of a compatible kind            33,080   60.1%
  receiver names nothing this workspace declares           1,079    2.0%
  name too common to guess, and no receiver given         13,504   24.5%
  receiver named no type holding that member                 336    0.6%
  a local, or a member of a scope not written in           7,046   12.8%
  refused by no rule above — a gap in this partition           0    0.0%
edges: 127,784 (Unique 74,108 · Ambiguous 53,096 · Weak 580)
  354 references resolve only by a cross-language name match — listings mark those '?'
file dependencies: 0 of 1,157 name a workspace file
database: 22.1 MB
```

**The exhaustiveness row is 0 on this corpus.** Every refused reference was refused by a named rule. That is the headline non-finding and it is worth stating plainly: on a 456-file, 143k-reference C#/XAML corpus the tool has never seen, the partition held.

Two other numbers worth flagging as corpus facts rather than defects:

- **`Call` is 93.6% external** and `TypeUse` is 100% external — this repo leans hard on BCL/`System.*`/CommunityToolkit/Helix/Clipper2. Nothing to fix; it means the *residue* after external is tiny, which is what makes §4 below sharp rather than noise.
- **4 imperfect parses, not ~53.** This repo predates C# 12 collection expressions almost everywhere, so the known grammar limit barely bit here.

## 2. What you asked it

| Question | Tool | Right? | Notes |
|---|---|---|---|
| What BC types exist and how are they scoped? | `file_outline BoundaryCondition.cs` | yes | Perfect. 20 defs in source order, containment-indented; `FaceIds` on the abstract base immediately answered "face-scoped only", which turned out to be the single largest gap in the audit. |
| What does `Material` carry? | `get_symbol Material` → id disambiguation → `#19598` | yes | The 4-way ambiguity prompt ("pass an id") was correct behaviour, not friction — two of the four were unrelated properties named `Material`. Member list settled "no YieldStrength/UTS" in one call. |
| Does anything consume `ElementTensorField`? | `get_callers ElementTensorField` | yes | **The highest-value query of the session.** 3 callers: the producing solver and two tests. That converted "the stress/strain tensors exist" into "they are computed and never reach the UI" — a finding I would not have trusted from grep, because grep can't tell a consumer from a mention. |
| Is `FlowOpening` wired to the app? | `get_callers FlowOpening` | yes | 3 callers: 2 tests + its own settings property. Decisive proof that enclosure/internal-flow boundaries are implemented, tested, and unreachable from the UI. Same shape as above, same value. |
| Who uses `ThermalContacts`? | `get_callers ThermalContacts` | yes | 17 rows across solvers/VMs/tests; confirmed thermal-only (no structural contact) at a glance. |
| What is `PrimitiveFactory`? | `get_symbol PrimitiveFactory` | **NO** | Reported **`callers: 0`** for a class both of whose production call sites exist. See §4. |
| Who calls `PrimitiveFactory.CreateBox` / `CreateCylinder`? | `get_callers #16474`, `#16490` | **NO** | 24 and 5 callers, **all tests**; both real App call sites missing. See §4. |
| Who calls `CfdSettings.ForExternalFlow`? | `get_callers ForExternalFlow` | yes | Found `SolveViewModel.SolveConjugateAsync` at the exact line — and this is namespace-qualified in source too, which is what makes §4 a puzzle rather than a rule. |
| Who calls `OverlayGrid.InteriorQuads`? | `get_callers InteriorQuads` | yes | Found `SceneBuilder.BuildMaskedFieldOverlayModel`, also namespace-qualified. |
| Who uses `NetMesher`? | `get_callers NetMesher` | partial | 17 rows, all correct — but 4 sites in `Ipc2581Tests.cs` written `new OpenSim.Pcb.Import.NetMesher()` are absent. See §4. |
| Is there a member/field named `Geometry` (shadow hypothesis)? | `search_symbols "Geometry" exact kinds=variable,constant` | yes | `exact:true` did exactly what the flag promises — returned `MainViewModel.Geometry`, `Body.Geometry`, and stopped. Without it the fuzzy matcher would have buried these. |
| Sanity-check the index before trusting any of the above | `stats` | yes | Ran it before writing conclusions. The 93.6%-external `Call` row is what told me the unresolved mass was library surface, not missing workspace edges — which is why I treated §4 as a real anomaly rather than expected noise. |

## 3. Where you fell back to grep — and why

| Wanted | Fell back to | Why the tool could not / did not |
|---|---|---|
| The literal field names a solver emits (`"Stress (von Mises)"`, `"Displacement"`) | `grep -n "new NodalScalarField\|new NodalVectorField"` | **Feature gap, mild.** These are constructor *arguments*, not declarations. `get_context` carries "per-site verbatim arguments" and would likely have shown them — I reached for grep out of habit and it cost me a wrong first answer (grep on one pattern showed 2 fields; the file actually returns 4). My mistake, but it points at a discoverability problem: nothing in the tool listing says "arguments at call sites are indexed". |
| Which `CfdSettings` properties the App actually sets | `grep -n "CfdSettings\|ForExternalFlow\|DomainBox\|Openings" SolveViewModel.cs` | **Correct fallback.** "Which of this record's initialisers appear in one file" is an object-initializer question, not a call/reference question. Nothing in the tool answers "what shape is this record built with here". |
| Whether any FE-result export exists anywhere | `grep -rn "\.csv\|WriteAllText\|StreamWriter" OpenSim.App` | **Correct fallback, and it's the interesting one.** This is a *negative* capability question across a subtree — "does anything anywhere write results out". `io_boundaries` is the tool for this and I did not use it. Discoverability bug on my side; I only remembered it existed while writing this report. |
| The list of `[ObservableProperty]` backing fields in a VM | `grep -nE "\[ObservableProperty\]"` | **Feature gap.** Source-generated MVVM properties don't exist as declarations in the source tree; the tool sees `_showArrows` (a private field) and never `ShowArrows`. On a CommunityToolkit.Mvvm repo — 701 properties indexed against ~1,470 fields — a large slice of the app's real public surface is invisible by construction. |
| Prose in a `.docx`/`.pdf`/`.xml`, Ansys mesh zone names | `pdftotext`, `python -c zipfile`, `strings` | Not a code question. Correct fallback, listed only for completeness. |

## 4. Wrong answers

**One, and it is the confident kind.**

`get_symbol PrimitiveFactory` (`#16473`, `OpenSim.Geometry/PrimitiveFactory.cs:10`) reports:

```
callers: 0  callees: 0
```

`PrimitiveFactory` has exactly two non-test call sites in the entire repo, and both exist:

```
OpenSim.App/ViewModels/GeometryViewModel.cs:45:  SetGeometry(Geometry.PrimitiveFactory.CreateBox(BoxSizeX, BoxSizeY, BoxSizeZ), ...
OpenSim.App/ViewModels/GeometryViewModel.cs:56:  SetGeometry(Geometry.PrimitiveFactory.CreateCylinder(CylinderRadius, CylinderHeight), ...
```

Drilling to the members does not rescue it. `get_callers #16474` (`CreateBox`) returns **24 callers, every one a test** (plus `CreatePlate`); `get_callers #16490` (`CreateCylinder`) returns **5, every one a test**. `GeometryViewModel` appears in neither.

Why this costs more than a confusing answer: **"only tests use this" is a true, load-bearing signal on this codebase** — it is precisely the reasoning that made `ElementTensorField` and `FlowOpening` genuine findings in §2, and I acted on both. Here the tool produced the identical output shape for a type that production code *does* use. Nothing distinguished the real dead seam from the false one. Had `PrimitiveFactory` been the audit's subject rather than a side check, I would have reported "primitive geometry is test-only scaffolding" to the user and been wrong.

A related miss, same flavour, different shape: `get_callers NetMesher` returns 17 correct rows but omits `OpenSim.Tests/Pcb/Ipc2581Tests.cs:1377/1410/1436/1491`, which construct it as `new OpenSim.Pcb.Import.NetMesher()`.

**I could not identify the rule, and I am not going to guess one.** My first hypothesis — "namespace-qualified references are missed" — is *refuted* by two measurements in the same session: `Model.CfdSettings.ForExternalFlow(...)` at `SolveViewModel.cs:295` and `PostProcessing.OverlayGrid.InteriorQuads(...)` at `SceneBuilder.cs:565` are written with the same partial-namespace prefix and both resolve correctly. My second hypothesis — "the segment `Geometry` is shadowed by `MainViewModel.Geometry`/`Body.Geometry`" — is *consistent* with the `PrimitiveFactory` case but does not explain the fully-qualified `OpenSim.Pcb.Import.NetMesher` case. Both cases are reproducible from a clean `reindex`; the mechanism is the open question.

## 5. Pitfalls

- **`get_symbol X` for a class reports `callers: 0` even when its members have dozens of callers.** Independent of §4: `PrimitiveFactory`'s *methods* were reachable via `get_callers #16474` while the *class* read `callers: 0`. That may be a defensible design (class-reference vs member-reference are different edges), but the word "callers" on a static class invites exactly the wrong reading, and I read it wrong for several minutes before drilling to members. If class-level counts deliberately exclude member call sites, the field should say so — `class references: 0 (members: 29)` would have cost me nothing.
- **My own mistake, and the most instructive one:** I grepped for one constructor pattern (`new NodalScalarField|new NodalVectorField`) and concluded the static solver emitted 2 result fields. It emits 4 — the other two are `ElementTensorField`, which my pattern didn't name. The tool then *corrected* me: `get_callers ElementTensorField` surfaced `LinearStaticSolver.BuildResultFields` as a caller, which sent me back to read the method properly. **The tool caught a grep error, which is the exact value proposition, and I'd nearly skipped it.**
- `cat OpenSim.Core/Numerics/SymmetricTensor.cs` failed — the type lives in `Results/ResultFields.cs`, against this repo's own "one primary type per file" convention. `get_symbol SymmetricTensor` would have given me the path instantly. Filed as my error, but it's the canonical case for reaching for the tool first.
- `search_symbols` fuzzy matching on a short common word (`Geometry`) is noisy; `exact:true` fixed it immediately. The flag is documented and worked exactly as described — noting it only because the *first* instinct is to add `kinds=` and the *right* instinct is `exact=`.

## 6. What I would fix, ranked

| # | Fix | Grounds (measured) | Query that measured it |
|---|---|---|---|
| 1 | Find the rule that drops the `PrimitiveFactory` / `NetMesher` references. It produces a **silently short caller list**, indistinguishable from a true "test-only" answer. | **2 of 2** non-test call sites of `PrimitiveFactory` missing (→ `callers: 0` on the class); **4 of 4** `Ipc2581Tests.cs` sites missing from `get_callers NetMesher`. Counter-examples that *do* resolve: 2 (`ForExternalFlow`, `InteriorQuads`) — so this is not "all qualified refs", and the rule is unidentified. | `get_symbol #16473`; `get_callers #16474` (24 rows, 0 non-test); `get_callers #16490` (5 rows, 0 non-test); `get_callers NetMesher` (17 rows, `Ipc2581Tests` absent); `grep -rn "PrimitiveFactory.Create" --include=*.cs \| grep -v OpenSim.Tests` → exactly 2 lines. Both counter-examples: `get_callers ForExternalFlow`, `get_callers InteriorQuads`. |
| 2 | Say what a class-level `callers` count includes. Either aggregate member call sites, or label the field so `0` cannot read as "dead". | `PrimitiveFactory` class: `callers: 0`. Its two methods: **29** callers between them (24 + 5). A reader who stops at the class gets the opposite of the truth. | `get_symbol #16473` vs `get_callers #16474` + `get_callers #16490`. |
| 3 | Index source-generated MVVM properties, or state in the tool listing that they are invisible. On a CommunityToolkit.Mvvm app this hides a large share of the real public surface. | This repo declares **701 properties** and **1,470 fields**; `[ObservableProperty]` appears **369** times across 24 App view-models, each generating a public property the index does not hold. **Probe caveat: this over-counts** — it is a textual attribute count, and I did not verify every one generates a distinct property. It errs high; the order of magnitude is the point. | `stats` (701/1,470); `grep -rc "\[ObservableProperty\]" OpenSim.App/ViewModels/*.cs \| awk -F: '{s+=$2} END {print s, NR}'` → 369 across 24 files. |
| 4 | Discoverability: surface `io_boundaries` and `get_context`'s verbatim-arguments capability at the point of use. Both would have replaced a grep fallback I made this session, and I only remembered they existed while writing this report. | 2 of my 5 code fallbacks in §3 (result-export sweep; solver field-name literals) were answerable by tools I own and didn't reach for. That is a 40% self-inflicted fallback rate on a session where I *wanted* to use the tool. | §3, rows 1 and 3. |

Deliberately **not** filed: the 4 imperfect parses (known grammar limit, and remarkably low here); the 39.2% unresolved share (93.6% of `Call` and 100% of `TypeUse` unresolved are external — this repo's dependency surface, not a gap); XAML's 88.1%-external binding paths (bindings name generated/framework members, documented).

## 7. Checks

- [x] Nothing in §6 is on the known-limits list. (§6.3 is adjacent to "JavaScript declares no types" in spirit but is a distinct, C#-specific source-generator gap on a corpus type the tool has not been tuned against.)
- [x] Every number in §6 was measured this session, not carried from another report.
- [x] `stats` in §1 is the whole block, including the `0.0%` exhaustiveness row.
- [x] The languages row came from `stats` (C# 432 · XAML 24), not from what the repo looks like.

---

# Session 2 — the same repo, in WRITE mode (same day)

The first half of this report was an **audit**: read-only, mapping a codebase against an
external reference. This half is the follow-on **implementation** batch on the same corpus —
edge-scoped boundary conditions, derived result fields, statistics, strength properties and a
CSV/PNG export — and it is worth separating, because the tool behaved differently when the
tree was moving under it.

| | |
|---|---|
| Repo | OpenSim Studio (WPF/.NET 8 multi-physics CAE) |
| Mode | implementation: 27 files changed, 19 added, ~760 lines |
| Reindex mid-session | 45 parsed / 429 unchanged / 0 failed, **10.9 s** |
| Index after | 474 files (C# 449 · XAML 25), 24,180 definitions, 148,108 references, 23.2 MB |
| Depth of use | real, but narrower than session 1 — two structural questions, both load-bearing |

## 1. `stats`, verbatim (post-reindex)

```
files: 474 (C# 449 · XAML 25) · 4 imperfect parses (see: errors)
symbols: 24,180 (Variable 11,851 · Parameter 5,170 · Method 3,161 · Field 1,513 · Property 737 · Class 699 · Namespace 448 · Constant 267 · MarkupElement 176 · EnumMember 104 · Enum 23 · Interface 9 · Struct 9 · Typedef 7 · ResourceKey 6)
references: 148,108 · 33,877 carry a receiver (22.9%) · 19,284 carry arguments (13.0%)
resolution, per reference:
  resolved uniquely     76,909   51.9%
  ambiguous             12,779    8.6%  (4.4 candidates each)
  unresolved            58,420   39.4%
why the 57,204 unresolved were refused (the other 1,216 are include/import, settled as file dependencies):
  no workspace definition of a compatible kind            34,414   60.2%
  receiver names nothing this workspace declares           1,104    1.9%
  name too common to guess, and no receiver given         13,935   24.4%
  receiver named no type holding that member                 576    1.0%
  a local, or a member of a scope not written in           7,175   12.5%
  refused by no rule above — a gap in this partition           0    0.0%
```

**The exhaustiveness row is 0.0% again**, now on a corpus 18 files larger than the morning's.
Every unresolved reference was refused by a rule the tool can name.

## 2. What I asked it, and whether the answer was right

| Question | Tool | Right? | What it was worth |
|---|---|---|---|
| Who calls `FeMesh.GetFaceNodes`? | `get_callers` | **yes, and completely** | **The decisive query of the batch.** I had to add a scope kind to boundary conditions and needed every place a scope becomes a node set. It returned 6 solver `Validate` sites, 9 application sites and the test callers. Reading those six, they turned out **byte-identical** — which is what made collapsing them into one shared validator safe rather than a guess. grep would have found the same lines; it would not have told me the list was complete. |
| What does `ProjectSession` declare? | `file_outline` | yes | Placed two new observable collections beside the existing selection state without opening the file. |
| Does the `PrimitiveFactory` `callers: 0` anomaly survive a clean reindex? | `get_symbol` + `get_callers` | **it reproduces** | See §4 — and this time I found the mechanism. |

Two ergonomic notes, both positive: passing `file:` to `file_outline` and `name:` to
`get_symbol` produced **typed errors naming the correct parameter** rather than an empty
result. That is the right failure. And the staleness banner (`N of 456 indexed files changed
on disk — call reindex to refresh`) rode on every response while I was editing, which is
exactly when it matters; I leaned on it to know my answers were about the pre-edit tree.

## 3. Where I fell back to grep, and why

| Fallback | Why the index could not do it |
|---|---|
| `grep -rn "SelectedFaces"` across the App | I needed **XAML binding paths** too. Bindings are attribute strings (`{Binding Results.SelectedField}`), not resolved references, so a property reached only through a binding is invisible to `get_callers` by construction. |
| `grep -o 'Name = "[^"]*"'` on `MaterialLibrary.cs` | ENUMERATING the string literals of 20 object initializers. `find_by_value` matches a value; I wanted the whole list. |
| `grep -rhoE 'x:Name="..."'` across all XAML | Surveying which controls already carry automation ids — an attribute-value census, not a symbol question. |
| `grep -n "BuildResultFields"` | Trivial single-file lookup in a file I already had open. Self-inflicted. |
| `grep -c "" CLAUDE.md` | Prose. Correctly grep. |

Three of five are genuine index blind spots and two of those are the same one: **this app's
UI surface is bound by string path, so the index cannot see the edges that matter most in the
App layer.** That is the same finding as session 2's ranked item #3 in the first half
(source-generated MVVM properties), from the other direction.

## 4. The wrong answer — reproduced, and this time explained

`get_symbol PrimitiveFactory` still reports **`callers: 0`** after a full clean reindex.
Measured, on the rebuilt index:

- `get_callers` on `CreateBox` returns **25 callers — every test, plus the internal
  `CreatePlate`**. The repo has 27 call sites. The two it does not return are
  `GeometryViewModel.cs:45` and `:56` — **the only production callers that exist.**
- Both are written `Geometry.PrimitiveFactory.CreateBox(...)`: a **relative** namespace
  qualifier, resolved by C# from inside `OpenSim.App.ViewModels`.

**My session-1 hypothesis was that a member named `Geometry` on the enclosing type was
shadowing the namespace. That is REFUTED:** `GeometryViewModel` declares no member named
`Geometry` (measured — the file's only `Geometry` tokens are the using, the type name, and
the two call sites).

A second, independent instance on a different symbol, prefix and enclosing type:
`MainViewModel.NewProject` writes `new Core.Model.SimProject()` at `:177`, and
`get_callers SimProject` **does not list `NewProject`** — while it does list
`ProjectSerializer.Load`, `ProjectSession.Project` and eleven tests. `MainViewModel` declares
no member named `Core`.

So the pattern that reproduces is: **a relative (partial) namespace qualifier on a call or
construction is not resolved**, while unqualified and fully-qualified-from-root forms are.
I am naming the pattern, not the rule — I have not read the resolver, and two instances is
evidence rather than proof.

**Counted headroom:** 31 relative-qualifier references across 9 files
(`(Core|Geometry|Meshing|Solvers|Pcb|Rf|Cfd|Model|Numerics|Results|Interfaces|PostProcessing|Persistence).Type`,
excluding fully-qualified `OpenSim.*`). Small in absolute terms — **and that undersells it,
because 2 of the 31 are the sole production callers of a public factory.** The damage is not
proportional to the count; it is concentrated exactly where the shortfall turns a caller list
into a confident, wrong, load-bearing answer.

## 5. What bit me

- **"All callers are tests" is the single most actionable shape this tool produces, and it is
  the shape this bug forges.** I acted on that exact signal three times in session 1 (result
  tensors, `FlowOpening`, contact) to conclude a feature was implemented-but-unreachable.
  Those three happened to be right. `PrimitiveFactory` shows the same output when it is
  wrong, with nothing in the response to tell them apart.
- **A class-level `callers` count is not the sum of its methods'.** `PrimitiveFactory` reads
  `callers: 0` while `CreateBox` alone has 25. Whatever the intended semantics, the two
  numbers side by side read as a contradiction.
- **Nothing bit me about staleness**, which is worth saying: I edited 45 files without
  reindexing and the banner made that safe, because I was only ever querying untouched code.
  Had I queried something I had just changed, the banner is the only thing that would have
  told me.

## 6. What I would fix, ranked, with measured grounds

1. **Resolve relative namespace qualifiers.** Grounds: reproduces after a clean reindex on
   two independent symbols; the shadowing explanation is refuted by measurement; 31 sites in
   9 files, two of which are a public factory's only production callers. This is the same
   item as session 1's #1, now with a mechanism narrowed to a testable shape.
2. **Make the class-level `callers` figure agree with its members, or say what it counts.**
   Grounds: `PrimitiveFactory` 0 against `CreateBox` 25 + `CreateCylinder` 5 in one response
   pair.
3. **(unchanged from session 1) Index source-generated MVVM properties, or state that they
   are invisible.** Grounds: 737 properties indexed against 369 `[ObservableProperty]`
   occurrences across 24 view-models. Session 2 adds a second face of the same gap: WPF
   binding paths are attribute strings, so App-layer consumers are unreachable from the
   index in both directions.

Deliberately not filed: the 4 imperfect parses, the 39.4% unresolved share, the XAML binding
paths as a defect (that one is a design boundary, not a bug — it is filed above only as
context for why grep stayed necessary).

## 7. Checks

- [x] `stats` is the whole block, including the `0.0%` exhaustiveness row, taken after a
      reindex on the changed tree.
- [x] Every number in §4 and §6 was measured in this session on the rebuilt index.
- [x] The session-1 hypothesis was tested and **refuted** before a new one was offered, and
      the new one is labelled a pattern, not a rule.
- [x] Headroom in §6.1 is counted (31 references, 9 files), and its limitation is stated.
- [x] Nothing in §6 is on the known-limits list.
