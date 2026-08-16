; C++ references.
;
; Convention:
;   @ref.<kind>  the reference node
;   @name        the identifier being referenced (falls back to the @ref node)
;   @args        argument list, used to record call arity
;
; The analyzer drops any reference whose position coincides with a declaration's
; own name, so declaring a symbol never counts as referencing it. Where two patterns
; land on the same position the more specific kind wins, so a call site never also
; appears as a bare identifier use.

; ---------------------------------------------------------------- shared with C

(call_expression
  function: (identifier) @name
  arguments: (argument_list) @args) @ref.call

; The receiver is recorded verbatim — dev.send() carries `dev`, this->send() carries
; `this` — so the resolver can tell a spelled-out local dispatch from a call whose
; target lives wherever the receiver's type does.
(call_expression
  function: (field_expression
    argument: (_) @receiver
    field: (field_identifier) @name)
  arguments: (argument_list) @args) @ref.call

(preproc_include path: (string_literal) @name) @ref.include
(preproc_include path: (system_lib_string) @name) @ref.include

(type_identifier) @ref.type
(sized_type_specifier) @ref.type

(identifier) @ref.use

(field_expression
  argument: (_) @receiver
  field: (field_identifier) @name) @ref.use

; ------------------------------------------------------------------- C++ only

; Base classes and implemented interfaces.
(base_class_clause (type_identifier) @name) @ref.inherit

; Device::Send(…) and std::move(…). The scope is recorded as the receiver: an
; explicitly qualified call names its home, so a same-file match proves no more here
; than it does for dev.send() — the qualifier does the locating, not the file.
(call_expression
  function: (qualified_identifier
    scope: (_) @receiver
    name: (identifier) @name)
  arguments: (argument_list) @args) @ref.call

; The name half of a qualified id is a use of whatever it names: State::Idle reaches
; the enumerator.
(qualified_identifier name: (identifier) @name) @ref.use

; The scope half names a type or namespace, which is how an out-of-line definition
; links back to its class.
(qualified_identifier scope: (namespace_identifier) @name) @ref.type

; std::vector<Device> — both the template and its arguments are type references, and
; the argument is picked up by the plain (type_identifier) rule above.
(template_type name: (type_identifier) @name) @ref.type

; new Frame(…) depends on the type, not on a function of that name, so it is recorded
; as a type reference — that is the definition it can actually resolve to.
(new_expression type: (type_identifier) @name) @ref.type
