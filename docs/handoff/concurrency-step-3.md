# Next: shared functions

Repo `E:\Repos\Wylow\wylow`, branch `dev`. Shared classes are done (0054). Read decisions 0015, 0019,
0020, 0036, and 0054, the spec sections `concurrency.sendable`, `concurrency.shared-functions`,
`functions.lambdas.escaping`, `functions.lambdas.captures`, `conversions.functions`, and the steps
under "Concurrency" in issue #6.

A `shared fn` is the function type whose lambda may run on another thread. Threads, channels, and
the standard library's synchronization types are not implemented, so this step settles the type, its
conversions, what its lambda may capture, and the closure object. The runtime still runs one thread.

## What is done

- `shared` is a keyword, and `Modifiers.Shared` marks only a class. `TypeSymbol.IsShared` holds the
  binder's answer, and `wyl::SharedObject` in `runtime/wyl/object.h` gives a shared class's object an
  atomic count, the global heap, and destruction at the last release.
- `FunctionType` has two forms today, escaping and non-escaping, interned by reference in
  `Semantics/TypeKinds.cs`. `BindFunctionType` returns the escaping form (`Binder.Lambdas.cs`).
- `escaping` is parsed on parameters, locals, and function-type parameters. `Binder.Escaping.cs`
  checks what an escaping lambda captures, and `Lowering.Lambdas.cs` makes an `IrClosure` that copies
  the captures.
- An escaping lambda's closure is `wyl::Closure<Name, Captures...> : wyl::Object`, made by
  `wyl::closure` in `runtime/wyl/functions.h`. `StackSlots` may make it in a stack slot.
- `TypeSendable` says no for every function type. `CyclicClasses` treats every closure that captures
  as a possible cycle node, and the interpreter marks its `ClosureValue` the same way.

## Step 1: the four forms

- Read `shared` in a function type: `shared fn(...) -> R`. A type starts with `shared` only when
  `fn` follows, so every place that recognizes a type start asks that question: `Parser.StartsType`,
  the top-level function branch in `Parser.ParseCompilationUnit`, `IsDeclarationStart` in
  `Parser.Statements.cs`, and `ParseType` with `ParseFunctionTypeRest` in `Parser.Types.cs`.
  `SyntaxFacts.IsTypeStart` needs a companion that sees the token after `shared`. `TypeSyntax` gains
  the flag.
- A parameter or local of function type writes `escaping` before the type, so a shared one reads
  `escaping shared fn() -> R`, as the spec shows. `shared` anywhere else stays the `WYL0469` error
  the class modifier reports.
- Give `FunctionType` an `IsShared` beside `IsEscaping`, and intern all four forms by reference in
  `Semantics/TypeKinds.cs`. `Escaping`, `NonEscaping`, and the new `NonShared` each change one
  property and keep the other. `MapParts` keeps both, and `BuildName` writes `shared fn(...) -> R`.
  `BindFunctionType` builds the form the syntax names.
- One answer for conversions: `Conversions.IsImplicit` accepts a function type whose target drops
  `escaping`, drops `shared`, or drops both, and accepts nothing that adds either. Function types
  whose parameters differ in escaping still convert to nothing.
- `TypeSendable` accepts an escaping `shared fn` and says no for every other function type. A
  shared class's field of that type is then sendable and copyable and needs no other rule.
- `TypeSelfTests` gains the properties: the four forms are distinct by reference, each form's
  accessors return the interned form, and only the drops convert.

## Step 2: what a shared lambda may capture

- A lambda converted to a `shared fn` type is a shared lambda. The target type decides, since no
  syntax marks the lambda itself.
- An escaping shared lambda copies its captures, and several threads read the copies, so each
  capture's type must be sendable and copyable. The copyable rule rejects a unique capture. The
  check belongs with the escaping capture checks in `Binder.Escaping.cs`.
- A non-escaping shared lambda refers to its captures in place, and other threads read those
  places while it runs. It may capture only read-only locals and parameters of sendable copyable
  types, and `in` references and `ReadOnlySpan`s of such data. `FindCaptureProblem` answers the
  escaping case today; the shared non-escaping case needs its own answer, in the same place.
- A shared lambda cannot assign a captured local, call a `mut` method on one, or pass one by `ref`,
  even when the lambda is non-escaping. Check the assignment path in `Binder.Statements.cs`, the
  call path in `Binder.Calls.cs`, and the `ref` argument path there.
- A shared lambda may capture `this` only in a shared class's method. In any other method the
  capture is a compile-time error, including where the escaping capture rule would also reject a
  struct's `this`.
- Each error gets a catalog entry, a page, and a test. Start the codes after WYL0471.

## Step 3: the closure object

- An escaping shared lambda's closure is a shared object, so its last reference may be released on
  any thread. Add `wyl::SharedClosure<Name, Captures...> : wyl::SharedObject` and
  `wyl::shared_closure` beside `Closure` and `closure` in `runtime/wyl/functions.h`. The emitter
  chooses them from `IrClosure.Type.IsShared` in `CppEmitter.Ir.cs` (`ClosureText` and
  `ClosureObjectType`). A lambda that captures nothing owns no closure either way.
- `StackSlots` must not put a shared closure in a slot, as it already refuses a shared class's
  object (`Optimize/StackSlots.cs`). The slot's end destroys its object, which is not how a shared
  object ends.
- The cycle collector must not see a shared closure. Leave the closures of shared lambdas out of
  the cycle graph in `Analysis/CyclicClasses.cs`, say no in `IsCyclicClosure`, and mark the
  interpreter's `ClosureValue` not cyclic so `Released` destroys it at zero
  (`Interpret/Interpreter.Functions.cs`, `Values.cs`, `Interpreter.Cycles.cs`).
- Record the answer in a decision record after 0054. A cycle among shared objects may pass through
  a shared closure, and another thread may refer to that closure, so the collector frees no shared
  closure, for the reason `concurrency.shared-classes` gives for a shared class's object. Add the
  test that pins it.

## Step 4: tests

- A `shared fn` value holds a lambda whose captures are sendable and copyable, is accepted by a
  `T extends sendable` bound, and converts to the same type without `shared`.
- A shared class's field holds a shared function value, since a shared function value is sendable
  and copyable.
- A shared lambda in a shared class's method captures `this`; one in any other method is an error.
- Each capture error: a `mut` capture, a non-sendable capture, a unique capture, a `ref` or span of
  a type that is not sendable, an assignment to a capture, a `mut` call on a capture, and a `ref`
  argument of a capture.
- A non-escaping `shared fn` is not sendable and cannot be stored, as any non-escaping function.
- A shared function value in a shared class's field that captures `this` forms a cycle the
  collector never frees, and a `Weak<T>` breaks it.
- The four forms in `MatrixTypes` where function values appear, a fuzzer mutation that adds
  `shared` to a function type, and a test that prints the release point of a shared closure's
  captures. Run each in the interpreter and in the native modes.

## Still open

- Threads, `Thread`, channels, `Mutex` with the one-lock check, `Atomic`, `Frozen`, and one
  parallel loop built on `shared fn`.
- `Isolated<T>`, whose rule rests on a shared function's sendable arguments making its result
  isolated.
- The `sendable` rows for `std`'s `unsafe` floor types (0036).
- Per-thread pools with remote free lists, and a thread's cycle buffer freed when the thread ends.
- A non-escaping `shared fn` handed to another thread, which only `unsafe` code may do, and which
  the thread library checks when threads exist.
