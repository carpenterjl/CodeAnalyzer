; HTML symbol definitions.
;
; Convention shared by every language pack:
;   @def.<kind>  the node whose span defines the symbol
;   @name        the identifier (falls back to the @def node when absent)
;   @type        verbatim declared type
;
; Markup has one thing that behaves like a declaration: an element carrying an id.
; That is the name the rest of a codebase addresses it by, in a stylesheet selector or
; a getElementById call, so it is what makes an HTML file searchable. Tag names and
; classes are not declarations — many elements share them — and are left alone.

(element
  (start_tag
    (tag_name) @type
    (attribute
      (attribute_name) @attribute
      (quoted_attribute_value
        (attribute_value) @name)))
  (#eq? @attribute "id")) @def.markup_element

(element
  (self_closing_tag
    (tag_name) @type
    (attribute
      (attribute_name) @attribute
      (quoted_attribute_value
        (attribute_value) @name)))
  (#eq? @attribute "id")) @def.markup_element

(script_element
  (start_tag
    (tag_name) @type
    (attribute
      (attribute_name) @attribute
      (quoted_attribute_value
        (attribute_value) @name)))
  (#eq? @attribute "id")) @def.markup_element
