; Python symbol definitions.
;
; Convention shared by every language pack:
;   @def.<kind>  the node whose span defines the symbol
;   @name        the identifier (falls back to the @def node when absent)
;   @value       verbatim literal initializer
;   @type        verbatim declared type
;   @params      parameter list, used for arity and for the displayed signature
;
; Patterns may overlap: where two claim the same name at the same offset the more
; specific kind wins. That is how the same `def` rule yields a function at file scope
; and a method inside a class, without a "not inside a class" predicate.

(class_definition
  name: (identifier) @name
  superclasses: (argument_list)? @params) @def.class

(function_definition
  name: (identifier) @name
  parameters: (parameters) @params
  return_type: (_)? @type) @def.function

; The same def, seen through its class: more specific, so it wins the position.
(class_definition
  body: (block
    (function_definition
      name: (identifier) @name
      parameters: (parameters) @params
      return_type: (_)? @type) @def.method))

; @staticmethod and friends wrap the definition in a decorated_definition.
(class_definition
  body: (block
    (decorated_definition
      definition: (function_definition
        name: (identifier) @name
        parameters: (parameters) @params
        return_type: (_)? @type) @def.method)))

; Assignments. Python has no declaration keyword, so an assignment is the declaration;
; the container pass is what separates module state from a function local.
(assignment
  left: (identifier) @name
  right: (_) @value) @def.variable

(assignment
  left: (identifier) @name
  type: (type) @type
  right: (_) @value) @def.variable

; SCREAMING_CASE is the language's own convention for a constant, and promoting it is
; what puts the literal on the node in the graph.
(assignment
  left: (identifier) @name
  right: (_) @value
  (#match? @name "^[A-Z][A-Z0-9_]*$")) @def.constant

(assignment
  left: (identifier) @name
  type: (type) @type
  right: (_) @value
  (#match? @name "^[A-Z][A-Z0-9_]*$")) @def.constant

; Instance attributes: `self.channel = channel` is where a Python object's members are
; actually declared.
(assignment
  left: (attribute
    object: (identifier) @receiver
    attribute: (identifier) @name)
  right: (_) @value
  (#eq? @receiver "self")) @def.field
