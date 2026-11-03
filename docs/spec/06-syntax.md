# 6. Syntax

## 6.1 Packages, modules, imports

Packages, modules, imports, visibility, and the entry point are in
[chapter 3](03-programs.md#programs). These library modules stay here until appendix A is written:

- **`std::panic`** holds what is about panics ([Panics](18-errors.md#errors.panics)): `panic(message)`, which `std` re-exports, and
  the checks the built-in arrays make, for a container whose index isn't its storage's (a ring
  buffer on an `Array`): `checkIndex(index, length)` and `checkRange(start, count, length)`, which
  report the location of a `@CallerLocation` caller as the built-in checks do. A message is all a
  panic carries: it is a bug, not a value to recover ([Panics](18-errors.md#errors.panics)).
- **`std::memory`** holds raw memory (`allocate`, `free`, `initialize`, ..., all `unsafe`,
  [Interop](21-interop.md#interop)) and the cycle collector (`collectCycles`, `cycleStats`,
  [Cycles](17-memory.md#memory.cycles)).

## 6.5 No properties

Wylow has no properties (C#'s `get` and `set`), as Java has none. A value is either a field, read
and assigned directly, or a method, called with parentheses:

```java
class Player {
    pub mut int health = 100;
    pub bool isDead() { return health <= 0; }
}
```

A class field that isn't `mut` can be read anywhere but assigned only by its class's constructors,
which covers what a read-only property would: `array.length`. A struct's fields are as mutable as
the variable holding it ([Mutability](06-variables.md#variables.mutability)), so a struct whose
fields must keep an invariant leaves them unmarked, private to its file, and offers methods:
`span.length()`. Turning a field into a method later means updating its uses.

## 6.10 Annotations

Java syntax: `@Name` or `@Name(args)`. Currently compiler-defined only:

| Annotation | Meaning | Chapter |
|---|---|---|
| `@CallerLocation` | A panic inside the function reports where it was called from | below |
| `@AllowLargeStack` | Suppress the large-stack warning | [`@AllowLargeStack`](22-annotations.md#annotations.allow-large-stack) |
| `@Align(n)`, `@Packed` | Struct layout | [Layout](07-structs.md#structs.layout) |
| `@Repr(T)` | An enum's integer representation | [09-enums.md](09-enums.md#enums.repr) |
| `@Flags` | An enum whose values combine as bits | [09-enums.md](09-enums.md#enums.flags) |
| `@Acyclic` | A class's objects never form cycles | [What the collector looks at](17-memory.md#memory.cycles.collected) |

An argument is a type, as in `@Repr(uint)`, or a literal, as in `@Align(16)`. Settings that
change how a declaration is laid out or treated, rather than what it is, are annotations rather
than new syntax. **An annotation never changes a type**: what can be assigned, converted, or
captured. Whatever is part of a type is a keyword, such as a parameter's mode (`ref`, `in`) or
whether a function is `escaping` ([Escaping and non-escaping](12-functions.md#functions.lambdas.escaping)).

Parentheses right after an annotation are its arguments unless they start with `fn`, which makes
them the parentheses that group the annotated declaration's function type: `@Name (fn(int) -> int)? f`.

### `@CallerLocation`

Every panic inside a function marked `@CallerLocation`, including those of the checks it calls
and of other `@CallerLocation` functions it calls, reports the location of the call to it. The
standard library uses it so that a failed `list[i]` points at the user's line rather than into
`List`. A lambda written in such a function reports its own locations, since it may run after the
call has returned. It applies to functions, methods, constructors, and indexers, but
not yet to virtual, abstract, or interface methods.
