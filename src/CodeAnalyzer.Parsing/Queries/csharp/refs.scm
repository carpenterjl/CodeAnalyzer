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

; The same type positions written with a namespace in front. `new OpenSim.Pcb.Import
; .NetMesher()` names the identical declaration `new NetMesher()` does, but its `type:`
; field is a qualified_name rather than an identifier, so none of the rules above see it.
; The trailing identifier was then left to the bare-identifier rule at the foot of this
; file, which files it as a Use — and a Use is not kind-compatible with a class, so a
; namespace-qualified construction resolved to nothing at all while the unqualified form
; resolved fine. Measured on OpenSim Studio before these were written: four
; `new OpenSim.Pcb.Import.NetMesher()` sites carried no edge, and the field report that
; found them read the silence as a resolver rule about relative qualifiers.
;
; qualified_name's `name:` field is the last segment however deep the qualifier nests, and
; a generic tail is already covered by the (generic_name …) rule above, which matches
; wherever a generic_name appears.
(object_creation_expression type: (qualified_name name: (identifier) @name)) @ref.type
(variable_declaration type: (qualified_name name: (identifier) @name)) @ref.type
(parameter type: (qualified_name name: (identifier) @name)) @ref.type
(method_declaration returns: (qualified_name name: (identifier) @name)) @ref.type
(property_declaration type: (qualified_name name: (identifier) @name)) @ref.type
(array_type type: (qualified_name name: (identifier) @name)) @ref.type
(nullable_type (qualified_name name: (identifier) @name)) @ref.type
(cast_expression type: (qualified_name name: (identifier) @name)) @ref.type
(base_list (qualified_name name: (identifier) @name)) @ref.inherit

; Namespace imports. These feed the file dependency graph rather than symbol edges;
; a namespace is not a file, so most stay unresolved, which is the honest answer.
(using_directive (qualified_name) @name) @ref.import
(using_directive (identifier) @name) @ref.import

; Member access, then bare identifiers: how a method reaches a constant or a field.
(member_access_expression
  expression: [(_) "this" "base"] @receiver
  name: (identifier) @name) @ref.use
(identifier) @ref.use
