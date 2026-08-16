; JavaScript references.
;
; Convention:
;   @ref.<kind>  the reference node
;   @name        the identifier being referenced (falls back to the @ref node)
;   @args        argument list, used to record call arity
;
; The analyzer drops any reference whose position coincides with a declaration's own name,
; and where two patterns land on the same position the more specific kind wins.
;
; A method call stores only the member name — `bridge.on(…)` is recorded as `on` — for the
; same reason the C# and Python packs do: the receiver's type is not a syntactic fact, and
; inventing one would put a confident edge where the source states nothing.

(call_expression
  function: (identifier) @name
  arguments: (arguments) @args) @ref.call

(call_expression
  function: (member_expression
    property: (property_identifier) @name)
  arguments: (arguments) @args) @ref.call

(new_expression
  constructor: (identifier) @name
  arguments: (arguments)? @args) @ref.instantiate

; `class Renderer extends Base`
(class_heritage (identifier) @name) @ref.inherit

; Module dependencies. The specifier is recorded exactly as written and matched against
; workspace files during resolution. A specifier written without its extension —
; `import x from "./util"` — names no file this index can find; it is left unresolved
; rather than having an extension guessed onto it.
(import_statement
  source: (string
    (string_fragment) @name)) @ref.import

(call_expression
  function: (identifier) @callee
  arguments: (arguments
    (string
      (string_fragment) @name))
  (#eq? @callee "require")) @ref.import

; Property access, then bare identifiers.
(member_expression
  property: (property_identifier) @name) @ref.use

(identifier) @ref.use
