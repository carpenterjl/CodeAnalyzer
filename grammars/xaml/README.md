# tree-sitter-xaml

`tree-sitter-html`, compiled a second time under a different name, with HTML's tag table
switched off and three XML constructs added. Four `#ifdef TREE_SITTER_XAML` blocks — one in
`vendor/src/tag.h` and three in `vendor/src/scanner.c` — are the entire difference; nothing
is edited outside them, `parser.c` is byte-identical to upstream, and the exports are
renamed at compile time by `-D`. **No generated table changes**, which is why this needs
neither node nor the tree-sitter CLI.

| | |
|---|---|
| Upstream | [tree-sitter/tree-sitter-html](https://github.com/tree-sitter/tree-sitter-html) |
| Commit | `73a3947324f6efddf9e17c0ea58d454843590cc0` (2025-09-15) |
| Licence | MIT — `vendor/LICENSE` |
| Built by | `grammars/build-xaml-grammar.ps1` (needs only a 64-bit gcc) |
| Output | `lib/tree-sitter-xaml.dll`, exporting `tree_sitter_xaml` at ABI 15 |

## Why

XAML had been read by the stock HTML grammar since the pack was written. The two languages
do agree about elements, attributes, quoted values and self-closing tags — but HTML also
carries a table of about 130 tag names with special parsing behaviour, and XAML's tag names
collide with it case-insensitively.

The one that cost something is **`<Style>`, which HTML treats as a raw-text element**: the
parser stops reading markup at the start tag and resumes at the matching end tag, because in
HTML the content is CSS. In WPF the content is markup, and every declaration written inside a
`<Style>` was discarded — **32 of them in this repo's own `Themes/Controls.xaml`**, every
`x:Name` on a template part.

It was discarded *silently*. Not one of those files was reported as an imperfect parse for
that reason. What the reader saw instead was an error attributed to `.Triggers`, and for
several rounds the reports here said the property element was unreadable. It is not:
`</Style.Triggers>` begins with the characters `</Style`, which is what the raw-text scanner
watches for, so the swallowed region ends at the wrong tag and *that* is what produced the
error. The property element was the messenger.

Two more table-driven behaviours are wrong for XAML and were switched off with the same line:

- **void elements** — HTML's `<br>` and `<img>` never close. XAML has no void elements.
- **implicit closing** — HTML lets `<p>` be closed by the next block tag. XAML does not, and
  `<Button>`, `<Label>`, `<Menu>` and `<Style>` all hit rows in that table.

## What XAML has that HTML does not

Three constructs, all added in round seventeen after the tag table had been off for three
rounds. Each was reproduced on a one-file probe before it was written, and the probe files
are the tests in `XamlAnalyzerTests`.

| Construct | What HTML did | What it cost |
|---|---|---|
| `<Grid.RowDefinitions>` — a property element | tag names take letters, digits, `-` and `:`, so the `.` ended the tag and raised an error | **nothing but the alarm.** Same file with and without one indexes the same declarations, and 303 of this repo's 661 XAML references already sat inside one |
| `<?xml version="1.0"?>` — the XML prologue | no rule for a processing instruction | the alarm, on the first line |
| `<![CDATA[…]]>` | no rule for a CDATA section | **a declaration.** The element holding the section failed to parse and took its own `x:Key` with it — 2 declared, 1 indexed |

The last is the one that mattered, and it is the same shape as the `<Style>` swallow three
rounds earlier: data lost quietly, with an error pointing at the wrong thing.

## The whole diff

```diff
 static inline Tag tag_for_name(String name) {
     Tag tag = tag_new();
+#ifdef TREE_SITTER_XAML
+    tag.type = CUSTOM;
+#else
     tag.type = tag_type_for_name(&name);
+#endif
     if (tag.type == CUSTOM) {
```

`tag_is_void`, `tag_can_contain` and the `SCRIPT`/`STYLE` arms of `scan_start_tag_name` all
test `tag.type`, so forcing `CUSTOM` turns off raw text, void elements and implicit closing
at once, all three of which are wrong for XAML.

That has one consequence worth knowing: `style_element` is built from a token only a `STYLE`
tag type can produce, so **that node can never appear in a XAML tree**. `Queries/xaml/` used
to carry two patterns for it; round seventeen deleted them as dead and checked the count did
not move.

The other three blocks are in `scanner.c`: `.` joins the tag-name character set, and `<?…?>`
and `<![CDATA[…]]>` are consumed and reported as `COMMENT`, which `grammar.js` declares as an
extra and therefore admits anywhere. Reporting an existing token is what keeps the generated
parser untouched.

`.html` files still use the stock grammar, where raw text is correct and none of this applies.

## Rebuilding

```powershell
powershell -ExecutionPolicy Bypass -File grammars\build-xaml-grammar.ps1
```

Then rebuild the solution (`Directory.Build.targets` copies the DLL into every output) and
re-index with `--full`: an incremental refresh reads file timestamps and cannot see that the
analyzer now reads them differently.

A 32-bit gcc compiles this without complaint and produces a DLL .NET cannot load at all
(`%1 is not a valid Win32 application`), so the script checks `-dumpmachine` rather than
trusting whatever `gcc` is on PATH.

## If the vendored source is ever missing

The build falls back to nothing — `Language("xaml")` cannot resolve `tree-sitter-xaml.dll`
and XAML files fail to parse rather than parsing wrongly. The test
`ADeclarationInsideAStyleElementSurvives` is what fails first, and that is its whole job.
