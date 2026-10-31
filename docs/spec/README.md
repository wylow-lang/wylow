# Wylow Language Specification

**Wylow** is a personal, purpose-built language for writing 3D games.

- **Looks like Java:** naming, class syntax, packages, generics syntax.
- **Performs like C++:** value types, inline arrays, explicit layout, no tracing GC (cycles are
  collected only where you call the collector), zero-cost generics.
- **Compiles to C++:** the Wylow compiler emits C++ which is then built by clang/MSVC/gcc.
- **No undefined behavior by default:** everything the compiler generates is UB-free unless you
  explicitly opt out with `unsafe` or `@Unchecked`, similar to C#.

Wylow is built for games and similar applications, but is not limited to them. Game-specific
functionality (vectors, matrices, rendering, etc.) lives in a small standard library rather than
the language itself; the language provides the tools needed to make that library as fast as
built-ins (value types, indexers, layout control).

> Status: **draft**. Sections marked *Open* are not yet decided.

## Design principles

1. **Safe by default, fast by opt-in.** Every check can be turned off, but only explicitly.
2. **Failures are loud.** A failed safety check *panics*. A panic stops the isolate it happens in and
   discards its state: the whole process, unless the code runs in an `Isolate` or on another thread.
3. **Errors are values.** No exceptions. Recoverable failures are `Result<T, E>`.
4. **Simple ownership.** No borrow checker and no lifetime annotations. Safety comes from a small
   set of structural rules (second-class references, refcounting, borrow counters).
5. **Data-oriented friendly.** Plain structs, arrays, and spans are the fast path and the parallel path.
6. **Explicit over clever.** Boilerplate is acceptable if it makes behavior obvious.

## Chapters

| File | Contents |
|---|---|
| [01-safety.md](01-safety.md) | Undefined behavior policy, panics, `unsafe`, `@Unchecked` |
| [02-types.md](02-types.md) | Primitives, conversions, nullability, enums, structs, classes, arrays, generics |
| [03-memory-model.md](03-memory-model.md) | Values, refcounted objects, references, spans |
| [04-errors.md](04-errors.md) | `Result<T, E>`, `try`, handling errors |
| [05-concurrency.md](05-concurrency.md) | Thread confinement, `Sendable`, `shared class`, jobs, threads, statics, isolates and panics |
| [06-syntax.md](06-syntax.md) | Packages, construction, parameters, indexers, lambdas, `defer`, control flow |
| [07-interop.md](07-interop.md) | `extern`, `@Safe`, blittable types, C enums and flags, strings/buffers/callbacks, native shims |

## Behaviors that surprise people

These are deliberate, but differ from what a Java/C#/C++ programmer might expect.
Each is explained in detail in its chapter.

1. **Copying a struct shares what's inside it.** Struct copies are shallow: class references in the
   copy point at the same objects (same `List`, same `String`). Use `.clone()` for a deep copy.
   See [02-types.md](02-types.md#29-structs).
2. **Mutable statics are per-thread.** Unlike Java, each thread has its own copy of a
   `static mut` field. Use `static shared` for a true process-wide global.
   See [05-concurrency.md](05-concurrency.md#55-statics).
3. **`1.5` is a `float`.** Use `1.5d` for `double`. See [02-types.md](02-types.md#22-literals).
4. **`byte` is unsigned** (like C#). `sbyte` is the signed version.
   **Arithmetic keeps its operands' type**: `byte + byte` is a `byte`, not an `int`, so it can
   overflow. Widen first where it might: `((int)a + b) / 2`. See [02-types.md](02-types.md#24-arithmetic).
5. **`new` means heap allocation.** Structs are constructed without it: `Vec3(1, 2, 3)`.
   See [06-syntax.md](06-syntax.md#63-construction).
6. **Class `==` is identity, struct `==` is memberwise.** `String` is the exception: its `==`
   compares contents. See [06-syntax.md](06-syntax.md#equality).
7. **A variant can leave out its enum only where the type is known.** `Direction d = East;` and
   `move(East)` work, but `var d = East;` doesn't, and neither does an argument to overloads that
   disagree on its type. See [02-types.md](02-types.md#creating-values).
8. **`break` in a `switch` case leaves the enclosing loop**, since a switch has no fallthrough to
   stop. See [06-syntax.md](06-syntax.md#switch).
9. **A list can't change length while it's borrowed.** A loop over a list, a `ref` or span into
   it, or an assignment like `list[0] = f()` borrows it, and `add` or `removeAt` meanwhile panics.
   See [03-memory-model.md](03-memory-model.md#borrow-counters-on-containers).
10. **Cycles are freed only when you ask.** Reference counting frees everything else immediately, but
   objects that refer to each other in a cycle stay alive until the program calls
   `Memory.collectCycles`, which never runs on its own. Use `Weak<T>` for references that shouldn't
   keep their target alive. See [03-memory-model.md](03-memory-model.md#cycles-memorycollectcycles).
11. **`T?` is `T` when `T` is already nullable.** A generic `T? find()` on a `List<int?>` returns
   an `int?` that is null both when nothing was found and when a null was. See
   [02-types.md](02-types.md#nullable-type-parameters).
12. **A null check doesn't narrow a `mut` local that a lambda assigns**, from where the lambda is
   made on, since the lambda may run during any call. Copy it to another local and check that. See
   [06-syntax.md](06-syntax.md#67-lambdas-and-function-types).
13. **Variables, parameters, and fields are immutable unless declared `mut`**, as in Rust. For a
   class reference, this means the variable can't be reassigned; the object itself can still
   change. See [02-types.md](02-types.md#27-mutability-and-constants).

## Standard library

The standard library is Wylow source in `src/Wyl.Compiler.Semantics/Stdlib/`, compiled with every program, so
what it does can be read like any other Wylow code. The compiler knows its types only through narrow
hooks: `Result` (for `try`), `Array` (for `T[]`), `Span` and `ReadOnlySpan` (for conversions and
loops), `Spannable`, `ReadOnlySpannable`, `Borrowable`, `Printable` and `Comparable` (which
built-in types satisfy without implementing them), and `String`, whose `extern struct`
declares the methods of the built-in type. Until packages exist, a program cannot declare a type
with the name of a standard library type.

Its `extern` functions are implemented by the C++ runtime. Those marked `@Pure` can't panic or
change anything, so the compiler orders calls of them like operators rather than like calls.

## Not yet designed

- The rest of the standard library: `Map` and the other collections beyond `List` and `Array`.
  `Box<T>` (02-types.md) is designed but not implemented. Pools and handles are not part of the
  language: they are an ordinary data structure a library can provide.
- **Performance warnings**: a kind of diagnostic, separate from errors and from ordinary warnings,
  for code that compiles and is correct but that the compiler can't optimize the way a reader would
  expect. The first planned one: a destructor that stores over an element of some type, which
  makes every loop over that type that may release an object of its class keep a reference count
  per element ([03-memory-model.md](03-memory-model.md#loop-elements)). It should name the
  destructor and the loops it affects.
  Some are found at runtime instead, in debug builds: the cycle collector's
  ([03-memory-model.md](03-memory-model.md#diagnostics)).
