# Wylow

A personal language for 3D games: Java-style syntax, C++-level control, no undefined behavior by
default. The compiler is written in C# (.NET 10) and compiles Wylow to C++20.

## Read first

- `docs/spec/` is the language definition and the source of truth. Read `docs/spec/README.md`
  before designing anything. If you make a design decision the spec doesn't cover, add it to the spec.
- `docs/design/architecture.md` is the compiler's layer contract: which layer answers which
  question, and how each rule is enforced. Check every change against it; a question that seems
  to need a second answer in another layer means the work is in the wrong layer.
- `docs/design/testing.md` is the testing strategy: what each oracle and harness catches, what
  none of them can see, how findings and survivors are triaged, and the testing backlog.
- `docs/handoff/` holds task prompts for upcoming milestones.

## Commands

```
dotnet build
dotnet tests/Wyl.Tests/bin/Debug/net10.0/Wyl.Tests.dll              # all tests, MSVC
dotnet tests/Wyl.Tests/bin/Debug/net10.0/Wyl.Tests.dll --cc gcc     # all tests, g++
dotnet tests/Wyl.Tests/bin/Debug/net10.0/Wyl.Tests.dll --cc asan    # all tests, clang-cl with ASan and UBSan
dotnet tests/Wyl.Tests/bin/Debug/net10.0/Wyl.Tests.dll --all-modes  # CI's seven modes at once, one process each (CI's job; only when asked)
dotnet tests/Wyl.Tests/bin/Debug/net10.0/Wyl.Tests.dll --interpret  # only the IR interpreter: the meaning against its expectations, and the optimized IR against it
dotnet tests/Wyl.Tests/bin/Debug/net10.0/Wyl.Tests.dll structs      # tests whose path contains "structs"
dotnet tests/Wyl.Tests/bin/Debug/net10.0/Wyl.Tests.dll matrix       # only the interaction matrices
dotnet tests/Wyl.Tests/bin/Debug/net10.0/Wyl.Tests.dll matrix --update-snapshots  # record what they do now
dotnet tests/Wyl.Fuzz/bin/Debug/net10.0/Wyl.Fuzz.dll --seconds 120  # fuzz: mutate the tests and check them in-process (tier 1)
dotnet tests/Wyl.Fuzz/bin/Debug/net10.0/Wyl.Fuzz.dll --tier 2 --seconds 900  # also build and run every mode
dotnet tests/Wyl.Fuzz/bin/Debug/net10.0/Wyl.Fuzz.dll --replay 42    # rebuild one mutant of the same seed
dotnet tests/Wyl.Generate/bin/Debug/net10.0/Wyl.Generate.dll --seconds 60  # generate and check in-process (tier 1)
dotnet tests/Wyl.Generate/bin/Debug/net10.0/Wyl.Generate.dll --tier 2 --seconds 600  # also build and run every mode
dotnet tests/Wyl.Generate/bin/Debug/net10.0/Wyl.Generate.dll --replay 12  # rebuild one generated program
dotnet tests/Wyl.Generate/bin/Debug/net10.0/Wyl.Generate.dll --self-test  # the generator's own checks
dotnet tests/Wyl.Mutation/bin/Debug/net10.0/Wyl.Mutation.dll --list-targets   # mutation testing: the files and their tests
dotnet tests/Wyl.Mutation/bin/Debug/net10.0/Wyl.Mutation.dll --target ir-effects [--filter <text>] [--max N] [--seconds S]
dotnet src/Wyl.Cli/bin/Debug/net10.0/wyl.dll run|build|check|emit|ir|interpret <file.wyl> [--release] [--cc gcc|asan] [--no-nest] [--no-opt]
bash tools/coverage.sh [runner args]   # coverage merged over all six modes: by layer and file, the lines no
                                       # test ran, and unreached error/unreachable sites, in tests/.coverage/
dotnet run --project tools/Dashboard   # local browser UI for builds, tests, fuzzing, generated programs, mutation, findings, and code size
```

The fuzzer (`tests/Wyl.Fuzz`) mutates `tests/cases/**/*.wyl` at feature boundaries and value edges
and checks every mutant with the oracles that need no expected output: no exception, the same
errors with and without optimization, deterministic compilation, warning-free C++, the same output,
exit code, and panic in every mode, and quiet sanitizers. Tier 1 also interprets the optimized IR
and the meaning and compares them, so a pass that changes the answer fails without any C++ build. A
failure is reduced by deleting statements and declarations and written to `tests/fuzz/findings/`
with the seed and mutant that reproduce it; a finding is a bug until it becomes a test in
`tests/cases/` (with `// output:`, `// error:`, or `// panic:`) or a removed line. Tier 1 is
in-process and runs thousands of mutants a minute; tier 2 builds and runs every mode per mutant,
the interpreter first, so it belongs in the background. A finding in `unsafe` or `@Unchecked` code
is the program's undefined behavior, not a compiler bug; a mutant whose mutation edits `unsafe` code
is still compiled, but tier 2 doesn't run it.

The generator (`tests/Wyl.Generate`) makes well-typed programs from a seed instead of mutating
existing ones, and checks each with the same oracles: tier 1 compiles in-process and interprets the
optimized IR against the meaning, tier 2 builds and runs every mode with the interpreter first,
plus a `--release` build (a generated program never overflows, so wrapping can't change its output).
There are no expected outputs, so it reaches combinations no test is near. Stage 1 makes integers,
`bool`, `char`, `float`, `double`, and `String` programs with locals, bounded loops, and helpers,
mixing types where the spec converts them (widenings, adopted constants, casts between kinds);
stages 2–4 add structured values, classes and containers, then generics and lambdas. A generated
program that does not bind, or whose meaning panics or never finishes, is a **generator bug** in
the typed emitter, reported with the binder's diagnostics or the panic and exit code 2, never a
compiler finding; a compiler that throws on one is a compiler finding. A finding is written to
`tests/generate/findings/` with the seed, program, tier, and size, and is a bug until it becomes a
test in `tests/cases/`, triaged like a fuzzer finding. `--self-test`, and every run before it
generates, checks determinism, the integer range and conversion facts, constructor and type
coverage, that a bound corpus contains each mixed-type construct as the compiler reads it, and
budgets, so a degraded generator says so instead of finding nothing; CI runs `--self-test` on every push. The
generator's runs are not part of the suite; they run nightly (`generate.yml`).

The mutation harness (`tests/Wyl.Mutation`) asks the question the oracles can't: would the tests
notice? It changes the compiler's own source — one operator, constant, pattern, negated condition,
or simple statement at a time — rebuilds the file's project into a private copy, and reruns the
tests that cover that feature (`tests/mutation/targets.json`). Neither the working tree nor the
build output is touched; the only artifact is each mutant's assembly in a private copy of the
runner. A mutant no test catches is a **survivor**, which becomes a test or a deleted line, like a
fuzzer finding. Results land in `tests/mutation/results/` and the dashboard's Mutation page.

Every function body is lowered to the mid-level IR, verified, and printed from it; the runner
writes `<name>.ir` next to each test's C++. `--no-nest` and `--no-opt` (in the CLI and the test
runner) must pass too: declining to nest a temporary or to run an optimization is always correct,
and `--no-opt` skips the `// stack:` and `// in-place:` markers, which count optimizations. (With
`--no-opt`, a budgeted `Memory.collectCycles` may reach other cycles, since a pin's release
buffers its object for the cycle collector.)

To show a change altered no behavior (a refactoring step): build the commit before it in a git
worktree, run the tests in both trees, and `diff -r` the two `tests/.out` (C++ and `.ir`).

Generated C++ for tests lands in `tests/.out/` (`--all-modes` writes each mode under
`tests/.out/<mode>/`, so the modes can run at once without overwriting each other). A program is
not rebuilt when the C++ and everything the build reads (flags, runtime headers, the C++ compiler)
match the `.key` beside its executable, so a repeat run only compiles what the change touched.
Check it compiles warning-free:
`g++ -std=c++20 -Wall -Wextra -Wshadow -Wno-sign-compare -Wno-overflow -fsyntax-only -Iruntime <file>`
(unused-parameter warnings from test programs are fine). Integer conversion warnings are left out:
g++ draws them after constant folding on casts the emitter wrote explicitly, and since C++20 every
integer conversion is defined, so a narrowing matters only if it changes a value, which the
comparison with the interpreter finds.

`--cc asan` builds with clang-cl (found on PATH), AddressSanitizer, and UBSan,
and any report fails the test; the runner also checks that both sanitizers still report a
deliberate bug (`sanitizers/live`). It finds what the other modes can't: a use after free whose
freed memory still holds the old bytes prints the same output. UBSan reports and continues, so a
report is found in standard error, not the exit code. Every sanitized program prints a harmless
`interception_win: unhandled instruction` line: Clang 19's ASan can't hook one function of this
newer MSVC runtime. To reproduce a failure, `wyl run <file> --cc asan`.

## Architecture

`Lexer → Parser (AST) → Binder (bound tree, types, overloads) → IrLowering → Pipeline (Borrows → IrVerifier → ReadsInPlace, HoldElision, InPlaceElements, BorrowedLocals, LastUseMoves, StackSlots, each followed by IrVerifier) → CppEmitter → MSVC/g++`

This is the pipeline as it is; the target, and the order of work to reach it, is
`docs/design/architecture.md`. Each layer is a project, `src/Wyl.Compiler.<Layer>`, referencing
only the layers below it (Syntax ← Semantics ← Ir ← Lowering, Analysis ← Optimize ← CodeGen, and Lowering ← Optimize for the mandatory passes; Interpret references Ir and Analysis), so
reaching across is a compile error; `src/Wyl.Compiler` runs them in order (`Compilation.cs`). The
Syntax project also holds `Text` (sources, locations) and `Diagnostics`.

- `Syntax`: tokens, AST records, parser with error recovery.
- `Semantics`: the bound program and the checks that report errors on it.
  `Types.cs` (`WylType`, what every type can be asked, and `TypeMap` substitution), `TypeKinds.cs`
  (a sealed subclass per kind of type, each interning its own), `Conversions.cs`, the type facts by
  topic (`TypeStorage.cs`, `TypeDefaults.cs`, `TypeEquality.cs`, `TypeFormatting.cs`, extension
  members of `WylType`), `Symbols.cs`, `BoundTree.cs`, `PlacePath.cs` (a place or view as a base and the steps that stay
  in its storage). `Binder/` holds the binder's partial files and
  `MatchSpace.cs` (switch exhaustiveness and unreachable cases).
- `Analysis`: whole-program facts the passes and the emitter use, found after every check has
  passed, and the IR verifier (`IrVerifier*.cs`), which is the flow analysis: definite assignment,
  narrowing, returns, constructor fields, scoping, single assignment, moves, places, and that every
  reference, view, and borrowed local is used only while what it points into is held. On the
  lowered program its violations of the source's rules are the user's errors (`Violation`), and
  after a pass, compiler bugs; `tests/Wyl.Tests/IrSelfTests.cs` builds IR it must accept or reject.
  Every whole-program fact is found from the IR and
  throws if asked after the program's `Generation` has changed (`Callees.Fresh`).
  `Callees.cs` resolves what each instruction runs (`IrEffects.Runs`) to the lowered functions, each
  one unit of code (a constructor's field initializers and base call are in its own body). `Summaries.cs` finds a summary per unit that depends on its callees' (with a
  worklist, so a summary must only grow as those it reads do).
  `LambdaWrites.cs` (what each lambda may change around it, found with `Writes`: the verifier asks
  the lambdas made so far, and the passes and the emitter whether any lambda may). `Writes.cs` (the storage each lowered function may write, as `IrAccess` without its locals,
  and the types of what it may release, resolved to their destructors' writes by `Writes.Of`). `Escapes.cs` (which
  parameters, `this`, and captures each lowered function may let escape or return).
  `CyclicClasses.cs` (which class instantiations and closures may form cycles, so the cycle
  collector buffers and visits only their objects, found by `Callees`; `CppEmitter.Cycles.cs` emits what the collector calls; and which
  classes releasing a value may destroy, `Destroyable`, for `Callees` to resolve a release).
  `Overrides.cs` (the methods a dispatched call may run, from the overrides and interface
  implementations the binder records) and `FunctionValues.cs` (which lambdas a function value may be).
- `Optimize`: optimizations, each a rewrite of the lowered IR that no program can observe.
  `Pipeline.cs` is the only code that runs them (they are internal to the project): it runs
  lowering's mandatory passes and verifies the program they leave, then runs each optimization,
  unless `--no-opt`, with facts found fresh for it, bumps the generation, and verifies again. Lowering itself decides
  nothing optional: the unoptimized IR is the program's meaning, which `Interpret` runs as an
  oracle. `ReadsInPlace.cs` (reads a place instead of the temporary lowering copied it
  into at its turn, or kept for a by-value argument, where nothing may change the place while the
  copy matters, and passes a temporary itself where its kept copy's release runs no code). `HoldElision.cs` (§6.2 of the design: turns a hold that doesn't borrow and that nothing in its
  extent can disturb into an alias of its place, or the local itself if that is the place, then
  flattens the statement scopes that no longer release anything observable). `InPlaceElements.cs` (borrows loop elements in place, skipping their reference
  count or a struct's copy, where nothing can write an element of their type or a part of one; the
  verifier re-checks it with `Writes`). `BorrowedLocals.cs` (borrows the place a named local is
  copied from, where nothing to the end of its block may change the place; `Aliasing.cs` is shared with `HoldElision`). `LastUseMoves.cs` (moves a named local into its last
  use where releasing its type can't be observed). Tests
  mark their loops with `// in-place: <n>`. `StackSlots.cs` (puts objects and closures that can't
  outlive their scope in a `wyl::StackSlot`; docs/design/stack-objects.md). Tests mark their lines
  with `// stack: <n>`, and a test observing an optimization through diagnostics is
  `// mode: optimized`.
- `Semantics/Stdlib/*.wyl`: the standard library, Wylow source compiled with every program
  (`Result`, `Array`, `List`, `Span`, `Memory`, ...), declared before the user's code. The compiler
  knows stdlib types only through `StdlibSymbols` (found by name in stdlib sources) and narrow
  hooks; prefer writing library code in Wylow over adding compiler special cases. `extern` functions
  (stdlib only) are implemented in `runtime/wyl/intrinsics.h`.
- `Ir`: the mid-level IR every function body is lowered to (`docs/design/ir.md`).
  `Ir.cs` and `Ir.Values.cs` (the model), `IrPrinter.cs`, `IrEffects.cs` (what each instruction
  may write, run, or panic on; every analysis asks it), and `IrAccess.cs` (which storage an
  instruction touches, with its type, which is the one storage model, and the walkers over
  instructions), and `IrExtent.cs` (what may happen
  while a hold or anything pointing into it is in use, which `Borrows` and `HoldElision` share).
- `Lowering`: `Lowering*.cs`, bound tree to IR. It references only Semantics and the IR model,
  and asks nothing about what may change: it always holds, borrows what may be `Borrowable`,
  copies every operand at its turn, keeps every by-value argument, and gives every statement a
  block. It also holds the mandatory passes (`IrLowering.Passes`, each a `MandatoryPass`), which
  decide what the spec says and lowering can't alone: `Borrows.cs` drops a borrow where no code
  runs while anything refers into it (spec 03-memory-model.md, Borrow counters on containers).
- `CodeGen/CppEmitter*.cs`: C++ declarations (types, signatures, templates) from
  the bound program, and function bodies from the IR. `CppEmitter.Ir.cs` prints a lowered body,
  and `CppEmitter.IrNesting.cs` decides which temporaries it prints inside their one use (§5.1 of
  the design).
- `Interpret/Interpreter*.cs`: the IR interpreter, a second implementation of the instruction
  set beside the emitter (`docs/design/ir.md` §9.11). It executes the IR `Compilation.Lower`
  produces, either form, and is provenance-blind: one visitor rule per statement and rvalue kind,
  and one `Values` definition of copy, release, default, and equality driven by `WylType`.
  `RuntimeText`/`RuntimeArithmetic` reproduce `runtime/wyl/*.h`, and the intrinsic registry keys on
  `BuiltinCppName`. It covers the whole instruction set: values, locals, calls, control flow,
  panics, text, objects and ownership, function values with frame type maps, raw memory (pointers
  over a `Values.Buffer`, fixed-array `data`, spans, the standard library's containers), `Weak`
  blocks, stack slots, and the cycle collector (`Interpreter.Cycles.cs` mirrors
  `runtime/wyl/cycles.h`, with its stats and debug diagnostics). The `interpret` test mode runs the
  program's meaning, checks it against the test's expectations, and interprets the optimized IR to
  compare it with the meaning; it is part of `--all-modes`, and runs the matrix cells too.
- `Parser`, `Binder`, and `CppEmitter` are partial classes split by topic (`Binder.Calls.cs`,
  `CppEmitter.Types.cs`, ...); put new code in the file for its topic, and split a file that
  grows past ~600 lines.
- `runtime/wyl.h`: C++ support included by every generated program, which includes one file per
  topic from `runtime/wyl/` (`arithmetic.h`, `pools.h`, `object.h`, `cycles.h`, `functions.h`, ...).
  Each file includes what it depends on and compiles on its own.
- `tests/cases/**/*.wyl`: each test is a program with `// output:`, `// error:` (on the erroring
  line), `// panic:` (or `// panic-here:`, which also checks the panic is reported at that line),
  and `// mode: release` comments. Error tests must list every error exactly. Outputs are matched
  in file order, so put a called function's outputs at the call.
- `tests/Wyl.Tests/TypeSelfTests.cs`: properties every type must have (interning, substitution,
  `CppIdentity`, conversions, names that bind back to the type, facts that agree), checked on
  random types from a fixed seed; the runner reports them as `types/<property>`. A new kind of
  type, or a new fact about types, gets a case in its generator or a property here.
- `tests/matrix/*.snapshot`: the interaction matrices, generated by `MatrixPlaces.cs` (a place
  crossed with an operation), `MatrixTypes.cs` (a kind of type or value crossed with a context),
  and `MatrixValues.cs` (a value at an edge crossed with an operation) in `MatrixTests.cs`, one
  small program per cell, each run through every oracle like a test case: the interpret mode runs
  every cell interpreted against the same snapshot. A snapshot is reviewed
  against `docs/spec/` cell by cell once; after that any change to a cell fails the suite until
  `--update-snapshots` records it, and the diff is reviewed in that change. A cell the spec refuses
  belongs in the snapshot too: its error, or its panic, is what it does.
- `tests/cases/values/*.wyl`: one file per builtin kind, printing thirty or more computed results
  at the edges. This is the one test kind that catches a wrong answer every mode agrees on, so
  every operator, conversion, and format specifier of a kind belongs in its file.

## Rules

- **All safety checking happens before the emitter**: names and types in the binder, everything
  about order and flow in the flow analysis on the IR (`IrVerifier`), which runs only on a program
  that binds, so an error test keeps binder errors and flow errors in separate files. Lowering
  reaches a source local only through `IrLowering.Accessed`, which emits the `check.narrowed` the
  analysis proves where the source relies on narrowing. The emitter only
  translates and must never produce C++ with undefined behavior. A C++ compile failure is a
  compiler bug.
- **Evaluation order is left to right.** Lowering makes it explicit: each operand is computed by
  its own instructions in order, and the emitter nests one back into its use only where
  `IrEffects`/`IrAccess` show nothing in between can tell. A new instruction must be described
  there correctly.
- **Whatever a reference or span points into must stay alive and unmoved while it is used.**
  Lowering holds the object a place goes through (`IrLowering.ObjectLocal`: a pin, or a borrow
  of a `Borrowable` that hands out storage), and the verifier checks every use is covered. Every
  check and analysis of places decomposes them with `PlacePath.Of`, so a new way to produce a
  place or a view is taught there.
- **Nothing may keep referring into a nullable value** (`int?`, `Vec3?`, `T?`), since assigning the
  variable null destroys it: `PlacePath.NullableValueRoot` finds the narrowed `mut` local a place lies in,
  and a new way to hold a place or a view beyond one operation must reject such places. Within one
  operation, the IR verifier reports a place in the value that the operation (or an earlier operand) may
  set to null as the user's error (`IrVerifier.ReportNullableValue`), so it runs with the `DiagnosticBag`
  after lowering.
- **Each question about the program has one answer, in one place.** Whether copying a type counts
  references, whether a place may change, whether a function value may escape: if two passes ask
  it, they call the same definition, on `WylType`, `IrLocal`, `IrEffects`, or an analysis, not a
  private helper each. A second copy drifts, and in a compiler the drift is a miscompile or a
  missed check that nothing reports. Before writing a predicate, search for one that answers the
  same question; if its answer is wrong for you, fix it there, for every caller. If a layer needs
  an answer that only a later layer has, the work is in the wrong layer (architecture.md): move
  it, don't guess.
- **Every rule is enforced by structure, or labeled as enforced only by review.** A rule about the
  compiler holds only if breaking it breaks the build or the suite: a project boundary, an
  `internal` member, a required constructor argument, a visitor, a type, a pipeline check, or a
  test. A comment, a convention, or a memory doesn't count, since everyone misses things in a
  compiler. A change that adds a rule adds its mechanism in the same change and lists both in
  architecture.md's enforcement table; a rule that only review can enforce is listed as such there.
- **Warnings are errors.** The build fails on any C# warning (`TreatWarningsAsErrors` in
  `Directory.Build.props`), so a leftover parameter or unused local fails instead of piling up. Fix
  the cause; don't suppress a warning without a comment saying why it is wrong there.
- **What the spec says a program does is decided by lowering or a mandatory pass, never by an
  optimization.** Mandatory passes run under `--no-opt` too, and the IR after them is the
  program's meaning, which `--no-opt` emits as directly as it can. An optimization may only change
  what no program can observe: if turning it off changes output (`--no-opt`), it was really a
  language rule. A mandatory pass uses only what the language defines (`IrEffects`, facts on
  `WylType`), never a whole-program analysis, or making an analysis more precise would change
  what programs do. Every observable language rule has a test that prints what it decides, which
  the suite runs in every mode.
- **Facts come from the pipeline.** Passes and the emitter use the facts the pipeline hands them,
  never ones they build: the fact constructors (`Callees`, `Writes`, `Escapes`, `LambdaWrites`) are
  `internal` to Analysis, visible only to Optimize, which holds the pipeline. Facts describe the IR
  as the pass got it, so a pass doesn't ask about what it has already rewritten; the verifier after
  each pass catches a rewrite the facts didn't justify.
- **Docs match the code in the same change.** A change that makes a statement in CLAUDE.md,
  `docs/design/`, or the spec untrue fixes it in that commit. A doc that describes the code wrongly
  misleads the next session as much as a wrong comment.
- **Replace, don't layer.** When a new mechanism takes over from an old one (the IR from the bound
  tree, an analysis from a local rule), move every caller and delete the old one in the same
  change. Don't adapt old code to the new model or keep it alongside "for now"; a half-migrated
  concept is two ways of doing the same thing.
- Every feature gets tests for its behavior and for each error it can produce. Before committing,
  run two modes and nothing more (see "When tests run"); CI runs all seven on the push.
- **A feature adds its row or column to the matrices** (enforced only by review): a new place,
  operation, kind of type or value, context, or value edge goes in `MatrixPlaces.cs`,
  `MatrixTypes.cs`, or `MatrixValues.cs`, so the boundary is tested against every peer. A change
  that alters any cell is reviewed as a snapshot diff, which is where a cell that does something
  the spec doesn't say is found. A feature likewise gets a mutation or a value edge in
  `tests/Wyl.Fuzz/Mutations.cs` when one can cross it, and every fuzzer or generator finding is
  triaged: the reduced program moves into `tests/cases/` with its expected output or errors, and
  the finding file is deleted.
- **Comments are concise and explain why, never what.** Don't restate the code, narrate steps, or
  write comments likely to go stale. Prefer clear names.
- Match the existing code style. C# collection expressions, records, and pattern matching are used throughout.
- Commit each coherent change on `dev` once it passes the pre-commit check (a step of a plan, not
  only whole milestones), ending the message with the `Co-Authored-By` line. Every finding and
  every surviving mutant any run leaves is triaged. Don't push: the user pushes `dev`, and when
  its CI run is green, `main` is fast-forwarded to it (`git push origin dev:main`), so `main`
  only ever points at commits that passed every mode. A CI failure is read with
  `gh run view --log-failed` and fixed on `dev`.

## When tests run

| When | What |
|---|---|
| While editing | one mode with a filter (`Wyl.Tests.dll --interpret structs`): seconds |
| Before each commit | the whole suite in `--interpret` (about 20 s, no C++ build) and MSVC filtered to the area the change touches (`Wyl.Tests.dll structs`). Docs-only changes run nothing; dashboard-only changes only build. Nothing else: no `--all-modes`, other modes, fuzzing, mutation, or coverage unless the user asks |
| On every push to `dev` or `main` (CI) | each of the seven modes of `--all-modes` on its own runner (ASan with `--no-nest` among them, where a use after free can hide that MSVC's `--no-nest` run misses), and the generator's `--self-test` (`.github/workflows/ci.yml`) |
| Nightly (CI) | tier 1 and tier 2 fuzzing and generated-program checking, 30 minutes each, a new seed per run (`fuzz.yml`, `generate.yml`) |
| Weekly, or started by hand (CI) | every mutation target on its own runner, or one target (`mutation.yml`) |
| Whenever the user starts it | local fuzzing, generated programs, mutation targets, coverage |

The fuzzer, the generator, and mutation testing are not part of committing; their findings are
fixed when a run reports them. A CI run uploads its findings or mutation results as artifacts: a
finding moves into `tests/cases/` like a local one, and a results JSON copied into
`tests/mutation/results/` shows on the dashboard. Hand over the command for any local run longer
than a couple of minutes rather than waiting on it.

## Commit messages

The subject says which part of the compiler changed and what it does now, so `git log --oneline`
reads as a changelog: `area: what changed`. The `commit-msg` hook in `.githooks/` rejects a
subject without a listed area (enable it once per clone: `git config core.hooksPath .githooks`).

Areas: `lexer`, `parser`, `binder`, `types`, `verifier`, `lowering`, `analysis`, `opt`,
`emitter`, `interp`, `runtime`, `stdlib`, `cli`, `spec`, `docs`, `tests`, `fuzz`, `generate`,
`mutation`, `dashboard`, `build`. Two areas are written `lowering, emitter: ...`.

- Name the feature, construct, or component, never a plan step or a handoff: not "step 3" or
  "what the survivors found", but `interp: test choice release, enum default, pointer equality`.
- A fix names the symptom and what triggers it: `emitter: fix use after free nesting a string
  temporary under a span`.
- The body says why, then what, and names the test that pins the behavior.

## Pitfalls already hit

- C# `?:` unifies branch types before boxing: `cond ? 1 : 1.0` is a `double`. Cast each branch to
  `object` when building literal values. `BoundLiteral` rejects mismatched values to catch this.
- Visual Studio updates temporarily remove the .NET SDK and MSVC. If `dotnet` reports no SDKs, check
  for a running VS installer and wait for it rather than changing anything.
- Python is available as `python3` in bash, which is handy for multi-line edits that perl's
  quoting breaks. It writes CRLF on Windows; that's harmless, since `.gitattributes` normalizes to LF.
- `record with { ... }` copies computed base-constructor arguments instead of recomputing them:
  `access with { AssumesNonNull = true }` keeps the old `Type`. Construct a new bound node instead.
- Tests compile against the copy of `runtime/` in the build output, so run `dotnet build`
  after changing the runtime.
- The test filter matches paths with the OS separator: on Windows, `enums/values` matches nothing;
  use `values` or `enums\values`.
- In a perl substitution, `$1` followed by a digit (`$110.`) is read as a later group and silently
  drops the text; write `${1}`.
- In perl replacement text, escape the `$` of C# interpolated strings as `\$"`: `$"` is perl's list
  separator (a space), and `$"{what}` a hash element, so both silently drop text.
