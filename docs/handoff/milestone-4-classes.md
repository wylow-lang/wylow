# Task: implement classes (milestone 4)

You're continuing work on VPL, a language that compiles to C++. Milestones 1–3 are done: functions
and primitives, structs with `ref`/`in` parameters, and generics. Read `CLAUDE.md` and
`docs/spec/README.md` first, then the spec sections listed below. Look at `git log` and the existing
tests in `tests/cases/` to see how features are built and tested.

The user prefers building the features that reshape the compiler first and leaving syntax sugar for
later. Classes are next because they add a new kind of type (heap references), a C++ runtime for
reference counting, and null-safety flow analysis.

## What to build

Spec sections that define classes: `02-types.md` §2.5 (nullability), §2.10 (classes), §2.12
(interfaces), §2.13 (generic bounds); `03-memory-model.md` §3.2 (refcounting, `weak`) and §3.4
(pinning); `06-syntax.md` §6.3 (`new`), §6.6 (equality); `07-interop.md` §7.3 (destructor syntax).

In scope:

1. **Class declarations**: fields with initializers, constructors, methods, `static` methods,
   `private`/`protected`/`public`, destructors `~Name() { }`.
2. **`new`** allocates: `new Player(...)`. Class references are non-null by default.
3. **Reference counting**: deterministic destruction when the last reference goes away. Non-atomic.
4. **Inheritance**: single `extends`, `super(...)` constructor calls, `virtual`, `override` (required),
   `abstract` methods and classes, `final` classes and methods. Upcasts are implicit.
5. **Interfaces**: declared with method signatures, implemented by classes (`implements`), usable as
   variable types for classes (dynamic dispatch). Structs may implement interfaces but only for use as
   generic bounds.
6. **Generic bounds**: `T extends SomeInterface`, which allows calling the interface's methods on `T`.
   This also unblocks bounded generics for structs (static dispatch through templates).
7. **Nullability** for class references: `T?`, the `null` literal, `x!` (panics on null), `a ?? b`,
   `a?.b`, `if (x != null)` narrowing for locals, and `if (x is Player p)` (also the way to downcast).
8. **`weak` fields**: must be nullable; become null when the target dies.
9. **Class `==` is reference identity.** Structs keep memberwise `==`.
10. **Structs containing class references**: copying bumps refcounts. A non-null class field with no
    initializer makes the struct non-defaultable (see "Default values" in `02-types.md` §2.13;
    `Conversions.IsDefaultable` must learn about class references).

Out of scope, so don't build these yet: `shared class`, atomics, `Sendable`, threads, containers
(`List`, arrays, `Pool`, `Span`), making `String` a class, operator overloading, properties,
nullable value types (`int?`), and enums.

## Memory-safety requirements

These are the parts most likely to go wrong. Each needs a test that would fail without the fix.

- **The receiver of a method call must stay alive during the call.** In `player.weapon.fire()`, if
  `fire` causes `player.weapon` to be reassigned, the weapon object would be freed while its method
  runs. The caller must hold a strong reference to the receiver for the call's duration unless it
  can prove the receiver is otherwise kept alive (for example, a local that isn't reassigned).
  Spec §3.4 calls this pinning.
- **The same applies to `ref` and `in` arguments that point into a class object**, such as
  `bump(ref player.stats)`: pin the object that owns the field.
- **Release the old value only after the new one is stored** when assigning a class reference. A
  destructor can run arbitrary code, including code that reads the field being assigned.
- **Evaluation order now involves the heap.** Today's analysis assumes a call can only change the
  caller's state through `ref` arguments or a mutating method receiver. With classes, any call can
  change any object's fields. Class field reads must count as conflicting with any call, so that
  `obj.x + f()` reads `obj.x` before calling `f`. Update `Writes`/`ReadsState` in `CppEmitter`.
- **Non-null fields must be initialized.** A non-null class-typed field of a class needs an
  initializer or a definite assignment in every constructor. Decide what code may run before all
  fields are assigned (Kotlin and Swift differ), choose the simplest rule that's sound, and write it
  in the spec.
- **No null dereference without a check.** Only `!` and `?.` can touch a nullable value, apart from
  a narrowed local.
- **Downcasts** happen only through `is` patterns, never through unchecked casts.

## Suggested C++ approach

You don't have to follow this, but whatever you choose must be free of undefined behavior.

- **An intrusive refcount**: a `vpl::Object` base class holding the count and a virtual destructor,
  plus a `vpl::Ref<T>` smart pointer. Non-null is enforced by VPL; `Ref<T>` for `T?` can be null.
- **`weak`** needs either a weak count with the allocation freed only when both counts reach zero,
  or a list of weak references to clear. Pick one and note it in the spec.
- **Downcasts** can use `dynamic_cast` or a type ID.
- **Watch out:** generic code must call methods the same way whether `T` is a struct (value, `.`) or
  a class (reference, `->`). Add a small helper in the runtime, such as `vpl::self(x)`, rather than
  special-casing the emitter.
- **Destructors** run when the count hits zero. Two nested releases must not overflow the stack;
  for now it's enough to note the risk in the spec.

## How to work

1. Read the spec sections and the code. Write down the design decisions the spec leaves open, and
   settle them before coding. Prefer the simplest rule that is sound.
2. Build in small steps, each with tests: classes with `new` and refcounting, then destructors, then
   inheritance with virtual dispatch, then interfaces and bounds, then nullability and flow typing,
   then `weak`, then pinning and the evaluation-order changes.
3. For each step, add tests in `tests/cases/classes/` (behavior, with `// output:` comments showing
   destructor order where it matters) and `tests/cases/errors/` (each error message).
4. Run the suite on MSVC and g++ after each step. Read the generated C++ in `tests/.out/` for the new
   cases, and check it compiles warning-free under the strict g++ flags in `CLAUDE.md`.
5. Update the spec with every decision you made, and update the README status list.
6. Commit when the milestone is done, then report what you built, what decisions you made and why,
   and what you left out.

## Done means

- Every in-scope item works and has tests, including the memory-safety cases above.
- All existing tests still pass on both compilers.
- The generated C++ has no undefined behavior paths and no warnings.
- The spec describes everything that was decided.
