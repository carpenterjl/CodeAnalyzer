; XAML references.
;
; Convention:
;   @ref.<kind>  the reference node
;   @name        the identifier being referenced (falls back to the @ref node)
;
; One attribute in a XAML file names something in another language unambiguously:
; x:Class, which names the code-behind type this markup is compiled into. It is recorded
; verbatim, exactly as written.
;
; It does NOT currently resolve to that type, and the honest thing is to say so here
; rather than let the rule read like a working link. Two separate reasons, both checked
; against the real repo rather than assumed:
;
;   - The value is fully qualified (CodeAnalyzer.App.Views.MainWindow) while the C# class
;     is defined under its short name. Resolution matches a reference name against a
;     definition name whole, so this one matches nothing. That is the same position a C#
;     `using` is in — it names something the index cannot turn into a target — and it is
;     handled the same way: recorded, unmatched, never guessed at.
;   - x:Class sits on the root element, which carries no x:Name and is therefore not a
;     symbol. The reference has no enclosing definition to be attributed to, so it does
;     not appear in a caller list either.
;
; It is kept because it is true and cheap, and because the two things standing between it
; and a real markup-to-code-behind edge are both nameable pieces of work rather than
; unknowns. It is not kept because it currently does anything.
;
; Nothing else here is a reference. TargetType="Button" names a type but so does every
; other bare word in an attribute, and there is no syntax separating them; a markup
; extension such as "{Binding SearchQuery}" is one opaque attribute_value token to this
; grammar. Both are stored verbatim and neither is turned into a claim.
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
