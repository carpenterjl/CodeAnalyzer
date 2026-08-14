; Verilog / SystemVerilog symbol definitions.
;
; Convention shared by every language pack:
;   @def.<kind>  the node whose span defines the symbol
;   @name        the identifier (falls back to the @def node when absent)
;   @value       verbatim literal initializer
;   @type        verbatim declared type
;   @params      parameter list, used for arity and for the displayed signature
;
; This grammar uses almost no field names, so the patterns below are positional and
; deeper than the other packs. Every identifier bottoms out in (simple_identifier).

(module_declaration
  (module_header
    (simple_identifier) @name)) @def.module

(package_declaration
  (package_identifier
    (simple_identifier) @name)) @def.namespace

(interface_declaration
  (interface_ansi_header
    (interface_identifier
      (simple_identifier) @name))) @def.interface

; `define WIDTH 8 — the replacement text is the fact worth showing.
(text_macro_definition
  (text_macro_name
    (text_macro_identifier
      (simple_identifier) @name))) @def.macro

(text_macro_definition
  (text_macro_name
    (text_macro_identifier
      (simple_identifier) @name))
  (macro_text) @value) @def.macro

; Ports. The header carries the direction and the packed width, which together are
; what a caller needs to see.
(ansi_port_declaration
  (port_identifier
    (simple_identifier) @name)) @def.port

(ansi_port_declaration
  (variable_port_header) @type
  (port_identifier
    (simple_identifier) @name)) @def.port

(ansi_port_declaration
  (net_port_header1) @type
  (port_identifier
    (simple_identifier) @name)) @def.port

; parameter / localparam, in a port list or a body.
(parameter_declaration
  (list_of_param_assignments
    (param_assignment
      (parameter_identifier
        (simple_identifier) @name)
      (constant_param_expression) @value) @def.constant))

(local_parameter_declaration
  (list_of_param_assignments
    (param_assignment
      (parameter_identifier
        (simple_identifier) @name)
      (constant_param_expression) @value) @def.constant))

; typedef … name;  — the trailing identifier is the new type name.
(type_declaration
  (simple_identifier) @name) @def.typedef

(enum_name_declaration
  (enum_identifier
    (simple_identifier) @name)) @def.enum_member

; Packed struct members.
(struct_union_member
  (data_type_or_void) @type
  (list_of_variable_decl_assignments
    (variable_decl_assignment
      (simple_identifier) @name))) @def.field

; Variables and nets. Anchored to their declaration node so struct members, which
; reuse variable_decl_assignment, are not also counted here.
(data_declaration
  (data_type_or_implicit1) @type
  (list_of_variable_decl_assignments
    (variable_decl_assignment
      (simple_identifier) @name))) @def.variable

(net_declaration
  (list_of_net_decl_assignments
    (net_decl_assignment
      (simple_identifier) @name))) @def.variable

; Instance names: `fifo u_fifo (…)` declares u_fifo.
(hierarchical_instance
  (name_of_instance
    (instance_identifier
      (simple_identifier) @name))) @def.variable

; Functions and tasks. Both nest their identifier twice in this grammar.
(function_body_declaration
  (function_identifier
    (function_identifier
      (simple_identifier) @name))) @def.function

(function_body_declaration
  (function_identifier
    (function_identifier
      (simple_identifier) @name))
  (tf_port_list) @params) @def.function

(task_body_declaration
  (task_identifier
    (task_identifier
      (simple_identifier) @name))) @def.function

(task_body_declaration
  (task_identifier
    (task_identifier
      (simple_identifier) @name))
  (tf_port_list) @params) @def.function
