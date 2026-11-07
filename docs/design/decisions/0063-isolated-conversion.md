# 0063. A sendable value converts to a box, and a box is sendable for any `T`

- Status: accepted (2026-11-06)
- Spec: `concurrency.sendable`, `concurrency.isolated`

## Decision

- `StdlibSymbols.Register` marks the standard library's type named `Isolated` with
  `TypeSymbol.IsIsolated`, and its type named `IsolatedState` with `TypeSymbol.IsIsolatedState`.
- `TypeSendable.IsSendableIn` answers true for an isolated type before it walks the type's parts. It
  does not consult the type argument, since nothing outside a box refers to the graph the box holds.
- `Conversions.IsImplicit` converts a value of type `U` to `Isolated<T>` when `U` is sendable and `U`
  converts to `T`. A value that is not sendable does not convert.
- Lowering keeps the conversion as `IrConvert`. The emitter builds the box with the package content
  constructor, and the interpreter invokes the same constructor, so one body makes both.
- The binder reports `WYL0478` for a value that is not sendable, and a note names the field whose
  type is not sendable when a field makes the type so.

## Alternatives

- **A constructor that counts as a conversion.** The language has no rule by which a constructor
  converts, and adding one would make every struct's constructors candidates at every conversion.
  The package content constructor exists so that the conversion has a body to call, not so that a
  program may write it.
- **A free function `isolate(value)` with a `sendable` member bound.** A bound would refuse a value
  that is not sendable with the binder's existing message, but a call is not a conversion, so
  `Isolated<Index> box = value;` would not compile and the spec's rule would not hold.
- **One mark for both standard library exceptions.** `SyncState` is sendable because the runtime
  owns what the handle holds. A box is sendable because nothing outside refers to what it holds.
  One flag would record that a type is sendable without recording why, and a later change to one
  rule would then change the other type's answer.
- **Asking the type argument instead.** `Isolated<T>` would then be sendable only for a sendable `T`,
  which refuses the boxes that exist to carry a normal object graph.

## Why

The spec says a sendable value is isolated already and converts to `Isolated<T>` by moving in, so the
rule is a conversion, and a conversion belongs in one place, `Conversions`. The mark belongs on the
one type the standard library declares, as `SyncState`'s does, so no program can make another type
sendable this way.
