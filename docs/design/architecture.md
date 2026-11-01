# Compiler architecture: layers and who answers what

The compiler kept growing two answers to one question: one layer did work that belongs to
another, so the other layer computed the same thing again from what it had, or *guessed*, when
what it needed only existed later. The answers then drifted, and each drift was found a day or two
later as a crash, a miscompile, or a missed check. The migration below fixed the ones an inventory
of the compiler found (2026-10-25).

This document is the contract that prevents that. Each layer owns a set of questions. A question
is answered in exactly one place, and every rule here says **how it is enforced**. A rule that is
enforced only by people remembering it is a rule that will be broken, by any of us.

## The rule

**A layer uses only what exists at that layer. If it needs something only a later layer knows,
the work belongs to that later layer.** Derived facts are computed by the analysis that owns
them, from the current IR. They are never flags that one step sets and another must remember to
correct.

## Layers

| Layer | Input → output | Owns | Never |
|---|---|---|---|
| **Syntax** | text → AST | tokens, grammar, syntax errors | meaning |
| **Semantics** (binder) | AST → bound program | names, types, overloads, conversions; **facts about types** (`WylType` and, by topic, `TypeStorage`, `TypeDefaults`, `TypeEquality`, `TypeFormatting`: counted, escaping, defaultable, has default code, may be Borrowable, written by the runtime); errors about names, types, and local structure (e.g. "cannot assign to a field of a temporary") | anything about order or flow: assigned, narrowed, moved, changed, alive |
| **IR** (model) | none; it is data | the instruction set, what each instruction does (`IrEffects`, `IrAccess`), printing | analyses, decisions |
| **Lowering** | bound program → IR | translation | asking questions: it **always** holds, **always** copies at an operand's turn, **always** keeps an argument's copy for its statement, and gives every statement's temporaries a block |
| **Analysis** | IR → facts | one flow analysis (definite assignment, narrowing, moves, what each reference needs held, what lambdas change), plus the whole-program facts (callees, writes, escapes, cycles); **the user's flow errors**; **the verifier** | changing the IR |
| **Optimize** | IR + facts → IR | rewrites that the facts justify, one decision each | facts of its own: a pass asks the analysis |
| **CodeGen** | IR + facts → C++ | translation, and nesting, which asks the same effects model | decisions about meaning |
| **Interpret** | IR + facts + options → what the program did | executing the instruction set by the IR model's rules, and the runtime's arithmetic, text, ownership, and cycle collector | deciding meaning, building facts, checking |

The **pipeline** is: lower, run the mandatory passes, then analyze (reporting the user's errors),
then for each optimization: run it, analyze again, and verify. The facts a pass is given always describe the IR it is given.
The interpreter is a second consumer of the IR beside CodeGen, not a step of the pipeline: it runs
the IR after the mandatory passes (the program's meaning) or the optimized IR, and compares nothing
itself.

There are two kinds of pass:

- **Mandatory passes** decide what the spec says a program does where lowering can't on its own,
  such as which borrows a statement makes. They run under `--no-opt` too. The IR after them is the
  program's meaning, which the interpreter runs as a second executor to compare the C++ against. A mandatory pass uses
  only what the language defines (`IrEffects`, type facts), never a whole-program analysis,
  or making an analysis more precise would change what programs do.
- **Optimizations** change only what no program can observe, and `--no-opt` turns them off. If
  turning one off changes a program's output, it was really a language rule, and it belongs in a
  mandatory pass.

### The verifier is the flow analysis, checking

User errors and IR invariants are one check run at different times. On freshly lowered IR, a
violation is the user's error, reported at the statement's source location. After any pass, a
violation is a compiler bug. There is no second storage model and no second dataflow: the verifier,
the passes, and nesting all compare storage with `IrAccess`, which the verifier makes precise by
resolving references through what it knows they refer to.

## Enforcement

| Rule | Mechanism |
|---|---|
| Layers depend only downward (Syntax ← Semantics ← IR ← Lowering, Analysis ← Optimize ← CodeGen, and Lowering ← Optimize for the mandatory passes) | **One project per layer** with project references in that direction only, so reaching across is a compile error (`src/Wyl.Compiler.<Layer>`). |
| The binder answers nothing about flow | Semantics can't reference the IR project, and FlowAnalyzer is deleted: the flow errors are the verifier's (`IrVerifier.Violation`), on the lowered program. Where the source relies on narrowing, lowering says so with `check.narrowed`, which every access of a source local goes through (`IrLowering.Accessed`). |
| Lowering asks nothing | Lowering's project references Semantics and the IR model only, with no analysis types, and asks nothing about what may change: it holds every object a place goes through, copies every operand at its turn (`IrLowering.Read`), keeps every by-value argument (`Kept`), borrows every object that may be `Borrowable` where a place goes through what it hands out, and gives every statement a block. The mandatory passes decide what the language says lowering can't, and the optimizations undo what nothing needs. |
| Facts can't be stale | The pipeline (`Optimize/Pipeline.cs`) is the only code that runs passes, which are internal to its project. A pass gets its facts from the pipeline, and they are computed for the IR's current `Generation`; the pipeline bumps the generation after each pass, and a stale facts object throws when asked (`Callees.Fresh`). Facts live in the analysis's result, **not on IR nodes**: what lambdas change is `LambdaWrites`, found with `Writes`, and the IR model only names the question (`IIrLambdaChanges`). A loop element borrowed in place says so in the instruction that borrows it (`IrRead.Element`), not on its local. |
| What the source says can't be forgotten | Every statement has a **required** `Origin` (`IrStatement.Origin`, null only for what the source doesn't write), and so do `IrNew` and `IrClosure`, so building one without it doesn't compile. A loop element's declaration is on its symbol (`LocalSymbol.LoopElement`). These replaced side tables that some code paths filled and others didn't, which lost a user error once. |
| What a local is can't be left out | `IrLocalRole` (variable, temporary, parameter, `this`, capture) is a required constructor argument, and never changes. |
| A local's kind follows from its value | `IrLet.Defines` decides a `let`'s local kind and reference kind from its rvalue. `IrLet`'s constructors either make the temporary by it or throw for a local that disagrees, and the verifier checks every `let` against it after each pass, which may change both. |
| Every instruction is described | Already: visitors over every statement and rvalue kind. The flow analysis's transfer function is one too, so a new instruction can't compile without a rule. |
| Types compare by reference | Each kind of type is a sealed subclass of `WylType` (`TypeKinds.cs`) whose constructor is private, so only its own factory, which interns, can make one. |
| A walk over a type's structure covers every kind | `WylType.Parts` and `MapParts` are abstract, so a new kind doesn't compile until it says what it is built from; substitution (`TypeMap.Apply`), `ContainedTypeParameters`, and `CppIdentity` are written on them, not per kind. A walk that must treat kinds differently pattern-matches on them, which **only review** checks for a missing kind. |
| Each type fact has one definition | In Semantics, used everywhere: what a type is on `WylType` and its kinds, facts about a declared type on `TypeSymbol`, and facts derived from them as extension members by topic (`TypeStorage`, `TypeDefaults`, `TypeEquality`, `TypeFormatting`), which see only their public API. This is **enforced only by review**: C# can't forbid a private predicate that recomputes the same thing. Searching for an existing answer stays a CLAUDE.md rule. |
| The function an instruction runs has one answer | `IrProgram.Functions` is an `IrFunctions` index of every lowered function by symbol, with a memberwise constructor's forms and a struct's `make_default` (which no call names) by type. `Callees`, `CodeGen`, and the IR text all ask it; adding a function rebuilds the index, so it can't describe an earlier program. |
| The oracle and the build share one front end | `Compilation.Lower` produces the bound program, the IR, and the pipeline's facts; `Compilation.Compile` is `Lower` plus `CodeGen`, and `Compilation.Interpret` is `Lower` plus `Interpreter.Run`. The interpreter's input is `Lower`'s IR, either form, so it cannot grow a second lowering. |
| The executor cannot tell the two forms apart | `Interpreter.Run(IrProgram, Callees, InterpreterOptions)` takes no optimized/unoptimized parameter, and each statement and rvalue kind has one visitor rule, so a pass changes which fields an instruction holds, never whether the executor handles it. A form that needed two rules would be a missing definition in `Ir` or the spec. |
| The interpreter's facts are the pipeline's | `Interpreter.Run` takes the `Callees` the pipeline produced; the fact constructors are internal to Analysis (below), so the interpreter project cannot build one. |
| Every instruction and rvalue is interpreted | The interpreter's statement and rvalue executors are `IIrStatementVisitor`/`IIrRvalueVisitor` implementations, so a new kind does not compile until it has a rule, as `IrEffects` already requires. |
| The interpreter and the C++ runtime agree on text and arithmetic | `RuntimeText` and `RuntimeArithmetic` reproduce `runtime/wyl/*.h`, and `InterpreterSelfTests` pins a case per panic message and value edge; the interpret mode compares whole programs against their expected output. Every builtin and stdlib extern has a handler: `Interpreter.Intrinsics` is one table that is both the dispatch and `Interpreter.Handles`, and the `interpreter/registry` self-test walks them all and fails on a missing key. That each handler prints what the runtime prints is pinned by `RuntimeText`/`RuntimeArithmetic`'s self-tests and the whole-program comparison. |
| One comparison rule for the interpreter and the modes | `RunComparison.Compare` (`tests/Wyl.Harness`) compares stdout, the panic and its location, the exit code, and the warnings where asked; the runner's optimized-vs-meaning check, the fuzzer's tier-1 pass check and its tier-2 interpreter mode, and the generator's two tiers all call it, and `FuzzSelfTests` pins every case of the rule, so the harnesses cannot disagree about what a disagreement is. |
| The interpreter runs in the suite like a mode | The `interpret` mode is one of `--all-modes`' seven; `MatrixTests` interprets every cell against the same snapshot the native modes record, and `tools/coverage.sh` merges it into the per-layer report. That the mode must keep passing is **enforced by the suite**: every test runs in it, one process of `--all-modes`. |
| The interpreter's own source matters | `tests/mutation/targets.json` names each interpreter source file, with the test filters that reach it and `--interpret`, so a mutation to a rule only the interpreter implements is caught by interpreting the suite, not by a native mode that never runs that code. That each target is run and each survivor triaged is **enforced only by review**. |
| Runtime text is written once | `Location.PanicText` is the `file:line:column` form the runtime prints, used by the emitter, the IR printer, lambda names, and the interpreter; `FunctionSymbol.AbstractCallMessage` is the abstract-call stub's text, which the interpreter raises too. The runtime-header messages are `RuntimeText` and `RuntimeArithmetic`, which the agreement row above covers. |
| What a visibility reaches has one definition | `Reach` (Semantics) is the visibility and declaring file of a type, function, or field, with a member's already within its type's, and `Reach.Includes` is the only answer to whether a file may name it: the binder's `CheckVisible`, the overloads a call may choose (`VisibleFrom`, in `Resolve`), hiding and overriding, and the signature rule all ask it. A `FunctionSymbol` given no `Reach` is visible nowhere, so one the binder forgets fails every test that names it instead of being visible everywhere. That every lookup of a name asks it is **enforced only by review**; `errors/visibility.wyl` and `classes/visibility.wyl` pin the rules. |
| Types and their facts are consistent | `TypeSelfTests` checks properties on random types (interning, substitution, `CppIdentity` and the facts decided per C++ type, conversions, names that bind back, facts that imply each other) in every run of the suite. That its generator makes every kind of type, and that a new fact gets a property, is **enforced only by review**. |
| Each feature interacts with its peers | `MatrixTests` generates one small program per row and column of the interaction matrices (`MatrixPlaces.cs`, `MatrixTypes.cs`, `MatrixValues.cs`), runs each through every oracle, and compares its result to `tests/matrix/*.snapshot`; any change fails until the snapshot is updated on purpose, and the diff is reviewed against the spec. That a new feature adds its row or column is **enforced only by review**. |
| The compiler is wrong where two features meet | `tests/Wyl.Fuzz` parses the tests, mutates them at feature boundaries and value edges, and checks every mutant with the same oracles (it compiles, every mode agrees, the interpreter agrees with the meaning and with the modes, the C++ is warning-free, the sanitizers are quiet); a failure is reduced into `tests/fuzz/findings/` with its seed. A mutant whose edit is in `unsafe` code is compiled but not run, a `// mode: optimized` test is not compared under `--no-opt`, warnings about the program's own constant comparisons and constant xors are not the emitter's, and integer conversion warnings are not checked (a narrowing that changes a value is a disagreement with the interpreter). Where a native build is compared, stdout that differs only in the sign of a printed zero is equal (`RunComparison.WithoutZeroSign`): g++ and MSVC don't always keep it from correct C++. A program that draws a warning is still run and compared, and a difference in behavior is reported before the warning. That every finding becomes a test in `tests/cases/` and every feature gets a mutation are **enforced only by review**. |
| A generated program is well-typed by construction | `tests/Wyl.Generate` asks for an expression of one `WylType` and takes only constructors that produce that type, with `Ground(type)` as the total fallback, so it emits no text the binder can reject; `IntegerFacts` guards every integer operation against overflow so the program cannot panic, and a meaning that panics or exceeds its budget in tier 1 is reported as a generator bug (`Checker.Meaning`), so a wrong range fact can't pass as a program that stops early, and tier 2 can add a `--release` build that must agree with the interpreter exactly (`Mode.Release`, which only the generator asks `Modes.TryAvailable` for, since a fuzzer mutant may overflow); `GeneratorSelfTests` forces every constructor of every stage-1 type, checks the spec's conversion table (`ConversionFacts`, written from §2.3, not asked of the compiler) against `Conversions.IsImplicit`, asks the parser whether the corpus reaches constructs a quoting slip could silently lose (an interpolated string's holes), binds the corpus and finds each mixed-type construct in the bound tree (an implicit conversion of an operand, argument, initializer, or returned value; an unsuffixed literal bound to a type other than `int`; a cast between integers, floats, and `char`), since a spelling slip turns one into a same-type operation that still binds, and checks the text for the markers the oracles scan (`unsafe`, `@Unchecked`, `// mode: optimized`, `Memory.collectCycles`, `cycleStats`, `panic(`). That a non-binding program is fixed as a generator bug and every finding becomes a test in `tests/cases/` are **enforced only by review**. |
| The harnesses share one oracle | `tests/Wyl.Harness` holds the checker, the comparison, the reducer, the syntax index it deletes from, the mode list, the repository-root finder, and the finding writer; the test runner, the fuzzer, and the generator reference it, so a changed rule or a new oracle reaches every tool at once. That a new tool references it rather than copying one is **enforced only by review**. |
| The compiler is right, not only consistent | `tests/Wyl.Mutation` changes the compiler's own source (an operator, constant, pattern, condition, or statement), rebuilds the file's project in a private copy, and reruns the covering tests named in `tests/mutation/targets.json`; a survivor is recorded in `tests/mutation/results/`. The other harnesses only prove the compiler is consistent, which a wrong answer every mode agrees on defeats. That every survivor is triaged to a test or a deleted line is **enforced only by review**. |
| No leftovers | Every C# warning fails the build (`TreatWarningsAsErrors` in `Directory.Build.props`). |
| The C++ the emitter and runtime make has no undefined behavior | **Enforced by the suite's sanitizer mode** (`--cc asan`: AddressSanitizer and UBSan, where any report fails the test), as far as the tests reach and the sanitizers see. The runner checks that both still report a deliberate bug, so a mode that silently stops working fails. |
| Nesting keeps the values the IR makes, not only their order | `CppEmitter.IrNesting` refuses to print a temporary into a use that needs the object itself (`TemporaryUse.TakesPlace`: a `ref`/`addr` argument, a hold's borrowed place, any borrow read, or a call's function value, which must stay alive while it runs). The nested text is the expression that made the value — a copy's place, a move's xvalue, or a construction's value — not the object in C++ storage, so such a use would name the wrong object, fail to bind, or lose the copy that keeps a closure alive. It also treats a value whose release may run code, including an initialized local that nesting would turn into a temporary, as `CreatesOwner`, which nests only into its statement's last C++ statement. Every test runs in every mode, so a nested place that changes a value disagrees with `--no-nest` and with the sanitizers. |
| Only the pipeline builds facts | The fact constructors and finders (`Callees`, `Writes.Find`, `Escapes.Find`, `LambdaWrites.Find`, `CyclicClasses.Find`, `Overrides`) are `internal` to Analysis, visible only to Optimize and the tests (`InternalsVisibleTo`), so CodeGen can't build facts of its own. |
| A pass doesn't ask about what it has rewritten | **Enforced only by the verifier after the pass**, which re-analyzes with fresh facts and rejects a rewrite they don't justify. Within a pass, facts describe the IR as the pass got it. |
| Language rules aren't optimizations | Mandatory passes are a type of their own (`MandatoryPass`), which only the Lowering project can make (`IrLowering.Passes`), so they can't reach a whole-program analysis; optimizations are `internal` to Optimize. The pipeline runs the mandatory passes always, verifies the program they leave (the user's errors), and runs the optimizations only without `--no-opt`. Each observable rule has a test that prints what it decides, run in every mode (`classes/borrowable_observed.wyl` for borrows). |
| Storage is described one way | `IrAccess`, with the type of the storage, describes every read and write, and the whole-program summaries are `IrAccess` without the locals (`IrAccess.Summarized`), so there is no second model to disagree with it. Strict aliasing (spec 01-safety.md §1.2) decides which accesses of different types may overlap (`TypeStorage.MayContain`). |
| Docs match the code | **Enforced only by review** (a CLAUDE.md rule): a change fixes what it makes untrue in CLAUDE.md, `docs/design/`, and the spec in the same commit. |

Anything only review can enforce is listed as such, so it's known to be weaker.

## What naive lowering cost (step 5, 2026-10-26)

With lowering naive and the passes below, the optimized C++ of every test has as many
`wyl::pin`, `wyl::borrow`, and `wyl::StackSlot` as before, and fewer `wyl::keep` (162, from 167):

- **Holds** are HoldElision's. An alias of a whole local is renamed to the local.
- **Copies at an operand's turn, and kept copies,** are `ReadsInPlace`'s: a temporary copy of a
  place is replaced by the place where nothing may change it while the copy matters, which for a
  kept copy that may own objects is until its block ends, and a kept copy of a temporary whose
  release runs no code passes the temporary itself. It runs first, so that HoldElision's
  `Flatten` then finds the statement blocks that release nothing observable.
- **More kept copies are right now:** `l_back = pass(l_back)` keeps the argument, since the store
  replaces it within the statement; `IsStable` had made its release depend on the C++ ABI.
- **One was lost:** a store through a reference, which `IrAccess` took as unknown storage, kept
  a copy of a local of an unrelated type (`panics/list_ref_borrowed.wyl`), until accesses carried
  the type of their storage.

What only worked because of the shortcuts' shapes: the runtime's `wyl::argument` returned its
first argument's type (base constructor calls with arguments of different types), and the
verifier rooted a copy of a view in the local it was copied from, which a statement's block then
ended too early.

## Migration

Each step deletes the old home of what it moves, and passes all four modes before the next.

1. **Projects and the pipeline** (done). Remove the three crossings, split into projects, and give
   the pipeline its generation check. Behavior is unchanged: the generated C++ and IR of every
   test were identical before and after.
2. **Type facts on `WylType`** (done): inventory findings 2, 4 (predicate half), and 8. This fixed
   the `Weak`-in-enum compile failure, and LastUseMoves now moves strings. InPlaceElements and
   BorrowedLocals stayed with class types, since their rule didn't see stores into a struct's
   fields, until there was one storage model.
3. **Required origins and the local-kind rule** (done): the two failures naive lowering exposed.
   `IrProgram.Origins`, `Creations`, and `LoopElements` are gone, and so is the verifier's copy of
   the kind rule. The generated C++ and IR of every test were identical before and after.
4. **One flow analysis** (done). The verifier's dataflow is the analysis: its
   violations on the lowered program are the user's errors, so only a program that binds is
   lowered, and error tests keep binder and flow errors in separate files. FlowAnalyzer is deleted.
   Lambda changes are `LambdaWrites`, a fact found per generation (findings 1 and 10). The analysis
   narrows through `?:` as the spec says, which FlowAnalyzer didn't. There is one storage model,
   `IrAccess`, with paths (finding 5): the verifier passes it what its references are known to
   refer to, and asks `Writes.CodeMayWrite` with the lambdas made so far. What `*x` reaches is
   `IrLocal.Deref`. Unknown storage now may be a frame local for the verifier too, and a write
   above the last nullable a place lies in destroys it (IrSelfTests
   `ref_argument_replaces_inner_nullable_in_use`). The generated C++ and IR were identical.
5. **Naive lowering** (done): every lowering shortcut is deleted (finding 3, `capturedByLambda`,
   and `MayBeBorrowable`'s copy, now `BoundProgram.MayBeBorrowable`: finding 6), and
   `ReadsInPlace` owns the copies. StackSlots needed no change. A by-value argument is now
   released at its statement's end even where the call replaces its place
   (`classes/kept_arguments.wyl`).
6. **`CyclicClasses` onto the IR** (done): it finds closures and their captures from the lowered
   program, and `Callees` finds it and `Overrides` per generation, so `WholeProgram` and `Analyses`
   are gone and the emitter gets its facts from the pipeline's last `Callees`. Erasing reference
   nullability has one definition, now `WylType.CppIdentity` (finding 9). The generated
   C++ and IR were identical but for the order of instantiation traits.
7. **Local roles and the remaining walkers** (done). `IrLocalRole` replaces the local's booleans,
   and an in-place loop element is `borrow.element` (`IrRead.Element`), not a flag the verifier
   reads off the local. One definition each of the locals a statement declares (`IrWalk.Declared`),
   whether it gives a local another value (`IrWalk.Rebinds`), what a call is given
   (`IrWalk.Passed`), and what a statement does itself (`IrEffects.Itself`). Whether an operation
   panics is decided by its IR node, whose constructor requires a location exactly then; lowering,
   the verifier, and the emitter had each decided it. LastUseMoves now moves loop elements that
   aren't borrowed in place. The generated C++ was otherwise identical.

A review after the migration (2026-10-26) found gaps in how the rules were enforced, a language
rule decided by an optimization, and a second storage model; all three are fixed, and the rows
above say how each is enforced now.
