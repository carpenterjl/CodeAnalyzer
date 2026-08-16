; JavaScript symbol definitions.
;
; Convention shared by every language pack:
;   @def.<kind>  the node whose span defines the symbol
;   @name        the identifier (falls back to the @def node when absent)
;   @value       verbatim literal initializer
;   @type        verbatim declared type
;   @params      parameter list, used for arity and for the displayed signature
;
; JavaScript has no keyword separating a function from a variable that holds one, so these
; patterns overlap on purpose: a broad variable_declarator rule claims every declaration
; and the function-valued variants out-rank it at the same position. That is the same
; mechanism the C# pack uses to turn a plain field into a constant.
;
; There is no @type anywhere here. JavaScript declarations state no types, and deriving
; one from an initializer would be inference rather than a fact from the source.

; ------------------------------------------------------------------ functions

(function_declaration
  name: (identifier) @name
  parameters: (formal_parameters) @params) @def.function

(generator_function_declaration
  name: (identifier) @name
  parameters: (formal_parameters) @params) @def.function

; `var render = function (a) {…}` and `const add = (a, b) => a + b` declare a function as
; surely as the keyword form does, and the first of those is the dominant idiom in this
; repo's own page bundle.
(variable_declarator
  name: (identifier) @name
  value: (function_expression
    parameters: (formal_parameters) @params)) @def.function

(variable_declarator
  name: (identifier) @name
  value: (arrow_function
    parameters: (formal_parameters) @params)) @def.function

; `window.thing = function () {…}` — the assignment is the declaration, and the property
; is the name the rest of the code calls it by.
(assignment_expression
  left: (member_expression
    property: (property_identifier) @name)
  right: (function_expression
    parameters: (formal_parameters) @params)) @def.function

(assignment_expression
  left: (member_expression
    property: (property_identifier) @name)
  right: (arrow_function
    parameters: (formal_parameters) @params)) @def.function

; ------------------------------------------------------------------ classes

(class_declaration
  name: (identifier) @name) @def.class

; A class member and an object literal's shorthand method are the same node; the container
; pass is what tells them apart, so one bare pattern covers both. Getters, setters and the
; constructor come through here too — all of them are methods in the source's own words.
(method_definition
  name: (property_identifier) @name
  parameters: (formal_parameters) @params) @def.method

(field_definition
  property: (property_identifier) @name
  value: (_)? @value) @def.field

; ------------------------------------------------------------------ variables

(variable_declarator
  name: (identifier) @name
  value: (_)? @value) @def.variable

; SCREAMING_CASE is the language's own convention for a constant, and promoting it is what
; puts the literal on the graph node — and into the cross-language value index, where a
; command byte written 0xA5 in the page can meet the 165 in the firmware.
(variable_declarator
  name: (identifier) @name
  value: (_) @value
  (#match? @name "^[A-Z][A-Z0-9_]*$")) @def.constant

; ------------------------------------------------------------- object members
;
; An object literal is JavaScript's record, its namespace and its configuration blob all at
; once, and the syntax cannot tell those apart. A function-valued key is the module's
; behaviour and earns a method; anything else is recorded as a field carrying its verbatim
; value. Keys written as strings are deliberately not captured — `"background-color": …` in
; a style sheet object is data, not a declaration of anything.

(pair
  key: (property_identifier) @name
  value: (function_expression
    parameters: (formal_parameters) @params)) @def.method

(pair
  key: (property_identifier) @name
  value: (arrow_function
    parameters: (formal_parameters) @params)) @def.method

(pair
  key: (property_identifier) @name
  value: (_) @value) @def.field
