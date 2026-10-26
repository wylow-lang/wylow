# IR migration, step 3: continue lowering

Continue step 3 of `docs/design/ir.md` §7: lowering plus an IR emitter behind `--ir`. Read
`CLAUDE.md`, then `docs/design/ir.md` (§3, §5, §6.2, §7, §9), then skim the files below. Don't
re-read the whole emitter or spec up front; read what each construct you lower needs.

## Where it stands

Commits `728ed34` (straight-line code, calls, places, holds), `28e7901` (control flow), `d2d151e`
(patterns, switches, `?.`, `!` of a nullable value), `ba9b569` (stack slots, and collected cycles
destroyed oldest first), `482bd18` (loop elements borrowed in place), `05543b9` (lambdas,
closures, choices, calls of function values), `320267b` (pins last until the statement
completes), `2dfadd3` + `593332f` (constructors and super calls), and the commit after them
(a struct's `make_default` and memberwise constructors, 3b), `c81f67f` (`try`, lifted binder
restrictions, `@CallerLocation`), and `b1624d6` (`is ref`). The working tree is clean apart from
this file.
**Steps 3 and 4 are done** (step 4: `0bc00c8`). Every function body lowers, and lowering, the
verifier, and the IR emitter are the only path: `--ir`, `// mode: ir`, `NotLoweredException`, and
`CppEmitter.Expressions/Statements/Ordering/Patterns/Lambdas`'s bound tree half are gone
(`Statements.cs` is now `Functions.cs`). Decided in step 4, with the user where noted:
- (user) A `super(...)` argument for a `ref`/`in`/`ref?`/`in?`/view parameter must be `null`, a
  constructor parameter, or a field of one, for now (`Binder.IsHeldByCaller`, spec 02-types.md
  Construction note). Lifting it means the delegating constructor's `vpl::Arguments` owning the
  holds and temporaries the references need, released when the base constructor returns.
- A lambda in an `@CallerLocation` function reports its own locations (spec 06-syntax.md). The
  old emitter printed the caller's, which didn't compile for an escaping lambda.
**Step 5 is done** (`08502c9`, `66e472a`, `bb95271`, `8553dad`), with one decision from the user:
optimizations are separate from lowering (ir.md §9.11). Lowering emits only meaning, every
optimization is a pass in `src/Vpl.Compiler/Optimize` (off under `--no-opt`, which replaced
`--no-elide`), and the unoptimized IR is to be the input of a future interpreter used as an oracle.
`InPlaceElements` and `StackSlots` are passes; facts are `Analysis/Callees`, `Writes`, `Escapes`.
The verifier re-checks borrowed loop elements with `Writes` (`IsUnwrittenElement` is gone; it is
`IsElement`). The spec's Stack allocation section now only states what makes it legal;
docs/design/stack-objects.md has the rules. Porting found two places where the old analysis made
stack allocation observable (a temporary in a `?:` branch, and slots at the start of a statement),
now fixed.
**Hold elision is a pass** (`b025783`): `Optimize/HoldElision.cs`, first of the passes. It also
flattens the statement scopes lowering made only for holds it elided (and turns Define's
`declare x` + `{ ...; store x = v }` back into `let x = v`); output was byte-identical in every mode.
**Step 6, field-precise write summaries, is done** (`6d61e91`): `WriteSet` knows the fields code
may store to and the types it writes through unseen storage; hold elision and the verifier's
`CodeWrites` use `Writes.CodeMayWrite`. Pins in `tests/.out`: 43 to 35.
**Step 6, destructor precision, is done** (`91e186e`): `IrRunRelease` has the released type,
every implicit release is explicit (a block's end, `IrEffects.Ending`; a callee's by-value
parameters, in `Writes`), `Callees` resolves a release through `CyclicClasses.Destroyable`, and
`Callees.ExternRuns` says what `Memory`'s externs run (see ir.md §7 step 6).
**Step 6, borrowed locals, is done** (`Optimize/BorrowedLocals.cs`, ir.md §6.3 and §7). **Next:
last-use moves** (§9.10); struct-valued locals and `?.` receivers could also borrow.

Notes for borrowed locals, from planning at the end of the last session:
- Rule: `let x = copy P` becomes `let x: borrowed = borrow P` when nothing in the rest of x's
  block, including that block's end releases (`IrEffects.Ending`), may change P or what the
  aliases in its path borrow. The extent must run to the scope's end, not x's last use: with a
  copy, x keeps the object alive until its scope ends, so a change to P after the last use would
  make a destructor run earlier.
- Skip loop elements (`InPlaceElements` handles those), parameters, captures, locals a lambda
  assigns, and any x that is later stored, moved, or referenced as `ref`.
- `HoldElision`'s alias map and `Chain`/`MayChange` would be shared; consider a small helper class
  in `Optimize/`. Run it after `HoldElision`, so the holds it decided on stay valid; the verifier
  catches a wrong combination (e.g. a borrow dropped because x was still a copy then).
- Measure it by counting the lets the pass turns into borrows in `tests/.out`.
Don't ask the user about restructuring docs (they will rewrite all docs later); just keep the
information in them.

Previously: **§7 step 5: port the analyses onto the IR.** The emitter still runs bound-tree analyses
of its own (`FindRecursive`, `FindUnoverridden`, `FindOverloaded`); `FindRecursive` belongs with
the step 5 ports. The sections below describe step 3 and are kept for reference.

Sub-step 4, as done:
- `try` lowers in `Lowering.ControlFlow.cs` (`Try`): the operand is matched in place like a
  pattern's (`Matched`), and an `Err` returns `variant Err(move/copy %r.Err.0)` from wherever it is.
  The user chose to lift the binder's restrictions now: spec 04-errors.md §4.3 says `try` may be
  anywhere, with an "Implemented so far" note that it compiles mid-expression only with `--ir`. The
  old emitter throws a clear `InvalidOperationException` on such a `try`. `enums/try_expressions`
  is `// mode: ir`; drop the directive in step 4, along with `base_arguments`'s, and the note.
- `@CallerLocation`: `IrLowering.Loc(location)` gives `IrLoc.Caller` in such a function. A lambda
  in it reports its own locations, as the old emitter does.
- `is ref` (`Lowering.Patterns.cs`, `ReferenceTest`): the receiver's hold is declared with the
  name (a declared hold, made by its `let`, possibly again each loop iteration) and ended by
  `if !found { end %h }` right after the test. The verifier accepts the end only where the reference
  is known null (`nullTests`/`knownNull`). Elision skips these holds (`holders`): a polish item, since
  a pin of a local nothing reassigns could become an alias (and its `end` would go too).
- Verifier changes that came with it: a copy of a reference depends on what it points into, not on
  the copied temporary; a whole store clears the target's invalidity; `IrLocal.IsChangedByLambda` is
  false for references (a lambda storing through a `ref?` doesn't reassign it; this surfaced once
  `references/nullable.vpl`'s `main` lowered).

- `src/Vpl.Compiler/Ir/Lowering*.cs` (`IrLowering`): lowers per function. Anything it can't lower
  throws `NotLoweredException(reason)`, and that function is emitted from the bound tree as before.
  - `Lowering.cs`: entry point, statements, statement scopes (`InStatement`, and `Define` in
    `Lowering.ControlFlow.cs`). A statement's temporaries go in a block of their own only if one of
    them may run code when released; otherwise they're spliced into the enclosing block. A local whose
    initializer has such temporaries becomes `declare x` + `{ ...; store x = v }`.
    Stack slots: `DeclareSlots` declares a kept object's slot before its local's declaration, and a
    temporary's at the start of its statement's scope (passed to `InStatement`/`Define`). `slots`
    maps each `BoundNew` to its slot; `filled` tracks which `new`s are lowered, so an assignment's
    `end`s (from `EndedBy`) skip slots not filled yet. Ending an ended slot is a no-op (a loop's
    assignment does it each iteration), and the verifier allows it.
    Loop elements in place: `Declaration` makes a `Borrowed` local with `IsUnwrittenElement`.
  - `Lowering.Places.cs`: places via `PlacePath.Of`. The objects a place goes through are always
    held (`hold`, or `hold.borrow` for a `Borrowable` that hands out storage). `x!` of an object
    place is checked and then held in place (not copied). `Close(block)` then elides holds (§6.2)
    using the extent up to the last use of the hold or anything pointing into it. A borrow only
    needs to cover what runs after the first reference exists, **unless a named local (a `ref`/`in`/span
    variable, or a loop element) points into it**: the spec's table has those last for the scope.
    A pin needs to cover the whole extent. Reads are left in the instruction that uses them
    (`Read`), and `Settle` copies one into a temporary at its original position if something
    lowered since may write it.
  - `Lowering.Expressions.cs`: values, rvalues, calls, receivers, arguments, and the `vpl::keep`
    rule (`Kept`). `IrData` (a span of a fixed array) holds its root at least `InUse`.
  - `Lowering.ControlFlow.cs`: `if`, `while`, `for`, range loops, `break`, `continue`, and `&&`,
    `||`, `?:`, `??` via `Chosen`, and `?.`.
  - `Lowering.Patterns.cs`: patterns, `is`, switches (an `if` chain ending in `check.no_match`).
    Names a statement's patterns bind are `IrDeclare { IsBinding }` before it.
- `src/Vpl.Compiler/Ir/IrAccess.cs`: `IrAccess`, `IrFootprint` (§5.1 conflict rule), `IrWalk`.
- `src/Vpl.Compiler/CodeGen/CppEmitter.IrNesting.cs`: the nesting plan (which temporaries print
  inside their one use, `vpl::at` fusion, initialized declarations).
- `src/Vpl.Compiler/CodeGen/CppEmitter.Ir.cs`: prints a lowered body. Slots print as
  `vpl::StackSlot<T> s;`, `s.make(args)`, `s.end();`. `IrEnd` of a hold still throws (needed for
  `is ref`).
- `src/Vpl.Compiler/Ir/Lowering.Constructors.cs`: a class constructor is its base call
  (`IrFunction.BaseCall`, the body's first statement), the field initializers as stores (a
  synthesized `BoundAssignment` each), then the body. Each `super(...)` argument needing statements
  is `declare a` + a block storing it; one needing none (a constant, or a read of a stable place)
  is passed directly. A struct constructor has `IrFunction.Struct` (`self`, `let self = default T`,
  which runs the initializers) instead of `This`, and returns `copy self`; the verifier checks that.
  A struct's `make_default` and each form of its memberwise constructor a call uses (`make_0101`)
  are `IrFunction`s with `Builds` (which fields the form passes; `make_default` passes none, under
  a synthesized symbol named `default`). Each starts `let self = zeroed T` (`IrZeroed`, allowed
  nowhere else), stores each field its argument, initializer, or default unless that is zeroed
  storage, and returns `copy self`. The verifier checks nothing reads `self` before the return and
  every field whose zero isn't its default is stored. The emitter finds them in `irBuilt` by type
  and C++ name; the `.ir` lists them after the functions as `Vec.default`, `Vec.Vec[01]`.
  `CppEmitter.IrConstructors.cs` prints the base call: at most one computed argument nests or
  becomes an immediately invoked lambda inside `Base{...}`; with two or more, the constructor
  delegates to a `template <class A> C(vpl::BaseArguments<N>, A&&)` declared in the class, passing
  a `vpl::Arguments` a lambda computes in order (runtime `core.h`).
- `src/Vpl.Compiler/Ir/Lowering.Lambdas.cs`: a lambda is lowered by the same `IrLowering`, which
  swaps `function`, `symbol`, `self`, `block`, and `loops` while it lowers the body; its
  `IrFunction`s go into `IrProgram.Functions` after the enclosing one. A closure's captures are
  `IsCapture` locals (printed `l_x_tmp_N`, `vpl_this_tmp_N`). Non-escaping function arguments are
  passed before their conversion (the verifier's `PassesFunction`), and the emitter's
  `FunctionArgument`/`OptionalFunctionArgument` converts them at the call. A choice is `IrChoose`,
  its index computed first, then its lambdas made. `IrWalk.Footprint` doesn't look into lambda
  bodies (making one does nothing); `Extent` and the verifier's `Captured` dependency follow a
  lambda to the call that runs it.
- Every hold a statement makes (pins of receivers and of the objects holding `ref`/`in`
  arguments, and borrows) is released with the statement's temporaries, in reverse order of
  creation; nothing ends one early. A place read right away (`Use.Read`) is never borrowed.
- A non-escaping local whose value has temporaries is split by `Define` into `declare` and a
  block storing it; the emitter prints that as `auto x = [&] { ...; return v; }();`
  (`NestingPlan.Evaluated`), since its C++ type can't be declared first.
- `Compilation.Compile(source, CompileOptions)`: with `--ir`, a verifier error throws, and the
  message now includes the IR of each rejected function (so `vpl ir file.vpl` shows it even then).
- Runner: `--ir [--no-nest] [--no-elide]`; writes `tests/.out/<group>/<name>/<name>.ir` and prints
  "N of M lowered" grouped by fallback reason. CLI: `vpl ir file.vpl`.

Last run: 230 tests pass in every mode: `--ir`, `--ir --no-nest`, `--ir --no-elide`, `--ir --cc
gcc`, and plain on MSVC and g++ (plain skips `classes/base_arguments`, which is `// mode: ir`). 771
of 782 test functions are lowered. The g++ warning check (every `tests/.out/**/*.cpp` with the
CLAUDE.md flags plus `-Wno-unused-parameter`) is clean for `--ir` and `--ir --no-nest`, and
`classes/construction` and `classes/base_arguments` are clean under ASan.

## Decided while lowering constructors (tell the user if they ask; not yet discussed with them)

- **Each argument of `super(...)` is a statement of its own** (spec 03-memory-model.md, When
  references are released): its temporaries are released before the next argument and before the
  base constructor runs, and the values passed when it returns, the last first. C++ can't run
  statements before a base constructor, so the spec's usual rule (temporaries live until the
  statement completes) can't be met there without heroics.
- **MSVC doesn't evaluate braced mem-initializers left to right** when an argument leaves a
  destructor to run (a temporary `Ref` in it, or a by-value `Ref` parameter), even when each is an
  immediately invoked lambda; plain calls and ints were fine. Hence the delegation above. The old
  emitter has this bug (`super(new A().x, new B().x)` makes B first on MSVC) and releases the
  arguments' temporaries after the base constructor; it isn't fixed, since step 4 deletes it.
  `classes/base_arguments.vpl` runs only with `--ir` (the runner's new `// mode: ir`, documented in
  CLAUDE.md); drop the directive in step 4.

## Decided with the user in earlier sessions

- **A collected cycle's destructors run oldest first** (spec 03-memory-model.md, Destroying a
  cycle), across every cycle one call frees. Before, the order was the collector's discovery order,
  which depended on which releases buffered objects, including the compiler's own copies and pins,
  so it changed between emitters and with `--no-elide`. The user wanted it deterministic and cheap.
  Runtime: a 32-bit creation number in the header's spare half word (`Object::side.own.created`,
  moved into `WeakBlock` with the slot), `Object::age()`, and a sort in
  `CycleCollector::destroy_garbage`. Objects didn't grow. The spec also now says that which cycles
  a *budgeted* call reaches may differ between builds; an unbudgeted call frees exactly the
  unreachable cycles.
- **Pins last until the statement completes**, released with its temporaries (spec
  03-memory-model.md, When references are released, and Pinning). The spec had said "for the
  duration of the call"; the user preferred the statement, which needs no endable holds and keeps
  a call's result nestable. The old emitter still releases a pin when its hoisting lambda ends (the
  spec's "Implemented so far" note covers that), so a test observing a pin's release must store
  the call's result in a local first, as `lambdas/safety.vpl` does, until the old path is deleted.
- **`IsChangedInPlaceByLambda` must be cleaned up**, and the user asked that it really happen.
  `FlowAnalyzer` sets it on every `mut` struct, fixed array, or nullable value a lambda captures,
  even one the lambda only reads, so the IR treats any call after the lambda as writing it. It
  closed a real hole (only whole assignments counted, so `print(arr[i] + run(() -> { arr[0] =
  100; ... }))` read `arr[0]` after the call). It is written into step 7 of `docs/design/ir.md`
  (§7) and on the flag's doc comment: replace it with what each lambda's body writes, from the
  effect table. Raise it when step 7 comes up; don't let it become permanent.

## Next: §7 step 4

Commit each sub-step once the suite passes with `--ir` on MSVC and g++, with `--no-nest`, and with
`--no-elide` (which now passes fully). Most `main`s now lower, so a new behavior test no longer
needs each case in its own function, but check the runner's "N of M lowered" line for the test.

Polish noted (not done):
- A switch over a place whose root needs a hold keeps that hold over the case bodies too, so it
  rarely elides. Testing every case first and then dispatching would end it before the bodies.
- `is` with bindings prints as `bool t{}; if (...) { ...; t = true; } else { t = false; }`
  rather than one condition.
- A `?.` on a nullable object copies the receiver into `value` (+1), where a borrow would do
  (§6.3).
- A switch expression's result prints as `T t{}; if ... t = ...;`, which needs `T` to be
  default-constructible in C++ (every generated type is today).
- When a hold of a nullable place (`x!`) is elided, the borrowed alias keeps the place's nullable
  type (`borrowed Node?`) though it's dereferenced; harmless in C++ (same type), but untidy.

## Interim mechanisms (remove when the analyses are ported, §7 step 5/7)

- `IrDeclare.IsBinding`: pattern bindings count as stored (flow analysis guarantees it).
- `IrLocal.IsUnwrittenElement`: the verifier trusts `ElementAliasing` that nothing writes the
  element (whole-program summaries the effect table lacks), but checks the rest of its dependency.
  Self-tests `unwritten_element_across_call` / `alias_across_call` pin how far the trust goes.

## Known issues to report or decide

- A builder's `store self.f = move %0` of a type parameter's default doesn't nest (the
  `CreatesOwner && !last` rule), so `Pair<A, B>::make_default` declares a temporary per field.
  Correct, just not as tidy as the old output.
- Range loops use a checked add on the counter, which can never overflow (today's code uses
  `++`). This is an optimization to note, not a bug.
- `vpl::keep` on a place now copies (`vpl::keep(T(place))`).
- `IrLocal.Kind` and `Type` are settable so that elision can turn a hold into an alias.
- A constructor delegates (above) whenever two `super(...)` arguments need statements, even when
  their order can't be observed (`super(values[i], values[i + 1])` only for its checks' order).
  Optimization to note: delegate only when the arguments' footprints conflict.
- In a mem-initializer, `vpl::keep` is unneeded (the parameter dies right after the base
  constructor on every ABI), but kept for one rule everywhere.

## Working notes

- The full sweep (six modes) takes about 3.5 minutes, plus a few for the warning check and ASan.
  Tell the user before starting it; from their side a long run looks like being stuck.

- **Neither emitter is an oracle.** When the IR's output differs from the old emitter's, judge
  both against `docs/spec/`. Lowering `main`s this session exposed three IR bugs (a borrow elided
  that the spec keeps, a missing hold for a span of an object's fixed array, `x!` copied) and one
  spec gap (cycle destruction order).
- Run order for a sub-step: `--ir` (MSVC), `--ir --no-nest`, `--ir --no-elide`, `--ir --cc gcc`,
  plain on both compilers, then the g++ warning check, then ASan for anything touching the
  runtime. The modes share `tests/.out`, so run them one at a time. A shell loop over the modes
  piping each run through `grep -A6 "^FAIL\|passed,"` works well.
- The runner matches outputs in file order, so a called function's `// output:` lines go at the
  call in `main`, not in the function.
- Python is available as `python3` in bash, and is handy for multi-line edits. Perl substitutions
  break on C# `$"..."` and on braces; use the Edit tool for those.
- When nesting looks wrong, read the `.ir` next to the `.cpp`: most misses are a footprint that
  is too conservative (see the fixes in `28e7901`).
- The auto-mode safety classifier failed on every shell command for part of this session (no
  verdict); the user switched to accept-edits mode and approves commands by hand.
