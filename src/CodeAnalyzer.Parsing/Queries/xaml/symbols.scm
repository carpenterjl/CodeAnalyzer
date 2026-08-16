; XAML symbol definitions.
;
; Convention shared by every language pack:
;   @def.<kind>  the node whose span defines the symbol
;   @name        the identifier (falls back to the @def node when absent)
;   @type        verbatim declared type
;
; XAML is read with the HTML grammar — there is no XAML or XML grammar in the bundle.
; The two languages agree on elements, attributes, quoted values and self-closing tags,
; which is everything these patterns touch. Verified against this repo's own .xaml files
; before this pack was written, rather than assumed.
;
; What counts as a declaration, exactly as the HTML pack argues for id: the name the rest
; of the codebase addresses the element by. In XAML that is three attributes and no
; others:
;   x:Name   the generated code-behind field, and what FindName looks up
;   Name     the same thing spelled the other way on a FrameworkElement
;   x:Key    how a resource is addressed by StaticResource and DynamicResource
; Tag names are not declarations — a file has thirty Buttons — and neither is any other
; attribute; Title="CodeAnalyzer" names nothing.
;
; The element's tag rides along as @type, so a result reads "SearchBox TextBox" and says
; what the named thing actually is.
;
; Known limits, all of them omissions rather than inventions:
;   - A <Style> element is HTML's CSS <style>, so its children arrive as one raw_text
;     blob and Setters and Triggers inside it are invisible. The Style's own x:Key is
;     captured, which is the part anything else refers to.
;   - A property element (<Grid.RowDefinitions>) is parsed as a tag plus an error. Names
;     nested inside one are still found; the file is flagged as having a parse error, and
;     GrammarNotes is what stops that being reported as the author's mistake.
;   - A markup extension is stored verbatim as the attribute's value — "{Binding Path}"
;     is one opaque token to this grammar, so nothing here pretends to have read inside
;     it. Splitting those into real references is a separate piece of work.

(element
  (start_tag
    (tag_name) @type
    (attribute
      (attribute_name) @attribute
      (quoted_attribute_value
        (attribute_value) @name)))
  (#match? @attribute "^(x:Name|Name|x:Key)$")) @def.markup_element

(element
  (self_closing_tag
    (tag_name) @type
    (attribute
      (attribute_name) @attribute
      (quoted_attribute_value
        (attribute_value) @name)))
  (#match? @attribute "^(x:Name|Name|x:Key)$")) @def.markup_element

; A <Style x:Key="…"> lands here rather than in the rules above: the grammar routes it to
; style_element because HTML's <style> is CSS. Its key is still the name a StaticResource
; reference is written against, so it is still a declaration.
(style_element
  (start_tag
    (tag_name) @type
    (attribute
      (attribute_name) @attribute
      (quoted_attribute_value
        (attribute_value) @name)))
  (#match? @attribute "^(x:Name|Name|x:Key)$")) @def.markup_element
