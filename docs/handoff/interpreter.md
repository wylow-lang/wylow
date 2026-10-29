# The IR interpreter, the universal oracle

**Status:** step 0 done 2026-10-28 (commits `60bb49a`..`bf63b87`, one per shared
definition); step 1 done 2026-10-29; step 2 done 2026-10-29; step 3 done 2026-10-29. Step 4
(unsafe memory and the standard library) is next. Sources:
`docs/design/ir.md` (§4, §9.11), `docs/design/architecture.md`,
`docs/handoff/testing.md` ("The oracles we already have", Step 6), `docs/handoff/remaining-work.md` §4.

## Why

Every oracle today runs lowering. The five modes and the sanitizers agree on what lowering
produced; nothing compares the C++ against the IR's own meaning. `docs/design/ir.md` §9.11 states
the contract the pipeline relies on: lowering plus the mandatory passes decide what a program does,
and no optimization may change it. An interpreter that executes that IR turns the sentence into a
test:

- **Emitter bugs** become disagreements with the interpretation, not just C++ that happens to build.
- **Optimization bugs** are found twice: against a reference that ran none, and by interpreting the
  optimized IR itself and comparing it with that reference, which needs neither the emitter nor a
  C++ compiler.
- **Programs with no expected output** (matrix cells, fuzzer mutants, and later generated programs)
  gain a reference that does not need `// output:` comments.
- It is the oracle the **type-directed program generator** needs, which is the other missing
  oracle (`testing.md` Step 6, order 1 and 2).
- `vpl interpret file.vpl` runs a program with no C++ toolchain, and is how the interpreter is
  debugged.

What it does **not** check: lowering. Every mode and the interpreter lower the same program, so a
wrong evaluation order in lowering makes them all agree on the wrong answer. Expected-output tests,
the reviewed matrices, the value-printing files, and the spec remain the only check for that
(`testing.md`, the note after the oracle table). The interpreter is the meaning of the IR, not of
the source.

## What is interpreted

The interpreter executes any valid `IrProgram` the pipeline produces: one executor for the whole
instruction set. That set is closed under the passes — they rewrite copies, holds, and borrows, and
put objects in stack slots — so the same executor runs both forms of the program. Which form is run
is a question about what is being checked:

- The **oracle** runs the program's meaning: `Compilation.Lower(source, new
  CompileOptions(Optimize: false))`, i.e. lowering, the mandatory passes, and the verifier, with no
  optimization. This is the reference every comparison uses.
- The **pipeline check** runs the optimized IR, the same IR the emitter receives, and compares the
  two interpretations. This checks every pass in isolation from the emitter and C++: a pass that
  changes the answer fails even when the C++ happens to agree with the optimized IR. It uses the
  same observability rule as the modes (below).
- The CLI runs either: `vpl interpret` interprets what `vpl run` builds (the optimized IR), and
  `--no-opt` interprets the meaning. Both are useful when localizing a bug: an emitter
  disagreement that only appears in the optimized program is a pass or emitter problem, and the
  two runs say which.
- In either form the IR may contain `Borrowed` locals, in-place element borrows, slot locals made
  by `new`/`closure`, and slot `IrEnd`; they are ordinary instructions to the executor, not
  special cases to assert away. `Narrowed` checks are erased after the last verification in both.
- A `Release` flag, because integer overflow wraps under `--release` and cycle diagnostics exist
  only in debug builds.
- A step budget and a call/destructor depth budget. A program that exceeds either is
  **inconclusive**, like a timeout, not a failure.

The result is stdout as UTF-8 bytes, stderr text, an exit code, and a status
(`Completed`, `Panicked`, `BudgetExceeded`, `Unsupported`). A panic writes exactly

```
panic: <message>
    at <file>:<line>:<column>
```

and models `abort()`: no destructor runs while unwinding, and the exit code is the platform's
abort code (3 on this Windows machine; matrix snapshots already record `exit 3`).

**Comparison rule, in one helper** (the runner and the fuzzer both call it, so they cannot
disagree):

- The interpreter is the reference for `--no-opt`: stdout, the panic's message and location, the
  exit code, and (in debug builds) `performance warning:` lines must be identical. The interpreter
  mirrors the unoptimized IR's copies, holds, and releases, so even a budgeted
  `Memory.collectCycles` is expected to agree; a difference is a finding, not an exception to the
  rule.
- Against the optimized modes, stdout is compared except for a program containing
  `Memory.collectCycles` (which may legitimately reach other cycles under `--no-opt`), as tier 2
  already does. `// mode: optimized` tests are not compared, and the interpreter mode skips them
  like `--no-opt`.
- Interpretation of the optimized IR is compared with interpretation of the meaning under the same
  rules (stdout may differ for `Memory.collectCycles`, `// mode: optimized` tests are not compared,
  and a disagreement in `unsafe` code is inconclusive). This comparison needs no toolchain and is
  how a pass that changes the answer is found independently of the emitter.
- A program that may run `unsafe` code has no defined behavior to compare when it goes wrong: a
  disagreement there is inconclusive, as in the fuzzer.

The interpret mode checks a test's expectations as any mode does, except that it skips
`// mode: optimized` tests (they observe an optimization) and does not check the `// stack:` and
`// in-place:` markers, which count optimizations, as under `--no-opt`. It runs the meaning for
those expectations, and the optimized IR only for the pipeline comparison.

## Where it sits

A new layer beside CodeGen, not inside it:

- `src/Vpl.Compiler.Interpret` references `Vpl.Compiler.Ir` and `Vpl.Compiler.Analysis` only
  (the transitive Semantics reference comes with them). It cannot see Lowering, Optimize, or
  CodeGen, so it cannot grow a second lowering or borrow the emitter's decisions.
- It receives its facts from the pipeline, like CodeGen: `Optimized.Callees` gives the overrides
  used for dispatch, the cyclic flags, and the lambda decisions. It never builds a fact.
- `Compilation.Lower` is the one entry point that produces everything it runs (with
  `Optimize` on or off); `Vpl.Compiler` wires it, the CLI adds `vpl interpret`, and the test
  runner adds an `interpret` mode.

The architecture gains a layer and these rows in its enforcement table:

| Rule | Mechanism |
|---|---|
| Interpret only IR the pipeline produced | The interpreter takes the IR from `Compilation.Lower`, either form; the pipeline is still the only code that runs passes, and the oracle's mode is `Optimize: false`. |
| The executor cannot tell the two forms apart | `Interpreter.Run` takes the IR, the facts, the release flag, and the budgets — no optimized/unoptimized parameter. A pass changes which fields are populated, not the instruction set, so a rule that would need provenance means a missing definition in the IR model or the spec (fixed there), not a branch in the interpreter. |
| The interpreter's facts are the pipeline's | `Interpreter.Run(IrProgram, Callees, ...)`; nothing in the project can construct a fact, and `Callees.Fresh` rejects one found from an earlier generation. |
| Every instruction and rvalue is interpreted | The interpreter's statement and rvalue executors are `IIrStatementVisitor`/`IIrRvalueVisitor` implementations, so a new kind does not compile until it has a run rule, as `IrEffects` already requires. |
| Every extern the standard library declares, and every builtin, has a handler | A self-test walks the stdlib's `extern` methods and `Builtins.All` and asks the intrinsic registry for each; a missing handler fails the suite. |

## Shared definitions (step 0, done)

The interpreter needs answers the compiler already has. Each got one home before it was used
twice. All six are in; the generated C++ and IR of every test are byte-identical in all five
modes after each commit, and tier-1 fuzzing is clean.

1. **The function an instruction runs.** `IrProgram.Functions` is now an `IrFunctions`
   (`src/Vpl.Compiler.Ir/Ir.cs`) that indexes every lowered function by the symbol an
   instruction names: `Of(symbol)` for all a symbol names (a memberwise constructor's forms),
   `Single(symbol)`, `MemberwiseForms`, `Memberwise(symbol, form)`, `Contains`, `Default(type)`
   for the struct `make_default` under a symbol no call names, and `Defaults` for every one.
   `Callees`, `CppEmitter`, and `CompilationResult.IrText` use it; their copies are deleted.
2. **Lowering without C++.** `Compilation.Lower(source, options)`
   (`src/Vpl.Compiler/Compilation.cs`) returns a `LowerResult(Diagnostics, Program, Ir,
   Optimized)`; `Compilation.Compile` is `Lower` plus the emitter. A program that fails the
   pipeline still returns its `Ir`, with `Optimized` null. No behavior change.
3. **The implementation a dispatched call runs.** `Overrides.Implementation(MethodReference
   call, VplType runtimeType)` returns the `MethodReference` that runs, with the runtime owner's
   type arguments (so a generic owner's `TypeMap` is right), or null if the type has no method.
   It walks the class chain, and a struct takes its own method. An abstract method's answer is
   the method itself, whose stub panics during construction or destruction.
   `tests/Vpl.Tests/DispatchSelfTests.cs` covers overrides, the base's own method, direct and
   inherited interface implementations, structs, generic owners, and the abstract case.
4. **A formatted type.** `VplType.IsFormatted` (`src/Vpl.Compiler.Semantics/TypeFormatting.cs`),
   derived from the one list of formatted values, is what the binder, `StdlibSymbols`, and the
   interpreter ask. `TypeSelfTests` has a `formatted_types` property.
5. **A separate destructor.** `TypeSymbol.HasSeparateDestructor`
   (`src/Vpl.Compiler.Semantics/Symbols.cs`) carries the emitter's rule, with the spec sentence
   it implements (03-memory-model.md, Destroying a cycle), for the interpreter's collector.
6. **Runtime message and location text.** `Location.PanicText`
   (`src/Vpl.Compiler.Syntax/Text/SourceText.cs`) is the `file:line:column` text, now used by
   the emitter, `IrPrinter`, and a lambda's name. `FunctionSymbol.AbstractCallMessage` is the
   abstract-call stub's text, so the interpreter can raise exactly what the emitter does. The
   other runtime-header messages (arithmetic, checks, `enum from_value`, `to_char`, ...) are
   still the emitter's or `runtime/vpl/*.h`'s; porting them into the interpreter's own
   `RuntimeText`/`RuntimeArithmetic` was part of step 1, and `tests/cases/panics/` already pins
   them.

## The semantic model

The interpreter is a second implementation of the IR's operational semantics; the emitter is the
first. Write it from the spec and `docs/design/ir.md`, not from the generated C++, and let the
differential suite settle disagreements. The following decisions are what keep it tractable.

**The executor is provenance-blind.** A pass changes the program, not the instruction set: it
rewrites a `copy` into a `borrow`, makes a local `Borrowed`, fills an `IrNew`'s `Slot`, or marks a
read `Element`. The executor's one rule for each instruction covers every field of that
instruction, so it executes the optimized IR with the same code, counts and all. A form that ever
seemed to need a different rule would be a missing definition in `Ir` or the spec — put it there,
not behind a mode flag. Only two things legitimately depend on provenance: which IR the harness
asks for, and which comparison exceptions it applies.

### Values, storage, and places

- Every place is a **`Cell`**: a type and a value, mutable in place. A struct, fixed array, enum
  payload, nullable value, and object field is itself a small tree of cells, so a `ref` to a field
  is a reference to the same cell after any number of projections.
- Values: the boxed primitive of the VPL type (with `char` as a code point), `String` as immutable
  UTF-8 bytes, `StructValue`/`EnumValue`/`ArrayValue`/`OptionalValue` over cells, `ObjectRef`,
  `FunctionValue`, `PointerValue`, `WeakValue`, and the deferred `Formatted` value that
  interpolation consumes.
- **Copy, move, release, default, and equality of a value are defined once each, driven by
  `VplType`** and its existing facts (`InlineTypes`, `MayOwnObjects`, `IsCounted`, `HasDefaultCode`,
  `SupportsEquality`, `SharesStorage`): the interpreter must not grow its own type predicates.
- A **move** takes the value and leaves the source moved-from (empty), so its later release does
  nothing. A **copy** of an aggregate copies every field, in declaration order. A **release**
  releases fields in reverse declaration order, as C++ destroys members, then a base's fields, so
  a destructor that prints sees the same order. `String` copies share bytes; `Weak` copies adjust
  its block count.
- **The store rule** is the spec's: store every part of the new value, then release the old one
  (`docs/spec/03-memory-model.md` §3.2). Aggregates store all fields before releasing any old
  field. The exact order in which the old aggregate's fields are released is currently the C++
  `vpl::assign` swap; if a test can observe an order the spec does not state, **decide it in the
  spec** and make the runtime and the interpreter both follow that sentence.

### Frames, blocks, and release order

- A frame holds a cell per `IrLocal`. `IrDeclare` and `IrLet` declare; a block releases the cells
  declared in it, in reverse declaration order, on every way out (`break`, `continue`, `return`,
  fall-through). A **panic runs no cleanup**: it throws straight to the top, because `abort()`
  unwinds nothing. Cleanup is explicit code, never a C# `finally`.
- An `IrLet` of a local that is already declared (a declared hold) is an assignment: release or
  end the old value, then take the new one.
- At function exit: the body block's locals first, then the by-value parameters in reverse order.
  `this` is not owned (a class's is a borrowed handle; a struct's is the caller's cell), and a
  `ref`/`in`/`ref?`/`in?` parameter is a reference, never released.
- A `Borrowed` local is a read-only alias of a cell, and an in-place loop element's read is the
  same borrow; neither counts nor releases anything. The passes introduce both, so the executor
  handles them as ordinary forms rather than as the unoptimized program's leftovers.

### Calling convention (must mirror C++ parameter destruction)

| IR argument | In the callee |
|---|---|
| `IrRead` (by value) | owned copy; released at return |
| `IrRefArgument`/`IrAddrArgument` | a reference to the caller's cell, or null |
| struct receiver (`IrRefArgument`, `IrRead.Borrow` of a hold) | `this` is the caller's cell |
| class receiver (`IrRead.Borrow`) | `this` is the object, borrowed, not counted |
| non-escaping function | the function value, no ownership |
| escaping function | owned; the closure's count is released at return |

`IrRead.Borrow` returns the place's value without counting; `IrHold` decides its own lifetime from
the hold's kind: a class hold retains (and `hold.borrow` also calls `beginBorrow`), a struct hold
copies the struct, a type-parameter hold does whichever its runtime type needs (`auto&&` in C++).

### Objects, dispatch, and destruction

- Objects have a class (with type arguments), fields, an intrusive count, a cyclic flag, and a
  weak block. `new` allocates zeroed fields and starts at 1, then runs the constructor IR.
- **Release**: at zero, clear weak targets, hold one count, run the most-derived destructor body,
  then release that class's fields, then the base's destructor body and fields, and last check
  that no reference to the object survived (`"an object's destructor kept a reference to the
  object"`).
- **Dispatch uses the object's current construction phase**: during a base constructor and a base
  destructor the dynamic type is the base, exactly as C++ does. A call that lands on an abstract
  method there panics `abstract method '<Owner>.<Name>' called during construction or destruction`
  (the emitter's stub text). `tests/cases/panics/abstract_in_constructor.vpl` pins it.
- **Constructors** execute the IR as written: the base call block, field initializers, body; a
  struct constructor's `Struct` local; `make_default`/memberwise `Builds` forms chosen from the
  call's `IrFieldDefault` arguments. `IrDefault` runs `make_default` where the type has default
  code and zeroes otherwise.
- **Cycle collector**: mirror `runtime/vpl/cycles.h` exactly. Buffer a cyclic object when a
  release leaves its count above zero; trial-delete from the buffer with the same budget rule;
  clear weak targets; run destructor bodies oldest first with every field intact (the finalize
  phase); then release fields and memory, where a standard library container's destructor body
  releases its elements; enforce the collector's resurrection panic; update `CycleStats` and the
  debug diagnostics (growing buffer, repeated class, at-exit uncollected) with the same messages.
  `tests/cases/cycles/` pins the order.
- **Stack slots** are what `StackSlots` introduces. `IrNew`/`IrClosure` with a slot bind the object
  to the slot local, which holds the reference for the object's whole life; `IrEnd` or the slot's
  block destroys it. The object leaves the collector, so it is never buffered or visited, and
  `cycleStats().buffered` and the diagnostics match the optimized build
  (`cycles/stack_unbuffered.vpl`). This is exactly why the interpreter cannot assume the
  unoptimized instruction set: the optimized IR is a valid program in its own right.

### Generics, functions, and views

- A frame carries the call's `TypeMap`; every type read from the IR is applied before a
  type-directed operation (default, construct, convert, test, copy, release). Nothing is
  monomorphized in C#.
- A non-escaping function value is (lambda, creating frame). An escaping closure is an object
  holding its captures, made only when there are captures (a capture-free closure allocates
  nothing), with `IsCyclicClosure` deciding buffering. A `choose` value owns every callable it
  could select when its type is escaping, exactly as the C++ `Choice` does. Invoking a choice
  calls its chosen option.
- `IrRef`/`IrAddr`/returned references are cells; `ref?`/`in?` may hold null.
- `data P` gives a pointer into a fixed array's element cells; `Memory.allocate` gives a typed
  buffer of cells with initialized flags. Pointer arithmetic scales by element; `initialize`,
  `initializeDefault`, `drop`, `moveTo` (including overlapping ranges), and `free` follow
  `runtime/vpl/intrinsics.h`. `String.bytes()` returns a span built by calling the span's own
  constructor IR, not by knowing its layout.
- A type parameter receiver of `Printable.toString`/`Comparable.compareTo` uses the concrete type:
  formatted types are written by the runtime, everything else dispatches to its method.

### Text, numbers, and intrinsics

- `print`, interpolation, format specifiers, `toString`, and `compareTo` are the intrinsics
  `vpl::print`, `vpl::interpolate`, `vpl::formatted`, `vpl::format_string`, `vpl::compare`.
  Integers are decimal; a `char` prints as UTF-8; a float prints the **shortest text that reads
  back exactly**, with `.0` on a whole number, in `std::to_chars`'s choice of plain or exponent
  form, and `inf`/`-inf`/`nan` (`docs/spec/06-syntax.md`, String interpolation; strings.h). C#
  supplies the shortest digits; the exponent layout is the interpreter's. A golden format table and
  `tests/cases/values/*.vpl` are the check.
- Integer arithmetic reproduces `arithmetic.h`: checked in debug and wrapping in release, division
  and `MIN / -1` always panic, shifts mask, float→int saturates (NaN → 0), double→float rounds,
  `int→char` and `int→enum` panic. Comparisons use the total order for floats.
- `String`'s extern methods are implemented over UTF-8 bytes with byte offsets, including the
  slice bound and character-boundary panics.
- **Intrinsics are keyed by the C++ runtime name the compiler already uses** (`BuiltinCppName`, or
  the containing extern struct and method name). The registry test above makes a new extern or
  builtin a compile-and-test-visible requirement, not a runtime surprise.

## Steps

Each step passes the existing suite in all five modes and commits on its own. The interpreter mode
is opt-in (`--interpret`, or `--cc interpret`) from step 1 and joins `--all-modes` when every
non-error, non-`mode: optimized` test passes (step 5). Unsupported features fail the mode; a
step's filter names the tests it must already pass, minus the ones that reach a feature a later
step lands (a container, a closure, `Weak`), which move to that step's filter.

**Step 0 — shared definitions (done 2026-10-28).** The six items above, with no behavior change:
the `.cpp` and `.ir` of every test are byte-identical in all five modes after each of the six
commits.

**Step 1 — the core (done 2026-10-29).** `src/Vpl.Compiler.Interpret` has `Values` (cells, the
value kinds, and the one copy/release/default/equality definition driven by `VplType`),
`Interpreter` (frames, locals, calls, returns, if/loop/break/continue, block release order, the
`@CallerLocation` text), `Interpreter.Values` (operands, places, and the rvalue visitors),
`RuntimeText`/`RuntimeArithmetic` (the runtime's text, checked arithmetic, and conversions),
`Interpreter.Intrinsics` (the builtins, `Bounds`, and `String`'s non-span members), and
`Interpretation` (the result, statuses, and budgets). The CLI has `interpret`; the runner has
`--interpret`/`--cc interpret`, which runs the meaning, skips `// mode: optimized`, checks a
panic's message and location, and treats a budget exhaustion as inconclusive. `InterpreterSelfTests`
has 30 cases, one or more per instruction kind and per panic message.

What the filters still reach, and so moves to the step that lands the feature: `values/string` and
`basics/strings` need `String.bytes` (step 4); `enums/*` mostly construct classes (step 2);
`basics/arguments`, `interpolation`, `mutability`, and the string loops use classes (step 2);
`panics` has objects, lambdas, and the cycle collector. The whole suite in the interpret mode is
298 passed and 122 `Unsupported`, with no disagreement in any program the step covers.

Where step 1 started:

- **The project.** `src/Vpl.Compiler.Interpret`, referencing `Vpl.Compiler.Ir` and
  `Vpl.Compiler.Analysis` only (the Semantics reference comes with them), added to `vpl.slnx`.
  `src/Vpl.Compiler` references it to wire `Compilation`/CLI, as it does `Vpl.Compiler.CodeGen`,
  so the interpreter project itself must not reference `Vpl.Compiler`.
- **The entry point.** `Compilation.Lower(source, options)` gives `LowerResult.Ir` and
  `LowerResult.Optimized` (its `Callees` carries `Overrides`, `Cycles`, and `Lambdas`). The
  oracle runs `Optimize: false`; the pipeline check runs the optimized IR and compares the two
  interpretations.
- **The CLI.** `src/Vpl.Cli/Program.cs` lists its commands and usage text; add `interpret` (and
  its `--no-opt` meaning, which already exists) there.
- **The runner mode.** `tests/Vpl.Tests/Program.cs` parses `--cc`, `--no-opt`, and `--no-nest`,
  and `RunAllModes` lists the five modes; the interpret mode is opt-in from step 1 and joins
  `--all-modes` only at step 5. `README`-style self-tests are registered in
  `results.AddRange(...)` there (`DispatchSelfTests` is the newest example).
- **The instruction set.** Every instruction and rvalue kind already has a required visitor
  (`IIrStatementVisitor`, `IIrRvalueVisitor`, and `IrEffects`' two), which is the list the
  executor must implement; `IrFunctions` answers which function a call, `new`, construction,
  or `default` runs, and `Overrides.Implementation` which method a dispatched call runs.

**Step 2 — objects and ownership (done 2026-10-29).** Reference-counted objects, fields,
constructors and `make_default`/memberwise forms, destructors and release order, dispatch and the
construction phase, holds and borrows, `ref`/`in`/`ref?`/`in?` places, `end`, store/assign
semantics, borrowed locals and in-place element reads. Filters: `classes`, `structs`,
`references`, the remaining `panics`.

`Values.ObjectRef` is the object: a cell array per class of its chain, an intrusive count, and the
class a dispatched call runs as. Copy retains it; release lowers the count and, at zero, runs each
class's destructor body and then releases that class's fields, most derived first, holding the
count at one throughout so the destructor's temporaries cannot free it, and panicking when one
survived. `new` allocates zeroed fields and runs the constructor as C++ constructs: the base
constructor first (whose own frame runs the base's body), then the class's field initializers and
body, with a dispatched call resolving on the base until the base constructor returns, so an
abstract method called from a base constructor or destructor panics exactly as the emitter's stub
does. Dispatch resolves a call on an object's current class, and on a struct value's own type,
which a type parameter's substitution made concrete. A hold is a counted reference, and
`hold.borrow` also calls a `Borrowable`'s `beginBorrow`/`endBorrow`; `ref`, `in`, `ref?`, and
`in?` places, `end`, and stored assignments all work on objects. A formatted type and the built-in
comparable types answer `Printable.toString` and `Comparable.compareTo` the way
`vpl::to_string`/`vpl::compare_to` do, since they satisfy a bound without implementing it.
`InterpreterSelfTests` grew to 40 cases: construction/destruction order and phases, dispatch
through a base and an interface, assignment storing before releasing, borrow hooks, references
into objects, and the abstract-call and kept-reference panics.

Two executor rules step 2 settled, both of which C++ already did: an operand a pure operation only
reads (a comparison, a test, a check) keeps its local owning it even when the IR says `move`, so it
is released at its block's end as the emitter's temporary is; and an object dispatches as the
class its construction or destruction has reached, which is also what a `vpl::is` test sees.

What the filters still reach, and so moves to the step that lands the feature:
`classes/assignment`, `last_use_moves`, `pinning`, and `weak` need `Weak` (step 5);
`classes/base_references` needs pointers (step 4); `classes/stack_*` and `references/nullable`
need function values or slots (steps 3 and 5); the `list_*`, array, and `memory::*` panics need
the standard library (step 4). The whole suite in the interpret mode is 345 passed and 85
`Unsupported`, with no disagreement in any program the step covers.

**Step 3 — generics and function values (done 2026-10-29).** Frame type maps that compose with
the caller's, non-escaping lambdas that read their creating frame, escaping closures that count
and release their captures, choices, and invocation of a function value.

`Frame` carries the type arguments its body runs with, and a callee's are now `caller.Map.Compose(
callee.Map)` (`TypeMap.Compose`): a generic method that calls another with its own type parameters
as the arguments (`Pair<B, A> swapped()` inside `Pair<A, B>`) would otherwise run with `B` as its
own field type. A non-escaping lambda's frame is the creating frame's map with that frame as its
`Enclosing`, so its body finds the locals around it as places and their type arguments as they
were; an escaping closure carries the map and the copies of what it captured, and its body reads
the closure's cells by reference, which is why a capture is released by the closure and not by
the call.

`Values.FunctionValue` is the lambda's `IrFunction`, its map, either the creating frame
(non-escaping) or a `ClosureValue` (escaping), which counts like an object. `Values.ChoiceValue`
holds the index and every callable, owns them, and copies them with the choice. A `copy` of a
function value counts only where the type is escaping: the non-escaping form refers to a closure it
doesn't own, as `vpl::FunctionRef` does, and converting between the forms changes nothing about the
value (`RuntimeArithmetic.Convert`). Invoking reads the callee where it is — never copying a
function value or a choice, which would hold counts C++ never takes — and runs the chosen option of
a choice or the lambda's IR.

Two executor rules step 3 settled, both of which C++ already did:

- An operation that only reads an operand (`test`, a comparison, a check, a choice's index) reads
  the place in place: the `copy` the IR names makes no count. The interpreter had been copying and
  never releasing, which the first test to narrow a nullable object found.
- A closure releases what it captured, and a choice its callables, in the order they are held, not
  in the reverse order a struct's fields take, because the runtime holds each in one `std::tuple`.
  Both tested compilers destroy a tuple forwards, and `03-memory-model.md` now says so;
  `lambdas/capture_order.vpl` pins it.

`InterpreterSelfTests` grew to 50 cases: non-escaping lambdas through templates and over the
creating frame, closures over `this` and their release, composed type maps, function-typed fields
and nullable functions, choices, function references (a plain function, a static method, `ref`
parameters), interface dispatch to a lambda, `Printable`/`Comparable` through a type parameter, and
a closure over a type parameter.

What the filters still reach, and so moves to the step that lands the feature: `lambdas/captures`,
`contexts`, `element_writes`, `escaping`, `escaping_lifetimes`, `escaping_parameters`, `inference`,
`members`, `recursion`, `references`, and `safety` build a `List` or `Array`, so they need
`Memory.allocate`; `generics/comparable` and `generics/views` need spans. Both are step 4. `cycles/*`
and `lambdas/stack_closures` need the collector and stack slots (step 5). The whole suite in the
interpret mode is 373 passed and 68 `Unsupported`, with no disagreement in any program the step
covers.

**Step 4 — unsafe memory and the standard library.** Typed buffers and pointers, fixed-array
`data`, spans, `Memory`/`Bounds` intrinsics, `String`'s extern methods, `bounds` panics, and the
calls that make `Array`/`List` work. Filters: `containers`, `spans`, `arrays`, `unsafe`,
`programs`.

**Step 5 — Weak, slots, and cycles.** Weak blocks and `get`; stack slots and their collector
interaction; the collector mirror, its stats, and its debug diagnostics; at-exit reporting.
Filters: `cycles`, and then the whole suite. Add the `interpret` mode to `--all-modes`, the
matrix run, and the optimized-IR comparison.

**Step 6 — harness and hardening.** Matrix cells run interpreted against the same snapshots;
the fuzzer gets an interpreter mode in tier 2 (interpreter vs `--no-opt` first, then the optimized
modes, with the shared comparison rule) and the optimized-vs-meaning interpretation check in
tier 1, which already compiles both forms — a pass bug then fails without any C++ build. If
throughput allows, tier 1 also gets "the interpreter neither throws nor disagrees with itself".
Add the interpreter's files to the mutation targets with the interpreter filters. Measure
coverage. Update `CLAUDE.md` (six modes), the `docs/design/architecture.md` layer and enforcement
tables, this file's status, and the spec for any order or message the interpreter had to decide.

## Testing the interpreter itself

- **Self-tests** (`tests/Vpl.Tests/InterpreterSelfTests.cs`): small source programs with expected
  stdout, panic text and location, or exit code, one per instruction kind and per runtime message;
  release vs debug overflow; budget exhaustion as inconclusive. These are the only tests that
  distinguish "the interpreter is wrong" from "the program is wrong" when no native mode is run.
- **The interpret mode over the suite** is the broad check against expected outputs.
- **The optimized-IR run**, compared with the meaning for every program that compiles, is the
  pass-regression check; it runs before any C++ exists, in the runner and in fuzzer tier 1.
- **The matrices** are the reviewed spec check, now for the interpreter too.
- **The fuzzer** is the cross-check nothing written by hand reaches; its findings are triaged like
  any other.
- **Mutation testing** the interpreter's source is how its branches are shown to matter; the
  mutation harness already supports any file with a filter, so this needs targets, not a new tool.
- **Coverage** extends `tools/coverage.sh` to the new project at no extra cost, since the
  interpreter runs in the test process.

## Open questions and risks

- **Two definitions of instruction meaning.** `IrEffects` says what an instruction may write, run,
  and panic on; the interpreter says what it does. The differential suite and
  `IrSelfTests.Effects` keep them honest, but a rule only one of them knows is a bug source. A new
  instruction kind must add both, and a self-test case for each.
- **Float text.** The shortest round-trip digits are standard; the choice between plain and
  exponent form and the `.0` rule are the runtime's. Validate against a table generated from
  `std::to_chars` for the edges in `values/double.vpl` before trusting the general case.
- **`Memory.allocate`'s "allocation is too large"** needs C++ `sizeof(T)`. Either model sizes the
  ABI way and test, or decide the case is out of scope with a comment and no test. Do not let it
  silently differ.
- **Aggregate assignment release order** is not stated in the spec (see above); decide it when a
  test can see it.
- **The abort exit code** is the platform's; the comparison helper uses "panicked" rather than a
  fixed code where it can, and the matrix snapshot keeps the platform's number.
- **Performance.** A tree-walking interpreter is enough for the suite's ~400 small programs and
  matrices, but `cycles/values.vpl` runs 100,000 iterations and the fuzzer wants throughput. Start
  simple; profile before deciding whether to pre-resolve places or compile bodies to delegates.
- **When the mode gates commits.** Do not add it to `--all-modes` early with a skip list; that is
  how a whole oracle rots. Keep it opt-in until step 5's criteria pass.
- **Diagnostics under optimization.** Interpreting the optimized IR can surface a difference the
  spec treats as cost but a program can read (`cycleStats().buffered`, with borrows replacing
  copies). Stack objects are the documented exception; anything else is a finding to decide in the
  spec, not a comparison exception to add quietly. The shared rule must name every exception.

## Exit criteria for the interpreter

- Every non-error test not marked `// mode: optimized` passes in the interpret mode, with the same
  stdout, panic text and location, warnings, and exit code as its expectations; the matrices pass
  against unchanged snapshots.
- Interpreting the optimized IR agrees with interpreting the meaning on every program that
  compiles, under the documented exceptions, on the whole suite and a fuzzer budget.
- The fuzzer's tier 2 finds no disagreement between the interpreter and `--no-opt` over an agreed
  budget, and no design exception was added to the comparison.
- The intrinsic registry test covers every stdlib extern and builtin; no `Unsupported` result
  remains in the suite.
- Each interpreter source file has a mutation score with every survivor triaged into a self-test or
  a deleted line.
- `CLAUDE.md`, `architecture.md`, and the spec state the new mode, layer, and every semantic
  decision the interpreter forced.

## Follow-on: the type-directed generator

Not this plan, but it is why the interpreter comes first. Sketch, for its own handoff:

- A deterministic, seeded generator of **well-typed VPL source** (a typed generator that prints;
  the parser and binder validate every program; a generated program that fails to bind is a
  generator bug). Start with expressions, locals, and control flow with bounded loops; add
  structs/enums/nullables/fixed arrays, then classes and destructors, then generics, lambdas, and
  the standard library containers.
- No expected outputs: run the interpreter and every native mode, and compare with the same rule
  as the fuzzer, `--no-opt` first. Exclude `unsafe` from generation (undefined behavior would make
  a disagreement inconclusive).
- The interpreter's step budget doubles as the generator's only termination guarantee, so generate
  loops with statically bounded counts.
- Reuse the fuzzer's reducer and findings format; a located finding becomes a test in
  `tests/cases/`, and the question "what structure would have made this impossible?" is answered in
  the same change.
