; C# symbol definitions.
;
; Convention shared by every language pack:
;   @def.<kind>  the node whose span defines the symbol
;   @name        the identifier (falls back to the @def node when absent)
;   @value       verbatim literal initializer
;   @type        verbatim declared type
;   @params      parameter list, used for arity and for the displayed signature
;
; Patterns may overlap: where two claim the same name at the same offset the more
; specific kind wins, and among equals the one that captured more detail wins. That is
; what turns the broad field rule below into a constant rule for `const` fields, which
; no predicate in the query language can express directly.

(namespace_declaration
  name: (_) @name) @def.namespace

(file_scoped_namespace_declaration
  name: (_) @name) @def.namespace

(class_declaration
  name: (identifier) @name) @def.class

(struct_declaration
  name: (identifier) @name) @def.struct

; Covers both `record` and `record struct`; the grammar uses one node for them.
(record_declaration
  name: (identifier) @name) @def.class

(interface_declaration
  name: (identifier) @name) @def.interface

(enum_declaration
  name: (identifier) @name) @def.enum

(enum_member_declaration
  name: (identifier) @name
  value: (_)? @value) @def.enum_member

; A delegate declares a named type, so it lands with the other type aliases.
(delegate_declaration
  type: (_) @type
  name: (identifier) @name
  parameters: (parameter_list) @params) @def.typedef

; `using Alias = System.Text.StringBuilder;` declares a name. Capturing it as a
; declaration also stops the alias being read as an import of a namespace called
; "Alias" by refs.scm.
(using_directive
  name: (identifier) @name
  (_) @type) @def.typedef

; Methods. Interface members, abstract members and partial declarations have no body,
; so the broad rule records them without making them resolution targets; the second
; rule requires a body and out-ranks the first wherever there is one.
(method_declaration
  returns: (_) @type
  name: (identifier) @name
  parameters: (parameter_list) @params) @def.method_prototype

(method_declaration
  returns: (_) @type
  name: (identifier) @name
  parameters: (parameter_list) @params
  body: (_)) @def.method

(constructor_declaration
  name: (identifier) @name
  parameters: (parameter_list) @params) @def.method

(property_declaration
  type: (_) @type
  name: (identifier) @name) @def.property

(property_declaration
  type: (_) @type
  name: (identifier) @name
  value: (_) @value) @def.property

; Fields. The bare form catches every field; the value form adds the initializer and
; the const form promotes it to a constant, both by out-ranking the bare match.
(field_declaration
  (variable_declaration
    type: (_) @type
    (variable_declarator
      name: (identifier) @name))) @def.field

(field_declaration
  (variable_declaration
    type: (_) @type
    (variable_declarator
      name: (identifier) @name
      (_) @value))) @def.field

(field_declaration
  (modifier) @modifier
  (variable_declaration
    type: (_) @type
    (variable_declarator
      name: (identifier) @name
      (_) @value))
  (#eq? @modifier "const")) @def.constant

(event_field_declaration
  (variable_declaration
    type: (_) @type
    (variable_declarator
      name: (identifier) @name))) @def.field

; Positional record members: `record struct Frame(int Length, byte[] Payload)`.
(record_declaration
  (parameter_list
    (parameter
      type: (_) @type
      name: (identifier) @name) @def.field))

; Parameters, captured for what the declared type buys: a receiver that names a
; parameter can be typed exactly like one naming a local, and a method's inputs are
; searchable beside its locals. Only typed parameters land — an implicit lambda
; parameter carries no type and would add a bare name with nothing to say. The
; positional-record rule above out-ranks this one, so a record's parameters stay
; the fields they declare.
(parameter
  type: (_) @type
  name: (identifier) @name) @def.parameter

; Locals, so a method's own state is searchable and linked to it by container.
(local_declaration_statement
  (variable_declaration
    type: (_) @type
    (variable_declarator
      name: (identifier) @name))) @def.variable

(local_declaration_statement
  (variable_declaration
    type: (_) @type
    (variable_declarator
      name: (identifier) @name
      (_) @value))) @def.variable

; ------------------------------------------------------------ modifier harvest
;
; A declaration with N modifiers arrives as N separate matches, one modifier each; the
; analyzer accumulates every @modifier seen at a name's offset and stamps the joined,
; source-ordered list onto whichever pattern won that position. These patterns exist
; only to feed that accumulation — they restate the base rules (at equal or lower rank,
; so they never change which record wins) with the modifier captured.

(class_declaration
  (modifier) @modifier
  name: (identifier) @name) @def.class

(struct_declaration
  (modifier) @modifier
  name: (identifier) @name) @def.struct

(record_declaration
  (modifier) @modifier
  name: (identifier) @name) @def.class

; The `record` keyword itself. The grammar models it as an anonymous node rather than a
; (modifier), so without this a record and a class are indistinguishable in the index —
; and in C# that difference decides whether `with` expressions and value equality exist.
; The keyword is verbatim source text, so it belongs in the modifiers column beside
; `sealed`, rather than inventing a kind the vocabulary does not have.
(record_declaration
  "record" @modifier
  name: (identifier) @name) @def.class

; `record struct` is a value type, and the second keyword is the only thing that says so.
(record_declaration
  "struct" @modifier
  name: (identifier) @name) @def.class

(interface_declaration
  (modifier) @modifier
  name: (identifier) @name) @def.interface

(enum_declaration
  (modifier) @modifier
  name: (identifier) @name) @def.enum

(delegate_declaration
  (modifier) @modifier
  type: (_) @type
  name: (identifier) @name
  parameters: (parameter_list) @params) @def.typedef

; The prototype kind, so a bodiless harvest match can never out-rank the bodied rule.
(method_declaration
  (modifier) @modifier
  returns: (_) @type
  name: (identifier) @name
  parameters: (parameter_list) @params) @def.method_prototype

(constructor_declaration
  (modifier) @modifier
  name: (identifier) @name
  parameters: (parameter_list) @params) @def.method

(property_declaration
  (modifier) @modifier
  type: (_) @type
  name: (identifier) @name) @def.property

(field_declaration
  (modifier) @modifier
  (variable_declaration
    type: (_) @type
    (variable_declarator
      name: (identifier) @name))) @def.field

(event_field_declaration
  (modifier) @modifier
  (variable_declaration
    type: (_) @type
    (variable_declarator
      name: (identifier) @name))) @def.field
