; C# references.
;
; Convention:
;   @ref.<kind>  the reference node
;   @name        the identifier being referenced (falls back to the @ref node)
;   @args        argument list, used to record call arity
;   @receiver    the receiver expression of a member access, recorded verbatim so the
;                resolver can tell obj.Foo() — whose target lives wherever obj's type
;                does — from a bare Foo() or a this.Foo(), which really are local claims
;
; The analyzer drops any reference whose position coincides with a declaration's own
; name, and where two patterns land on the same position the more specific kind wins.

; Calls
(invocation_expression
  function: (identifier) @name
  arguments: (argument_list) @args) @ref.call

; The receiver alternation is deliberate: in the bundled grammar `this` and `base` are
; anonymous keyword tokens, which the (_) wildcard does not match.
(invocation_expression
  function: (member_access_expression
    expression: [(_) "this" "base"] @receiver
    name: (identifier) @name)
  arguments: (argument_list) @args) @ref.call

; Base classes and implemented interfaces.
(base_list (identifier) @name) @ref.inherit

; Type positions. C# has no separate type-identifier node, so each place a type can
; appear is listed: without these a field would only ever reach its type as a bare
; identifier use, which resolves to values rather than to type declarations.
(variable_declaration type: (identifier) @name) @ref.type
(parameter type: (identifier) @name) @ref.type
(method_declaration returns: (identifier) @name) @ref.type
(property_declaration type: (identifier) @name) @ref.type
(array_type type: (identifier) @name) @ref.type
(nullable_type (identifier) @name) @ref.type
(generic_name (identifier) @name) @ref.type
(type_argument_list (identifier) @name) @ref.type
(cast_expression type: (identifier) @name) @ref.type

; `new Frame(…)` depends on the type; that is the declaration it can resolve to.
(object_creation_expression type: (identifier) @name) @ref.type

; Namespace imports. These feed the file dependency graph rather than symbol edges;
; a namespace is not a file, so most stay unresolved, which is the honest answer.
(using_directive (qualified_name) @name) @ref.import
(using_directive (identifier) @name) @ref.import

; Member access, then bare identifiers: how a method reaches a constant or a field.
(member_access_expression
  expression: [(_) "this" "base"] @receiver
  name: (identifier) @name) @ref.use
(identifier) @ref.use
