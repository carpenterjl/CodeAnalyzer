# Field reports

Twelve rounds of this tool were ranked from **one corpus** — its own repository. That corpus
is 208 files of C#, XAML and vendored JavaScript, and every number that has ever set the work
list came from it. A resolver rule that scores well there may be scoring well on C# habits, not
on resolution.

This folder is where sessions on *other* codebases file what they saw, so a rule can be judged
against a corpus that was not used to write it.

## Starting a session that will file one

Most of it is already standing instruction — `~/.claude/CLAUDE.md` tells every session on every
project to prefer the tool for structural questions and to write surprises down as they happen.
Paste this only when you want a session to *deliberately* dogfood:

> Use the `codeanalyzer` MCP tool for structural questions here rather than grep — who calls
> this, what does this file define, where else is this value. Start with `stats`; if it says
> there is no index, call `reindex` first. Keep a running note as you go of anything it got
> wrong, anything you had to grep for anyway and why, and anything that surprised you. At the
> end, run `/codeanalyzer-field-report`.

### Verifying it is connected

Three outcomes, and the middle one is success rather than failure:

| What you see | Means |
|---|---|
| No `mcp__codeanalyzer__*` tools at all | Not connected. Check `claude mcp list` from a shell. |
| `no index for <path> — … call the reindex tool to build it` | **Connected.** This workspace has never been indexed. Call `reindex`. |
| `[index: <path> (N definitions, built …, M indexed files unchanged on disk)]` | Connected, indexed, and the header names the root it is answering about. |

That header is the whole verification: it proves the server is up, that it resolved the right
working directory, and how far the index has drifted from disk since it was built. Every command
and every tool prints it.

From a shell, in any directory:

```bash
claude mcp list
```

`claude mcp get codeanalyzer` says which scope won, which is what to check if a session seems to
be running the wrong copy.

### Which build you are reporting on

```bash
codeanalyzer version
```

One line: the commit the binary was built from, the index schema version it writes, and when
it was built. Paste it verbatim into the report's **Tool build** row.

This exists because it did not. Three consecutive reports carried `Tool build | schema v26`
while the schema was 27, then 29, then 30 — not from carelessness but because no command
would state it, so each session copied the row from the last report it could find. A row a
reader cannot obtain is a row they will inherit. The same line rides at the foot of `stats`,
since reports paste that block whole.

## Filing one

Sessions have the `/codeanalyzer-field-report` skill installed at user level. It carries the
template and the rules; invoke it at the end of a session that used the tool. By hand:

1. Copy [TEMPLATE.md](TEMPLATE.md).
2. Save as `reports/YYYY-MM-DD-<repo-slug>.md`.
3. Paste the **whole** `stats` block. It is the single most valuable thing in the report —
   per-corpus resolution and the refusal partition exist nowhere else.

## The rules a report is held to

These are not style notes. They are the specific ways twelve rounds of this project produced
work that measurement then retired, each one written the round it cost something:

| Rule | Comes from |
|---|---|
| **Measure before you rank.** A grounds cell is a claim; run the query. | Round twelve retired 3 of 5 ranked items this way — one yielded **0**. |
| **A rule named is not a rule modelled.** When a probe stands in for a rule, say which way it errs *before* reading its number. | A probe using `EXISTS` where the resolver wants agreement invented 23 phantom gaps. |
| **An absence is a measurement, not an observation.** "Nothing uses X" needs the query that found nothing. | Round eight. |
| **Headroom is a claim.** "This could fix ~N" needs N counted, not estimated. | Round nine. |
| **"Inherent" is a verdict you inherit and must re-earn.** | Round ten. |
| **Name the rule that refused it.** Residue is not one thing. | Round eleven. |

And one that only applies here: **do not file a known limit as a finding.** The list is in the
[README's Known limits](../README.md#known-limits) section. The ones field reports keep
rediscovering:

- **~53 C# files "with syntax errors"** — the bundled grammar predates C# 12 `[]`. They all
  compile and all index. `codeanalyzer errors` leads with the tally that says so.
- **No TypeScript, JSX, Go, Rust, Java or Ruby.** Indexed extensions are exactly `.c .h .cpp
  .cxx .cc .c++ .hpp .hxx .hh .h++ .ipp .inl .cs .py .pyi .pyw .v .vh .sv .svh .vlg .html .htm
  .xhtml .js .mjs .cjs .xaml`. A TS repo indexing nothing is the registry, not a bug — though
  *how much of a repo went unseen* is worth reporting.
- **`*.min.js` is skipped on purpose.** Minified names carry no meaning.
- **Unresolved is not a defect count.** A workspace leaning on external libraries is *supposed*
  to have a large external share. `stats` says which part is external.
- **A shared value is evidence, not proof.** `find_by_value` matches literals, and most matches
  on a vendor-header-heavy tree are coincidence.

## Reviewing them

A finding earns a place on the work list when it reproduces on a corpus that did not generate
it. Two reports naming the same rule from different languages outrank a large number from this
repo alone — that is the entire reason this folder exists.
