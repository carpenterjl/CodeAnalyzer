; HTML references.
;
; Convention:
;   @ref.<kind>  the reference node
;   @name        the identifier being referenced (falls back to the @ref node)
;
; A page's dependencies are the resources it names: script src, stylesheet href, image
; src, links to other pages. They are recorded exactly as written and matched against
; workspace files during resolution, so an absolute URL simply stays unresolved rather
; than being turned into a claim about a file that is not there.

(script_element
  (start_tag
    (attribute
      (attribute_name) @attribute
      (quoted_attribute_value
        (attribute_value) @name)))
  (#eq? @attribute "src")) @ref.import

(element
  (start_tag
    (attribute
      (attribute_name) @attribute
      (quoted_attribute_value
        (attribute_value) @name)))
  (#eq? @attribute "src")) @ref.import

(element
  (start_tag
    (attribute
      (attribute_name) @attribute
      (quoted_attribute_value
        (attribute_value) @name)))
  (#eq? @attribute "href")) @ref.import

(element
  (self_closing_tag
    (attribute
      (attribute_name) @attribute
      (quoted_attribute_value
        (attribute_value) @name)))
  (#eq? @attribute "src")) @ref.import

(element
  (self_closing_tag
    (attribute
      (attribute_name) @attribute
      (quoted_attribute_value
        (attribute_value) @name)))
  (#eq? @attribute "href")) @ref.import
