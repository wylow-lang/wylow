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
