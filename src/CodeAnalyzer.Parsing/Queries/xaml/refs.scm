; XAML references.
;
; Convention:
;   @ref.<kind>  the reference node
;   @name        the identifier being referenced (falls back to the @ref node)
;
; One attribute in a XAML file names something in another language unambiguously:
; x:Class, which names the code-behind type this markup is compiled into.
;
; It resolves (M19.2). The two things that used to stand between it and a real edge —
; both named in this header when they were still true — each fell to a stored fact:
;
;   - The value is fully qualified (CodeAnalyzer.App.Views.MainWindow) while the C# class
;     is defined under its short name, and resolution matches names whole. A dotted type
;     reference is now split at its last dot by the analyzer: the segment is the name a
;     definition can match, the qualifier is recorded as the reference's receiver — where
;     it means what a receiver means everywhere else, that the locating was done in the
;     source rather than by the file the reference sits in.
;   - x:Class sits on the root element, which carries no x:Name and was therefore not a
;     symbol, leaving the reference with no owner. The root element is now a declaration
;     in its own right (see symbols.scm), named by the same verbatim qualified string, and
;     it owns this reference.
;
; The edge lands as a cross-language name match and wears the ? marker, which is the
; honest confidence: the index never checked a namespace against a folder.
;
; A markup extension is a reference too (M19.3). The value is still one opaque token to
; this grammar — the brace test below is all a pattern can do — so the pattern's whole
; job is to say "this value is an extension" and hand it to MarkupExtensionPath, which
; reads the binding path or resource key out of it, or refuses. {Binding SearchQuery}
; becomes a binding reference named SearchQuery; a value the parser is not certain of
; stays a verbatim attribute making no claim.
;
(attribute
  (quoted_attribute_value
    (attribute_value) @name)
  (#match? @name "^[{]")) @ref.binding

; An event handler is a reference too (M20.5), and it is the one place this pack guesses.
; XAML marks an event attribute with no syntax whatever: Click="OnAccept" and
; TargetType="Button" are the same shape, and only the type system the index does not have
; tells them apart. So the rule here is a convention — an attribute whose name reads like a
; WPF routed event, whose value is a bare identifier — and it nominates rather than decides.
; What makes nominating safe is the other end: a handler reference may resolve only to a
; method on this file's own x:Class, so IsExpanded="True" finds nothing, claims nothing, and
; is not reported as unresolved either. A misspelled handler is likewise silent here, which
; is the deliberate trade — the compiler already fails on those, so the cost of catching
; them is not worth an unresolved list full of attribute values.
;
; The suffix list is the standard routed-event vocabulary, not a guess at one: Click and
; DoubleClick, the Changed family, mouse and key Down/Up/Enter/Leave/Move/Wheel, the
; lifecycle pairs Loaded/Unloaded, Opened/Opening, Closed/Closing, Expanded/Collapsed,
; Activated/Deactivated, Checked, GotFocus/LostFocus, drag and drop, and the async-ish
; Started/Completed/Invoked.
(attribute
  (attribute_name) @attribute
  (quoted_attribute_value
    (attribute_value) @name)
  (#match? @attribute "(Click|Changed|Down|Up|Enter|Leave|Move|Wheel|Loaded|Unloaded|Opened|Opening|Closed|Closing|Focus|Checked|Expanded|Collapsed|Activated|Deactivated|Initialized|Drop|Drag|Scroll|Sorting|Started|Completed|Invoked|Toggled)$")
  (#match? @name "^[A-Za-z_][A-Za-z0-9_]*$")) @ref.handler

; Nothing else here is a reference. TargetType="Button" names a type but so does every
; other bare word in an attribute, and there is no syntax separating them — a bare word
; in an attribute value stays stored, not claimed.
;
(element
  (start_tag
    (attribute
      (attribute_name) @attribute
      (quoted_attribute_value
        (attribute_value) @name)))
  (#eq? @attribute "x:Class")) @ref.type

(element
  (self_closing_tag
    (attribute
      (attribute_name) @attribute
      (quoted_attribute_value
        (attribute_value) @name)))
  (#eq? @attribute "x:Class")) @ref.type
