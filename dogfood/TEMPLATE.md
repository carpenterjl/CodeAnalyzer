# Field report — <repo name>

| | |
|---|---|
| **Date** | YYYY-MM-DD |
| **Repo** | name, and what it is in one line |
| **Languages** | as `stats` reports them, not as you assume |
| **Size** | N files indexed / N symbols / N links |
| **Tool build** | the one line `codeanalyzer version` prints — commit, index schema version, binary build time. Paste it verbatim; do not carry it from an earlier report, which is how three consecutive rounds filed `schema v26` against a schema that was 27, 29 and 30. |
| **What the session was actually doing** | the real task — a report from a session that only poked at the tool is worth less than one from a session that needed it |

## 1. `stats`, verbatim

Paste the whole block. Do not summarise it, do not trim rows that read 0 — a rule printing
zero is evidence the partition is exhaustive on this corpus, which is exactly what a foreign
corpus is here to test.

```
<paste>
```

If `reindex` was needed first, say how long it took and how many files it refused.

## 2. What you asked it

One row per real question. "Right?" is about the answer, not the tool's confidence in it.

| Question | Tool | Right? | Notes |
|---|---|---|---|
| | | | |

## 3. Where you fell back to grep — and why

The interesting column is **why**. A fallback because the tool cannot answer that shape of
question is a feature request; a fallback because you forgot the tool could is a discoverability
bug; a fallback because the answer looked wrong is the most valuable row in the report.

| Wanted | Fell back to | Why the tool could not / did not |
|---|---|---|
| | | |

## 4. Wrong answers

Separate from pitfalls, because a confidently wrong edge costs more than a confusing one. For
each: the reference, what it resolved to, what it should have been, and the confidence it wore.

- 

*None found* is a real and useful entry — say so explicitly rather than deleting the section.

## 5. Pitfalls

What bit you: output you misread, a flag you expected, a name you guessed wrong, a step you had
to repeat. Include your own mistakes — this project's most useful findings have consistently
been the ones where the tool was right and the reader was not.

- 

## 6. What I would fix, ranked

Ranked by measured payoff. **Every grounds cell is a number you ran**, not an estimate — and if
a number came from a probe standing in for one of the tool's rules, say which way the probe
errs before you give the number.

| # | Fix | Grounds (measured) | Query that measured it |
|---|---|---|---|
| 1 | | | |

## 7. Checks

- [ ] Nothing in §6 is on the [known-limits list](../README.md#known-limits).
- [ ] Every number in §6 was measured this session, not carried from another report.
- [ ] `stats` in §1 is the whole block.
- [ ] The languages row came from `stats`, not from what the repo looks like.
