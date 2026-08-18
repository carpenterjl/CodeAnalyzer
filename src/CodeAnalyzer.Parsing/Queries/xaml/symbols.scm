; XAML symbol definitions.
;
; Convention shared by every language pack:
;   @def.<kind>  the node whose span defines the symbol
;   @name        the identifier (falls back to the @def node when absent)
;   @type        verbatim declared type
;
; XAML is read with a grammar derived from HTML's — there is no XAML or XML grammar in the
; bundle. The two languages agree on elements, attributes, quoted values and self-closing
; tags, which is everything these patterns touch. Verified against this repo's own .xaml
; files before this pack was written, rather than assumed. Where they disagree, the
; divergence lives in grammars/xaml (four #ifdefs, all documented in its README) rather
; than in these patterns, which is why this pack has never needed a change for it.
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
; The first two are one kind and the third is another, because they are two namespaces
; rather than two spellings of one. x:Name puts a field on the generated code-behind
; class; x:Key puts an entry in a resource dictionary, and nothing looks a resource up by
; element name or an element up by key. While both were markup_element, this repo's own
; Style="{StaticResource SearchBox}" — written on the <TextBox x:Name="SearchBox"> that
; uses it — resolved to the TextBox rather than to the Style two files away, and because
; the reference and the target were then the same symbol the false edge was a self-edge
; and never appeared in a listing at all.
;
; The element's tag rides along as @type, so a result reads "SearchBox TextBox" and says
; what the named thing actually is.
;
; Known limits, all of them omissions rather than inventions:
;   - A markup extension is still one opaque token to this grammar, but it is no longer
;     only stored: refs.scm flags it and MarkupExtensionPath reads the binding path or
;     resource key out of it (M19.3). Extensions the parser cannot read with certainty
;     remain verbatim values making no claim.

(element
  (start_tag
    (tag_name) @type
    (attribute
      (attribute_name) @attribute
      (quoted_attribute_value
        (attribute_value) @name)))
  (#match? @attribute "^(x:Name|Name)$")) @def.markup_element

(element
  (self_closing_tag
    (tag_name) @type
    (attribute
      (attribute_name) @attribute
      (quoted_attribute_value
        (attribute_value) @name)))
  (#match? @attribute "^(x:Name|Name)$")) @def.markup_element

(element
  (start_tag
    (tag_name) @type
    (attribute
      (attribute_name) @attribute
      (quoted_attribute_value
        (attribute_value) @name)))
  (#eq? @attribute "x:Key")) @def.resource_key

(element
  (self_closing_tag
    (tag_name) @type
    (attribute
      (attribute_name) @attribute
      (quoted_attribute_value
        (attribute_value) @name)))
  (#eq? @attribute "x:Key")) @def.resource_key

; The root element of a compiled file is a declaration too: x:Class says, verbatim, which
; class this markup is part of, and that qualified string is the name the element is
; recorded under. Deliberately the full dotted form, not the last segment — a markup
; element and a C# class under the same short name would make every exact lookup of the
; class ambiguous again, which is the regression the round-three template-folder deletion
; existed to fix. The x:Class *reference* (see refs.scm) is what carries the short name,
; and it anchors on the last segment so the two never collide. The element's tag rides
; along as @type, same as above, so the result reads "….MainWindow Window".
(element
  (start_tag
    (tag_name) @type
    (attribute
      (attribute_name) @attribute
      (quoted_attribute_value
        (attribute_value) @name)))
  (#eq? @attribute "x:Class")) @def.markup_element

(element
  (self_closing_tag
    (tag_name) @type
    (attribute
      (attribute_name) @attribute
      (quoted_attribute_value
        (attribute_value) @name)))
  (#eq? @attribute "x:Class")) @def.markup_element

; A <Style x:Key="…"> once needed two patterns of its own, because HTML routes <style> to
; style_element and reads its body as CSS. The XAML grammar forces every tag to CUSTOM, and
; only a SCRIPT or STYLE tag type can make the scanner emit the token style_element is built
; from — so that node can no longer appear in a XAML tree and the patterns that matched it
; were dead. A <Style> is now an ordinary element and the rules above claim its key.
; Removing them was checked, not assumed: 214 XAML definitions before and after.
