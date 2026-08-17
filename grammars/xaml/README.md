# tree-sitter-xaml

`tree-sitter-html`, compiled a second time under a different name, with HTML's tag table
switched off. One `#ifdef` in `vendor/src/tag.h` is the entire difference; `parser.c` and
`scanner.c` are byte-identical to upstream and are renamed at compile time by `-D`.

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
test `tag.type`, so forcing `CUSTOM` turns off all three behaviours at once and leaves every
other part of the grammar — including the node names the query pack is written against —
exactly as upstream. That is why `Queries/xaml/` needed no changes.

`.html` files still use the stock grammar, where raw text is correct.

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
