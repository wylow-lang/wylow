# VPL Language Specification

**VPL** (Videogame Programming Language) is a personal, purpose-built language for writing 3D games.

- **Looks like Java:** naming, class syntax, packages, generics syntax.
- **Performs like C++:** value types, inline arrays, explicit layout, no GC, zero-cost generics.
- **Compiles to C++:** the VPL compiler emits C++ which is then built by clang/MSVC/gcc.
- **No undefined behavior by default:** everything the compiler generates is UB-free unless you
  explicitly opt out with `unsafe` or `@Unchecked`, similar to C#.

VPL is intentionally *not* general purpose. Game-specific functionality (vectors, matrices,
rendering, etc.) lives in a small standard library rather than the language itself; the language
provides the tools needed to make that library as fast as built-ins (value types, operator
overloading, layout control).

> Status: **draft**. Sections marked *Open* are not yet decided.

## Design principles

1. **Safe by default, fast by opt-in.** Every check can be turned off, but only explicitly.
2. **Failures are loud.** A failed safety check *panics*, and a panic always aborts the process.
3. **Errors are values.** No exceptions. Recoverable failures are `Result<T, E>`.
4. **Simple ownership.** No borrow checker and no lifetime annotations. Safety comes from a small
   set of structural rules (second-class references, refcounting, generational handles).
5. **Data-oriented friendly.** Plain structs, pools, and spans are the fast path and the parallel path.
6. **Explicit over clever.** Boilerplate is acceptable if it makes behavior obvious.

## Chapters

| File | Contents |
|---|---|
| [01-safety.md](01-safety.md) | Undefined behavior policy, panics, `unsafe`, `@Unchecked` |
| [02-types.md](02-types.md) | Primitives, conversions, nullability, enums, structs, classes, arrays, generics |
| [03-memory-model.md](03-memory-model.md) | Values, refcounted objects, pools and handles, references, spans |
| [04-errors.md](04-errors.md) | `Result<T, E>`, `try`, handling errors |
| [05-concurrency.md](05-concurrency.md) | Thread confinement, `Sendable`, `shared class`, jobs, threads, statics |
| [06-syntax.md](06-syntax.md) | Packages, construction, parameters, properties, operators, lambdas, `defer`, control flow |
| [07-interop.md](07-interop.md) | `extern`, `@Safe`, blittable types, C enums and flags, strings/buffers/callbacks, native shims |

## Behaviors that surprise people

These are deliberate, but differ from what a Java/C#/C++ programmer might expect.
Each is explained in detail in its chapter.

1. **Pools free memory late.** Removing an element from a `Pool` while the pool is borrowed
   invalidates its handle immediately, but the destructor runs (and the slot becomes reusable)
   only once the pool is no longer borrowed. See [03-memory-model.md](03-memory-model.md#deferred-reclamation).
2. **Copying a struct shares what's inside it.** Struct copies are shallow: class references in the
   copy point at the same objects (same `List`, same `String`). Use `.clone()` for a deep copy.
   See [02-types.md](02-types.md#29-structs).
3. **Mutable statics are per-thread.** Unlike Java, each thread has its own copy of a mutable
   `static` field. Use `static shared` for a true process-wide global.
   See [05-concurrency.md](05-concurrency.md#55-statics).
4. **`1.5` is a `float`.** Use `1.5d` for `double`. See [02-types.md](02-types.md#22-literals).
5. **`byte` is unsigned** (like C#). `sbyte` is the signed version.
6. **`new` means heap allocation.** Structs are constructed without it: `Vec3(1, 2, 3)`.
   See [06-syntax.md](06-syntax.md#63-construction).
7. **Class `==` is identity, struct `==` is memberwise.** See [06-syntax.md](06-syntax.md#equality).

## Not yet designed

- Compiler architecture and implementation language.
- Standard library contents.
