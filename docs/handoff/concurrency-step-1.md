# Next: statics and `sendable`

Repo `E:\Repos\Wylow\wylow`, branch `dev`. Step 4 of ownership is done. Read decisions 0029, 0030,
0032, and 0036, the spec sections `variables.statics`, `concurrency.sendable`,
`concurrency.sendable.moving`, `types.copyable`, and `programs.execution`, and the steps under
"Concurrency" in issue #6.

## Step 1: the `sendable` property, then static fields

**`sendable` is a type property, like `copyable`.** One predicate answers whether a type is
sendable, from its declaration and from its fields, elements, and type arguments (0036,
`concurrency.sendable`). Nothing about a value's use belongs in that predicate.

- Give it the shape `TypeCopying` already has: `IsSendableIn(function)` is the single answer, and a
  member's `where` clause can make its type's parameter sendable, as it already can make one
  copyable. `FunctionSymbol` needs the set of parameters a member makes sendable, and
  `ApplyMemberBounds` must add and remove the property the way it does for `CopyableParameters`.
- `BindBounds` and `BindWhere` accept the word `sendable` in a bound, in lowercase and contextual,
  exactly where they accept `copyable`.
- The rows this step can implement are the primitive types, `String`, a type parameter with the
  bound, a struct, enum, or fixed array whose parts are all sendable, and the negative rows: a
  reference to a normal class, an array, a `List`, a function that is not `shared`, and an
  unbounded type parameter. The `shared class`, `shared fn`, `Weak`, and `Isolated` rows wait for
  the steps that add those types.
- Where the spec requires a sendable type, a failure is a compile-time error that names the field
  that is not sendable (`concurrency.sendable`). The first such place is a static's type; a
  `shared class` field comes with shared classes.

**Static fields.** The parser currently rejects `static` on a field, with `WYL0094`. Retire that
code, and add these:

- A static field in a generic type (0032): a new code, with its page and test.
- A static whose type is not sendable and copyable: a new code. A unique type is not copyable, so
  this also refuses a unique value as a static, as 0029 says.
- Writing to a static, since every static is read-only: a new code.

**Initialization.** Every static of the program's package and of `std` is initialized before `main`
runs, on the main thread, exactly once (0030):

- A package's statics come after those of every package it depends on, and packages that
  neither depends on come in name order. Within a package, files come in order of their path under
  `src/`, and within a file, statics come in the order they are written. Package dependencies are
  not implemented, so today the order is the program's package and `std`.
- An initializer that reads a static not yet initialized initializes that one first.
- Reading a static whose initializer is still running panics, naming the statics involved.
- A panic in an initializer ends the program before `main` begins.
- Starting a thread before `main` needs no work yet; threads do not exist.

**Representation.** A static is never destroyed (0029). The generated C++ therefore must not let a
C++ static's destructor run, and the interpreter must not release statics at the end of the run.
Settle one design for both executors: for example, storage that is initialized and never destroyed,
with the initialization written as one function the emitter and the interpreter both run.

**Tests.** For each behavior and each error, a test; the rows in the matrices; and a fuzzer
mutation that can cross a static (for example, one that adds a static field to a type).

- Initialization order across a file, across files of a package, and between the program and `std`.
- A static read from an initializer that has not run yet; a cycle among initializers and its panic.
- A program that ends: a static that refers to an object releases nothing when `main` returns.
- A static read from a method, and from outside its type once the access syntax is decided.

## What step 4 built

- **Destructors and `discard`.** A unique struct may declare a destructor; the emitter gives it a
  move constructor, deletes its copies, and keeps a state byte. `discard` is one IR instruction.
  A static cannot hold a unique value, so no destructor runs for a static (0029).
- **Values in use.** The mandatory `InUse` pass marks the values a call or a reference uses in
  place, and the runtime records one bit per value. This does not interact with statics except
  that a static's value is never in use across the end of the program.
- **The `copyable` property.** `BindBounds` stores a `copyable` bound on
  `TypeParameterSymbol.IsCopyableBound`, `ApplyMemberBounds` merges a member's clause, and
  `TypeCopying.IsCopyableIn(function)` is the single answer. The `sendable` work should copy that
  shape, not invent a second one.
- **Diagnostics.** New codes start at `WYL0463`. Every code needs a catalog entry, a page, a test
  marker, and its row in the matrices; `CatalogSelfTests` enforces the first three.
- **Tests and oracles.** Every test runs in the interpreter and in the native modes, so the
  interpreter must implement statics the same way the emitter does.

## Still open

- How a static is named and read: unqualified inside its type, and with what syntax outside it.
  The spec says a static is a field declared in a type; it does not spell the member access.
- Where the initialization order is computed: the binder knows the declarations, and the emitter
  and the interpreter both need the same order. One place should own it.
- How the generated C++ stores a static without destroying it, and how that maps to the
  interpreter's globals.
- Whether a static's initializer may start a thread once threads exist; 0030 says it panics.
- The sendable rows for `std`'s `unsafe` floor types, which 0036 leaves open.
- `String`'s reference count becomes atomic only when threads exist; statics alone do not need it.
