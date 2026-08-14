; C symbol definitions.
;
; Convention shared by every language pack:
;   @def.<kind>  the node whose span defines the symbol
;   @name        the identifier (falls back to the @def node when absent)
;   @value       verbatim literal initializer
;   @type        verbatim declared type
;   @params      parameter list, used for arity and for the displayed signature

; Function definitions
(function_definition
  type: (_)? @type
  declarator: (function_declarator
    declarator: (identifier) @name
    parameters: (parameter_list) @params)) @def.function

; Functions returning pointers: the declarator is wrapped one level deeper.
(function_definition
  type: (_)? @type
  declarator: (pointer_declarator
    declarator: (function_declarator
      declarator: (identifier) @name
      parameters: (parameter_list) @params))) @def.function

; Prototypes and extern declarations. Recorded but not resolution targets.
(declaration
  type: (_)? @type
  declarator: (function_declarator
    declarator: (identifier) @name
    parameters: (parameter_list) @params)) @def.prototype

; Aggregates
(struct_specifier
  name: (type_identifier) @name
  body: (field_declaration_list)) @def.struct

(union_specifier
  name: (type_identifier) @name
  body: (field_declaration_list)) @def.union

(enum_specifier
  name: (type_identifier) @name
  body: (enumerator_list)) @def.enum

(enumerator
  name: (identifier) @name
  value: (_)? @value) @def.enum_member

; Struct and union members
(field_declaration
  type: (_) @type
  declarator: (field_identifier) @name) @def.field

(field_declaration
  type: (_) @type
  declarator: (pointer_declarator
    declarator: (field_identifier) @name)) @def.field

(field_declaration
  type: (_) @type
  declarator: (array_declarator
    declarator: (field_identifier) @name)) @def.field

; Macros. The value capture is what lets the UI show a constant's literal text.
(preproc_def
  name: (identifier) @name
  value: (preproc_arg)? @value) @def.macro

(preproc_function_def
  name: (identifier) @name
  parameters: (preproc_params) @params) @def.macro

; Typedefs
(type_definition
  type: (_)? @type
  declarator: (type_identifier) @name) @def.typedef

; Variables, with and without an initializer. Locals are captured too; the
; container pass marks which ones live inside a function.
(declaration
  type: (_) @type
  declarator: (init_declarator
    declarator: (identifier) @name
    value: (_) @value)) @def.variable

(declaration
  type: (_) @type
  declarator: (identifier) @name) @def.variable

(declaration
  type: (_) @type
  declarator: (pointer_declarator
    declarator: (identifier) @name)) @def.variable

(declaration
  type: (_) @type
  declarator: (array_declarator
    declarator: (identifier) @name)) @def.variable
