; C references.
;
; Convention:
;   @ref.<kind>  the reference node
;   @name        the identifier being referenced (falls back to the @ref node)
;   @args        argument list, used to record call arity
;
; The analyzer drops any reference whose position coincides with a declaration's
; own name, so declaring a symbol never counts as referencing it.

; Direct calls
(call_expression
  function: (identifier) @name
  arguments: (argument_list) @args) @ref.call

; Calls through a struct member or function pointer field
(call_expression
  function: (field_expression
    field: (field_identifier) @name)
  arguments: (argument_list) @args) @ref.call

; Includes. Both quoted and angle-bracket forms feed the file dependency graph.
(preproc_include path: (string_literal) @name) @ref.include
(preproc_include path: (system_lib_string) @name) @ref.include

; Type references
(type_identifier) @ref.type
(sized_type_specifier) @ref.type

; Plain identifier uses: how a function reaches a constant or global.
(identifier) @ref.use

; Struct member access
(field_expression field: (field_identifier) @name) @ref.use
