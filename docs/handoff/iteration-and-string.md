# Iterator adapters, and `String`'s methods in Wylow

Two pieces of work remain from the extension-methods session. Both are decided. Extension methods
(`functions.extensions`, decision 0080), overloads that differ only in their bounds (decision 0081),
and the module `std::iteration` (decision 0079) are implemented. The recommended order is the
adapters first, because they need no compiler change that is still undecided, and the user called
them the main goal.

## 1. Iterator adapters and terminal operations

Implement step 2 of decision 0075 in `std::iteration`
(`src/Wyl.Compiler.Semantics/Stdlib/iteration/`). Read decisions 0075, 0076, 0079, and 0081 first.

- The adapters are `filter`, `map`, `take`, `skip`, and `zip`. Each adapter is an extension on
  `this I source` with `I extends Iterator<T>`, and returns a struct that holds the source and an
  escaping function. `filter`, `take`, and `skip` get one overload for each iterator interface
  (`Iterator`, `RefIterator`, `InIterator`), and decision 0081 lets the three differ only in the
  bound of `I`. `map` always returns an `Iterator<U>`.
- The terminal operations are `count`, `any`, `all`, `first`, `sum`, and `toList` on iterators, and
  the same operations on spans as extensions on `this ReadOnlySpan<T>` that take a non-escaping
  function. No interface covers the numeric types, so `sum` needs one overload for each element
  type, such as `Iterator<int>` and `Iterator<long>`. Those overloads differ only in their bounds,
  which decision 0081 allows.
- Each declaration gets a doc comment (decision 0004), with a `# Panics` section where it can panic.

What the extension-methods session found:

- A span extension that calls its function with an element bound by `in` must take `fn(in T)`, not
  `fn(T)`. A `fn(T)` parameter would move an element of an unbounded `T` out of the span, which is
  `WYL0441`. The spec's example in `functions.extensions` was wrong in the same way and is fixed.
- An iterator's `current()` returns a fresh value, so an `Iterator<T>` adapter may pass it to a
  `fn(T)`.
- `ListIterator<T>` is a unique struct, so an adapter moves its source into its own field, and the
  adapter struct is unique too. Check that a generic struct with a field of a bounded type parameter
  `I` and a field of an escaping function type works in every mode before writing the adapters on
  top of it.
- `tests/Wyl.Harness/TestPrograms.cs` imports `std::iteration::*` into the programs that the
  harnesses write. A matrix cell or self-test that declares its own `count`, `map`, or `first`
  hides the import's names in plain calls, which is the intended rule (`programs.imports`), and an
  extension call still finds the import's extension (decision 0080).
- The binder code is in `Binder.Extensions.cs`. An extension call binds as the ordinary call of the
  function with the receiver as its first argument, so the adapters need nothing from lowering or
  the emitter.

Tests: a chain on a list (`list.iterator().filter(...).map(...).toList()`), every adapter on each
iterator interface it accepts, a filtered `RefIterator` that a loop changes through `ref`, the span
operations, a type that implements two iterator interfaces (an ambiguous call, `WYL0237`), and a
unique element type (the `copyable` bound of `List.iterator()`). Add rows to the values matrix next
to the iterator rows of `MatrixValues.cs`.

## 2. Methods with bodies in extern structs, and `String`'s methods

Implement decision 0078. The spec rule is in `interop.extern.extern-structs`.

- The binder refuses a method with a body in an extern struct with `WYL0430`
  (`Binder.Declarations.cs`, `NonExternMethodInExternStruct`). Retire the code: decision 0003 keeps
  a retired code in the catalog, marked retired, and its page says that the compiler no longer
  emits it.
- **The emitter has no form for such a method yet, and this is the design question of the work.**
  A struct's instance method is a C++ member function (`CppEmitter.MemberDeclaration`), and a
  method with a by-value receiver is a `static` member. Neither can be added to `wyl::String`, which
  the runtime defines. A method with a body therefore needs a free C++ function that takes the
  receiver as an explicit parameter. The body's `this`, and its calls of other members without a
  receiver, then refer to that parameter. Decide the representation with `docs/design/ir.md` and
  the architecture row "A by-value receiver is not a parameter of the signature" in mind:
  `FunctionSymbol.ReceiverParameter`, `MethodReference.ReceiverCount`, and the lowering of
  by-value receivers already line up a receiver that is passed as the first argument. Present the
  choice to the user before building it.
- The interpreter sends every instance method of an extern type to its intrinsics
  (`Interpreter.Intrinsics.cs`, the check of `ContainingType.IsExtern`). A method with a body must
  run its body instead.
- Then move `String`'s methods: keep a native floor (the length in bytes, the bytes, and the
  operations whose speed depends on the runtime), and write `startsWith`, `endsWith`, `contains`,
  `indexOf`, `slice`, and `charCount` in `string.wyl` on top of it. Delete each moved method from
  `runtime/wyl/core.h` and from the interpreter's intrinsics in the same change.
- `bytes()` builds its span in Wylow from the runtime's data pointer and length. The runtime then
  stops naming std's `ReadOnlySpan` (`StdlibSymbols.Register` marks it `IsNamedByRuntime`, and
  `TypeSymbol.RuntimeName` gives the name). Remove that hook if nothing else needs it.

Tests: every moved method keeps its tests in `tests/cases/values/` and passes in every mode, and
the generated C++ of a program that calls them compiles without warnings.

## Open for the user

- Whether `List`, `Array`, and future collections such as `Map` and `Queue` move into a module such
  as `std::collections`. The convention for `std`'s modules is in `docs/design/working.md`.
- The roadmap (issue #6) still needs the update of the extension-methods session; the user applies
  it.
