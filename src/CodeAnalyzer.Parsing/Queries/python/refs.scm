; Python references.
;
; Convention:
;   @ref.<kind>  the reference node
;   @name        the identifier being referenced (falls back to the @ref node)
;   @args        argument list, used to record call arity
;
; The analyzer drops any reference whose position coincides with a declaration's own
; name, and where two patterns land on the same position the more specific kind wins.

(call
  function: (identifier) @name
  arguments: (argument_list) @args) @ref.call

; The receiver is recorded verbatim: dev.send() carries `dev`, self.send() carries
; `self`. The resolver treats a self-receiver as the local claim it is and any other
; receiver as defeating same-file locality.
(call
  function: (attribute
    object: (_) @receiver
    attribute: (identifier) @name)
  arguments: (argument_list) @args) @ref.call

; Base classes.
(class_definition
  superclasses: (argument_list (identifier) @name)) @ref.inherit

; Imports. The module path is recorded exactly as written — including the leading dots
; of a relative import — and matched against workspace files during resolution.
(import_statement
  name: (dotted_name) @name) @ref.import

(import_statement
  name: (aliased_import
    name: (dotted_name) @name)) @ref.import

(import_from_statement
  module_name: (dotted_name) @name) @ref.import

(import_from_statement
  module_name: (relative_import) @name) @ref.import

; `from device import Frame` names a symbol, not just a module.
(import_from_statement
  name: (dotted_name (identifier) @name)) @ref.use

; Annotations are type references.
(type (identifier) @name) @ref.type

; Attribute access, then bare identifiers.
(attribute
  object: (_) @receiver
  attribute: (identifier) @name) @ref.use
(identifier) @ref.use
