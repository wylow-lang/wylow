# `String`'s methods in Wylow

One piece of work remains from the extension-methods session, and it is decided. The iterator
adapters and terminal operations of `std::iteration` are implemented (decisions 0079, 0081, and
0082).

## Methods with bodies in extern structs, and `String`'s methods

Implement decision 0078. The spec rule is in `interop.extern.extern-structs`.

- The binder refuses a method with a body in an extern struct with `WYL0430`
  (`Binder.Declarations.cs`, `NonExternMethodInExternStruct`). Retire the code. Decision 0003 keeps
  a retired code in the catalog, marked retired, and its page says that the compiler no longer
  emits it.
- **The emitter has no form for such a method yet, and this is the design question of the work.**
  A struct's instance method is a C++ member function (`CppEmitter.MemberDeclaration`), and a
  method with a by-value receiver is a `static` member. Neither can be added to `wyl::String`, which
  the runtime defines. A method with a body therefore needs a free C++ function that takes the
  receiver as an explicit parameter. The body's `this`, and its calls of other members without a
  receiver, then refer to that parameter. Decide the representation with `docs/design/ir.md` and
  the architecture row "A by-value receiver is not a parameter of the signature" in mind.
  `FunctionSymbol.ReceiverParameter`, `MethodReference.ReceiverCount`, and the lowering of by-value
  receivers already line up a receiver that is passed as the first argument. Present the choice to
  the user before building it.
- The interpreter sends every instance method of an extern type to its intrinsics
  (`Interpreter.Intrinsics.cs`, the check of `ContainingType.IsExtern`). A method with a body must
  run its body instead.
- Then move `String`'s methods. Keep a native floor: the length in bytes, the bytes, and the
  operations whose speed depends on the runtime. Write `startsWith`, `endsWith`, `contains`,
  `indexOf`, `slice`, and `charCount` in `string.wyl` on top of it. Delete each moved method from
  `runtime/wyl/core.h` and from the interpreter's intrinsics in the same change.
- `bytes()` builds its span in Wylow from the runtime's data pointer and length. The runtime then
  stops naming std's `ReadOnlySpan` (`StdlibSymbols.Register` marks it `IsNamedByRuntime`, and
  `TypeSymbol.RuntimeName` gives the name). Remove that hook if nothing else needs it.
- The pipeline drops the functions of `std` that a program cannot reach (`Reached.Find`). The emitter
  or the interpreter may call a moved method outside the IR, for example to print a `String`. If
  one does, add it to the roots in `Reached.IsRoot`, and to the architecture row "Every program
  pays only for the `std` it reaches".

Tests: every moved method keeps its tests in `tests/cases/values/` and passes in every mode, and
the generated C++ of a program that calls them compiles without warnings.

## What the iteration session found

- A test run used to take about five minutes, because every program verified all of `std`. A run of
  the whole suite in the interpreter now takes about two and a half minutes. If the time grows
  again, profile one `wyl check` with `dotnet-trace` before guessing.
- A visible member still hides an extension of the same name (decision 0083). `List` has `count()`
  and `first()`, so a program reaches the span operations `count(test)` and `first(test)` on a list
  through `asReadOnlySpan()`.
- `Frozen<T>` has no iterator yet. Decision 0082 says that it would give an `InIterator`.

## Open for the user

- Whether `List`, `Array`, and future collections such as `Map` and `Queue` move into a module such
  as `std::collections`. The convention for `std`'s modules is in `docs/design/working.md`.
- The roadmap (issue #6) still needs the updates of the extension-methods and iteration sessions.
  The user applies them.
