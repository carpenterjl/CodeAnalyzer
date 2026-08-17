# Field report — install smoke corpus (Python + C)

| | |
|---|---|
| **Date** | 2026-08-16 |
| **Repo** | A two-file synthetic workspace written to verify the machine-wide install, not a real codebase. Reported because it found something anyway. |
| **Languages** | C 1 file · Python 1 file (as `stats` reports them) |
| **Size** | 2 files / 11 symbols / 6 links |
| **Tool build** | installed 2026-08-16 from `eef0e5f` (M27.4) via `tools\install-codeanalyzer.ps1` |
| **What the session was actually doing** | proving the installed copy resolves a foreign workspace from cwd with no `--root` |

**Read this one as a smoke test, not as evidence about Python codebases.** n = 20 lines. The
rule in §4 is confirmed from the resolver's source; the *magnitude* on a real Python repo is
unmeasured and nobody should rank it until it is.

## 1. `stats`, verbatim

```
files: 2 (C 1 · Python 1)
symbols: 11 (Function 3 · Variable 2 · Method 2 · Macro 1 · Constant 1 · Field 1 · Class 1)
references: 16 · 3 carry a receiver (18.8%) · 4 carry arguments (25.0%)
resolution, per reference:
  resolved uniquely          6   37.5%
  ambiguous                  0    0.0%
  unresolved                10   62.5%
by reference kind:
  Use            11  uniq  36.4%  amb   0.0%  unres  63.6%  ·  57.1% external
  Call            4  uniq  50.0%  amb   0.0%  unres  50.0%  · 100.0% external
  Include         1  uniq   0.0%  amb   0.0%  unres 100.0%  · 100.0% external
by language:
  Python        14  uniq  35.7%  amb   0.0%  unres  64.3%  ·  66.7% external
  C              2  uniq  50.0%  amb   0.0%  unres  50.0%  · 100.0% external
why the 9 unresolved were refused (the other 1 are include/import, settled as file dependencies):
  no workspace definition of a compatible kind                 6   66.7%
  name too common to guess, and no receiver given              0    0.0%
  receiver named no type holding that member                   0    0.0%
  a local, or a member of a scope not written in               3   33.3%
  refused by no rule above — a gap in this partition           0    0.0%
edges: 6 (Unique 6)
file dependencies: 0 of 1 name a workspace file
database: 0.1 MB
```

Index built in 0.2 s, 0 files refused, 0 syntax errors.

**The exhaustiveness row reads 0 on a corpus M27.1 was never tuned against.** That partition was
built and checked entirely against this tool's own C#/XAML repository; this is its first reading
on Python and C. It is one small corpus, so it is a weak confirmation — but it is a confirmation
from outside, which no number in the v12 report is.

## 2. What you asked it

| Question | Tool | Right? | Notes |
|---|---|---|---|
| Does the installed copy see the cwd workspace with no `--root`? | `stats` over stdio | yes | header named the right root |
| What tools does the installed copy expose? | `tools/list` | yes | 14, same set as this repo's dev copy |
| Who calls `SensorBus`? | `get_callers` | **no** | "no callers in the index" — see §4 |
| What does `main` call? | `callees --sites` | yes | both call sites, with verbatim arguments |
| Who calls `read_frame`? | `callers` | yes | resolved through `bus = open_bus(None)` |

## 3. Where you fell back to grep — and why

| Wanted | Fell back to | Why the tool could not / did not |
|---|---|---|
| The literal SQL fragment `CallableKinds` expands to | `Grep` on the resolver source | Reading a field's *initializer text* is a source question, not a graph one. `get_symbol` returns a fact sheet and `get_context` caps its excerpt; four sibling initializers at once is what a regex is for. Correct fallback, not a gap. |

## 4. Wrong answers

**`get_callers SensorBus` → "no callers in the index", with `open_bus` returning `SensorBus(port)`
one line away.**

Not a wrong *edge* — the tool drew nothing and refused honestly, filing the reference under "no
workspace definition of a compatible kind". But the answer a reader takes away is wrong.

The rule, read from `ReferenceResolver.cs:1027`:

```
CallableKinds      = Function, Method, Macro, Module      ← what a Call may land on
ConstructibleKinds = Class, Struct, Function, Module      ← what an Instantiate may land on
```

A `Call` therefore cannot land on a `Class` — deliberately, and right for C. Python has no
`new`, so `SensorBus(port)` is syntactically an ordinary call and the pack emits `Call`; the
class is then unreachable by construction. C# and JavaScript both write `new`, so the home
corpus cannot show this: M23.2 fixed exactly this shape for JS `new Foo()` and Python was never
in view.

Measured, not assumed — the 4 `Call` references are `SensorBus(port)`, `open_bus(None)`,
`bus.read_frame()`, `self.port.read(8)`; the row reads 50% resolved, and the two that resolved
are the two named above. So `SensorBus(port)` was emitted and refused, rather than never
captured.

## 5. Pitfalls

- **I nearly credited the wrong rule.** `bus.read_frame()` resolving looked like M27.2's
  factory-call receiver typing working on Python. It is not: M27.2 keys on `type_text IN ('var',
  'auto')` and a Python target has neither. `read_frame` is simply a unique name in a two-file
  workspace. A one-symbol corpus cannot distinguish "typed the receiver" from "only one
  candidate existed", and I would have reported a win that never happened.
- `codeanalyzer --version` is not a command. `cache` is the read that needs no workspace index —
  it is the right smoke test for an install.
- The published assembly is `codeanalyzer.dll`, never `CodeAnalyzer.Cli.dll`; checking for the
  latter makes a successful publish look failed.

## 6. What I would fix, ranked

| # | Fix | Grounds (measured) | Query that measured it |
|---|---|---|---|
| 1 | Let a Python `Foo(...)` reach a class — emit `Instantiate` from the Python pack when the callee names a class, or admit type kinds to `Call` for languages without `new` | **Unmeasured on any real corpus.** Here: 1 of 4 Call references, which means nothing at n=4. The *rule* is confirmed; the *payoff* is not. Do not rank this until a real Python repo has been measured. | `stats` by-kind row + `ReferenceResolver.cs:1027` |

That is the whole list, and the grounds cell says why it is not yet actionable. A two-file
corpus can show that a rule exists; it cannot show that fixing it is worth a round.

## 7. Checks

- [x] Nothing in §6 is on the known-limits list — Python class instantiation is not among them.
- [x] Every number was measured this session.
- [x] `stats` in §1 is the whole block.
- [x] The languages row came from `stats`.
