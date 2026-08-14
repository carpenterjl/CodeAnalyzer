; Verilog / SystemVerilog references.
;
; Convention:
;   @ref.<kind>  the reference node
;   @name        the identifier being referenced (falls back to the @ref node)
;   @args        argument list, used to record call arity
;
; The analyzer drops any reference whose position coincides with a declaration's own
; name, and where two patterns land on the same position the more specific kind wins.

(include_compiler_directive
  (double_quoted_string) @name) @ref.include

; Instantiating a module is this language's call graph.
(module_instantiation
  (simple_identifier) @name) @ref.instantiate

; Function and task calls that appear inside an expression.
(tf_call
  (simple_identifier) @name) @ref.call

(tf_call
  (simple_identifier) @name
  (list_of_arguments_parent) @args) @ref.call

; `WIDTH
(simple_text_macro_usage
  (text_macro_identifier
    (simple_identifier) @name)) @ref.use

; A user type used in a declaration: `state_e state;`
(net_declaration
  (simple_identifier) @name) @ref.type

(data_type
  (simple_identifier) @name) @ref.type

; Port connections name the instantiated module's ports.
(named_port_connection
  (port_identifier
    (simple_identifier) @name)) @ref.use

(named_parameter_assignment
  (parameter_identifier
    (simple_identifier) @name)) @ref.use

; Reads and writes of signals.
(primary
  (simple_identifier) @name) @ref.use

(variable_lvalue
  (simple_identifier) @name) @ref.use
