# Field report — several sessions at once

| | |
|---|---|
| **Date** | 2026-08-16 |
| **Repo** | Two synthetic C corpora, 600 files each (14 symbols per file, names prefixed `ALPHA_`/`BETA_` so a leak between them would be visible). Built to answer one question: does using the tool from several Claude Code sessions at once tangle their workspaces? |
| **Languages** | C 600 files (each corpus) |
| **Size** | 600 files / 8,400 symbols / 7,200 links (each) |
| **Tool build** | installed 2026-08-16 from `eef0e5f` (M27.4) |
| **What the session was actually doing** | registering the MCP server for every project on this machine |

## 1. `stats`, verbatim

```
files: 600 (C 600)
symbols: 8,400 (Function 4,200 · Parameter 3,600 · Macro 600)
references: 7,800 · 0 carry a receiver (0.0%) · 3,600 carry arguments (46.2%)
resolved uniquely      7,200   92.3%
```

Trimmed here only because this corpus is generated and its resolution profile is an artifact of
the generator, not a finding. The concurrency results below are the report.

## 2. What you asked it

| Question | Method | Answer |
|---|---|---|
| Do two different projects indexed at the same instant stay isolated? | two `index --full` in parallel, then search each for the other's names | **Yes.** Both exit 0 in 1.6 s; each index returns 5 of its own symbols and **0** of the other's. Cache directories differ by path hash (`wsA-2F073E6D…` / `wsB-00F49898…`). |
| Can one session read while another re-indexes the same project? | 1 writer + 3 readers, same workspace | **Yes.** All four exit 0; readers answered in 0.2 s mid-write. WAL, so a writer never blocks a reader. |
| Two sessions re-indexing the *same* project at the same instant? | 2 `index --full` in parallel | **One wins, one fails loudly.** exit 0 / exit 3. |

## 3. Where you fell back to grep — and why

| Wanted | Fell back to | Why |
|---|---|---|
| The `journal_mode` / `Pooling` settings on each connection | `Grep` over `src/` | Four call sites of a connection-string builder, wanted verbatim. A source question, not a graph one. |

## 4. Wrong answers

None from the tool. **One from my own probe, which is worth more than a clean run.**

My isolation check asked "does the string `BETA` appear in workspace A's search output". It does —
in `no symbols match 'BETA_calc'`, which is the query echoed back. So the probe reported a leak
**exactly when there was none**, and it erred toward false alarm in every case where the search
found nothing, i.e. in precisely the passing case. Counting result rows instead gave 0.

This is round twelve's lesson recurring within the hour: *a rule named is not a rule modelled* —
say which way a probe errs **before** reading its number. I did not, and the first number was
backwards.

## 5. Pitfalls

- **The lock error names the wrong suspect.** The loser of a two-writer race prints:

  > the index is locked or unreadable — is the GUI mid-index? (SQLite Error 19: 'UNIQUE constraint failed: symbol.id'.)

  The GUI is now the *least* likely cause. With the server registered at user scope, the ordinary
  case is a second Claude Code session on the same repository. The message should say so.
- The failure is **loud and non-destructive**, which is the important half: exit 3, and the
  surviving index measured 600 files / 8,400 symbols / 92.3% resolved — identical to a clean
  re-index afterwards (8,400 symbols, 7,200 links). Nothing was half-written.

## 6. What I would fix, ranked

| # | Fix | Grounds (measured) | Query that measured it |
|---|---|---|---|
| 1 | Reword the lock error to name a second session, not the GUI | 1 of 1 two-writer races produced it; the wording predates multi-session use entirely | the exit-3 output above |
| 2 | Consider making a concurrent `index` wait rather than fail — a SQLite busy timeout is set (30 s default) but the collision is a `UNIQUE constraint`, not a busy lock, so the writes interleaved inside a transaction rather than queueing | 1 of 1 races failed at 1.0 s, well under any timeout | as above |

Neither is ranked against a real corpus. Item 2 in particular should not be built until someone
has actually hit it in normal use — two sessions calling `reindex` on one repo in the same second
may simply never happen.

## 7. Checks

- [x] Nothing in §6 is on the known-limits list. The README documents an *in-process* lock
      ("a query and a write cannot overlap"); cross-process is a different claim and is not made.
- [x] Every number measured this session.
- [x] §1 trimmed deliberately, and said so.
- [x] Languages row from `stats`.
