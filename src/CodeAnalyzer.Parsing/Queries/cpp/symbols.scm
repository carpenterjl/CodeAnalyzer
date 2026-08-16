; C++ symbol definitions.
;
; Convention shared by every language pack:
;   @def.<kind>  the node whose span defines the symbol
;   @name        the identifier (falls back to the @def node when absent)
;   @value       verbatim literal initializer
;   @type        verbatim declared type
;   @params      parameter list, used for arity and for the displayed signature
;
; Patterns may overlap: where two of them claim the same name at the same offset the
; more specific kind wins (CaptureNames.Specificity). That is how a constexpr variable
; becomes a constant and an in-class function declaration becomes a method, without
; needing a negative predicate the query language cannot express.

; ---------------------------------------------------------------- shared with C

; Function definitions
(function_definition
  type: (_)? @type
  declarator: (function_declarator
    declarator: (identifier) @name
    parameters: (parameter_list) @params)) @def.function

; Functions returning pointers or references: the declarator nests one level deeper.
(function_definition
  type: (_)? @type
  declarator: (pointer_declarator
    declarator: (function_declarator
      declarator: (identifier) @name
      parameters: (parameter_list) @params))) @def.function

(function_definition
  type: (_)? @type
  declarator: (reference_declarator
    (function_declarator
      declarator: (identifier) @name
      parameters: (parameter_list) @params))) @def.function

; Prototypes, extern declarations and constructor declarations. Recorded so the file
; reads correctly, but not resolution targets: the definition elsewhere is the target.
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

; Data members
(field_declaration
  type: (_) @type
  declarator: (field_identifier) @name) @def.field

(field_declaration
  type: (_) @type
  declarator: (field_identifier) @name
  default_value: (_) @value) @def.field

(field_declaration
  type: (_) @type
  declarator: (pointer_declarator
    declarator: (field_identifier) @name)) @def.field

(field_declaration
  type: (_) @type
  declarator: (array_declarator
    declarator: (field_identifier) @name)) @def.field

; Macros
(preproc_def
  name: (identifier) @name
  value: (preproc_arg)? @value) @def.macro

(preproc_function_def
  name: (identifier) @name
  parameters: (preproc_params) @params) @def.macro

(type_definition
  type: (_)? @type
  declarator: (type_identifier) @name) @def.typedef

; Variables, with and without an initializer.
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

; Parameters, in the same declarator shapes as variables, plus the reference form C
; does not have. Captured for what the declared type buys the resolver: a receiver
; naming a parameter can be typed like one naming a local.
(parameter_declaration
  type: (_) @type
  declarator: (identifier) @name) @def.parameter

(parameter_declaration
  type: (_) @type
  declarator: (pointer_declarator
    declarator: (identifier) @name)) @def.parameter

(parameter_declaration
  type: (_) @type
  declarator: (reference_declarator
    (identifier) @name)) @def.parameter

(parameter_declaration
  type: (_) @type
  declarator: (array_declarator
    declarator: (identifier) @name)) @def.parameter

; ------------------------------------------------------------------- C++ only

(namespace_definition
  name: (namespace_identifier) @name) @def.namespace

; A template class matches here too: template_declaration wraps the class_specifier
; rather than replacing it.
(class_specifier
  name: (type_identifier) @name
  body: (field_declaration_list)) @def.class

; using DeviceList = std::vector<Device>;
(alias_declaration
  name: (type_identifier) @name
  type: (_) @type) @def.typedef

; Member functions declared inside a class or struct body but defined elsewhere. Like a
; C prototype these are not resolution targets: the out-of-line definition below is.
(field_declaration
  type: (_)? @type
  declarator: (function_declarator
    declarator: (field_identifier) @name
    parameters: (parameter_list) @params)) @def.method_prototype

; Constructors declared in a class body. The shared C rule already records these as
; prototypes; being inside a class body is what makes them members.
(field_declaration_list
  (declaration
    declarator: (function_declarator
      declarator: (identifier) @name
      parameters: (parameter_list) @params)) @def.method_prototype)

; Member functions defined inline in the class body — these have a body, so they are
; the definition.
(function_definition
  type: (_)? @type
  declarator: (function_declarator
    declarator: (field_identifier) @name
    parameters: (parameter_list) @params)) @def.method

; Out-of-line member definitions: int Device::Send(const Frame &frame) { … }
(function_definition
  type: (_)? @type
  declarator: (function_declarator
    declarator: (qualified_identifier
      name: (identifier) @name)
    parameters: (parameter_list) @params)) @def.method

; constexpr / const file-scope values, which carry a literal worth showing.
(declaration
  (type_qualifier) @qualifier
  type: (_) @type
  declarator: (init_declarator
    declarator: (identifier) @name
    value: (_) @value)
  (#eq? @qualifier "constexpr")) @def.constant

(declaration
  (type_qualifier) @qualifier
  type: (_) @type
  declarator: (init_declarator
    declarator: (identifier) @name
    value: (_) @value)
  (#eq? @qualifier "const")) @def.constant

; ------------------------------------------------------------ modifier harvest
;
; Per-member facts only: `virtual` is a keyword on the declaration, `override`/`final`
; are virtual_specifier nodes on the declarator. C++ visibility is NOT captured —
; `public:` is a section label, not per-declaration syntax, and deriving each member's
; visibility from the label above it would be inference, which this index never does.

(field_declaration
  "virtual" @modifier
  declarator: (function_declarator
    declarator: (field_identifier) @name
    parameters: (parameter_list) @params)) @def.method_prototype

(field_declaration
  declarator: (function_declarator
    declarator: (field_identifier) @name
    parameters: (parameter_list) @params
    (virtual_specifier) @modifier)) @def.method_prototype

; Inline member definitions carrying override/final, or declared virtual with a body.
(function_definition
  declarator: (function_declarator
    declarator: (field_identifier) @name
    parameters: (parameter_list) @params
    (virtual_specifier) @modifier)) @def.method

(function_definition
  "virtual" @modifier
  declarator: (function_declarator
    declarator: (field_identifier) @name
    parameters: (parameter_list) @params)) @def.method
