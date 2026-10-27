# VPL

A personal language for 3D games: Java-style syntax, C++-level control, no undefined behavior by
default. The compiler is written in C# (.NET 10) and compiles VPL to C++20.

## Read first

- `docs/spec/` is the language definition and the source of truth. Read `docs/spec/README.md`
  before designing anything. If you make a design decision the spec doesn't cover, add it to the spec.
- `docs/design/architecture.md` is the compiler's layer contract: which layer answers which
  question, and how each rule is enforced. Check every change against it; a question that seems
  to need a second answer in another layer means the work is in the wrong layer.
- `docs/handoff/` holds task prompts for upcoming milestones.

## Commands

```
dotnet build
dotnet tests/Vpl.Tests/bin/Debug/net10.0/Vpl.Tests.dll              # all tests, MSVC
dotnet tests/Vpl.Tests/bin/Debug/net10.0/Vpl.Tests.dll --cc gcc     # all tests, g++
dotnet tests/Vpl.Tests/bin/Debug/net10.0/Vpl.Tests.dll structs      # tests whose path contains "structs"
dotnet src/Vpl.Cli/bin/Debug/net10.0/vpl.dll run|build|check|emit|ir <file.vpl> [--release] [--cc gcc] [--no-nest] [--no-opt]
```

Every function body is lowered to the mid-level IR, verified, and printed from it; the runner
writes `<name>.ir` next to each test's C++. `--no-nest` and `--no-opt` (in the CLI and the test
runner) must pass too: declining to nest a temporary or to run an optimization is always correct,
and `--no-opt` skips the `// stack:` and `// in-place:` markers, which count optimizations. (With
`--no-opt`, a budgeted `Memory.collectCycles` may reach other cycles, since a pin's release
buffers its object for the cycle collector.)

To show a change altered no behavior (a refactoring step): build the commit before it in a git
worktree, run the tests in both trees, and `diff -r` the two `tests/.out` (C++ and `.ir`).

Generated C++ for tests lands in `tests/.out/`. Check it compiles warning-free:
`g++ -std=c++20 -Wall -Wextra -Wconversion -Wsign-conversion -Wshadow -fsyntax-only -Iruntime <file>`
(unused-parameter warnings from test programs are fine).

Run runtime changes (reference counting, `Weak`, the cycle collector) under AddressSanitizer: g++ here
(MSYS2) can't link ASan, but clang-cl in `D:\LLVM\bin` can. From Git Bash, after the tests have
generated `tests/.out`:

```
out=$(cygpath -w "$TEMP/asan"); mkdir -p "$TEMP/asan"; name=basics
MSYS_NO_PATHCONV=1 clang-cl -std:c++20 -EHsc -fsanitize=address -D_ALLOW_COMPILER_AND_STL_VERSION_MISMATCH \
  -DVPL_DEBUG=1 -Iruntime "tests/.out/cycles/$name/$name.cpp" "-Fe$out/$name.exe" "-Fo$out/$name.obj"
"$TEMP/asan/$name.exe"
```

- `_ALLOW_COMPILER_AND_STL_VERSION_MISMATCH`: the installed Clang 19 is older than this MSVC STL expects.
- `MSYS_NO_PATHCONV=1` and dash-style flags (`-Fe`, not `/Fe`): Git Bash rewrites `/`-flags as paths.
- Keep `-Fe$out/$name.exe` as written: `\\$name` escapes the `$`, so every build writes one literal file.
- Check the program actually ran (its output appears) before calling it clean, and that ASan is live
  at all: a deliberate use-after-free must report `heap-use-after-free`.

## Architecture

`Lexer → Parser (AST) → Binder (bound tree, types, overloads) → IrLowering → Pipeline (Borrows → IrVerifier → ReadsInPlace, HoldElision, InPlaceElements, BorrowedLocals, LastUseMoves, StackSlots, each followed by IrVerifier) → CppEmitter → MSVC/g++`

This is the pipeline as it is; the target, and the order of work to reach it, is
`docs/design/architecture.md`. Each layer is a project, `src/Vpl.Compiler.<Layer>`, referencing
only the layers below it (Syntax ← Semantics ← Ir ← Lowering, Analysis ← Optimize ← CodeGen, and Lowering ← Optimize for the mandatory passes), so
reaching across is a compile error; `src/Vpl.Compiler` runs them in order (`Compilation.cs`). The
Syntax project also holds `Text` (sources, locations) and `Diagnostics`.

- `Syntax`: tokens, AST records, parser with error recovery.
- `Semantics`: the bound program and the checks that report errors on it.
  `Types.cs` (`VplType`, what every type can be asked, and `TypeMap` substitution), `TypeKinds.cs`
  (a sealed subclass per kind of type, each interning its own), `Conversions.cs`, the type facts by
  topic (`TypeStorage.cs`, `TypeDefaults.cs`, `TypeEquality.cs`, extension members of `VplType`),
  `Symbols.cs`, `BoundTree.cs`, `PlacePath.cs` (a place or view as a base and the steps that stay
  in its storage). `Binder/` holds the binder's partial files and
  `MatchSpace.cs` (switch exhaustiveness and unreachable cases).
- `Analysis`: whole-program facts the passes and the emitter use, found after every check has
  passed, and the IR verifier (`IrVerifier*.cs`), which is the flow analysis: definite assignment,
  narrowing, returns, constructor fields, scoping, single assignment, moves, places, and that every
  reference, view, and borrowed local is used only while what it points into is held. On the
  lowered program its violations of the source's rules are the user's errors (`Violation`), and
  after a pass, compiler bugs; `tests/Vpl.Tests/IrSelfTests.cs` builds IR it must accept or reject.
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
  nothing optional: the unoptimized IR is the program's meaning, which a future interpreter is to
  run as an oracle. `ReadsInPlace.cs` (reads a place instead of the temporary lowering copied it
  into at its turn, or kept for a by-value argument, where nothing may change the place while the
  copy matters, and passes a temporary itself where its kept copy's release runs no code). `HoldElision.cs` (§6.2 of the design: turns a hold that doesn't borrow and that nothing in its
  extent can disturb into an alias of its place, or the local itself if that is the place, then
  flattens the statement scopes that no longer release anything observable). `InPlaceElements.cs` (borrows loop elements in place, skipping their reference
  count or a struct's copy, where nothing can write an element of their type or a part of one; the
  verifier re-checks it with `Writes`). `BorrowedLocals.cs` (borrows the place a named local is
  copied from, where nothing to the end of its block may change the place; `Aliasing.cs` is shared with `HoldElision`). `LastUseMoves.cs` (moves a named local into its last
  use where releasing its type can't be observed). Tests
  mark their loops with `// in-place: <n>`. `StackSlots.cs` (puts objects and closures that can't
  outlive their scope in a `vpl::StackSlot`; docs/design/stack-objects.md). Tests mark their lines
  with `// stack: <n>`, and a test observing an optimization through diagnostics is
  `// mode: optimized`.
- `Semantics/Stdlib/*.vpl`: the standard library, VPL source compiled with every program
  (`Result`, `Array`, `List`, `Span`, `Memory`, ...), declared before the user's code. The compiler
  knows stdlib types only through `StdlibSymbols` (found by name in stdlib sources) and narrow
  hooks; prefer writing library code in VPL over adding compiler special cases. `extern` functions
  (stdlib only) are implemented in `runtime/vpl/intrinsics.h`.
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
- `Parser`, `Binder`, and `CppEmitter` are partial classes split by topic (`Binder.Calls.cs`,
  `CppEmitter.Types.cs`, ...); put new code in the file for its topic, and split a file that
  grows past ~600 lines.
- `runtime/vpl.h`: C++ support included by every generated program, which includes one file per
  topic from `runtime/vpl/` (`arithmetic.h`, `pools.h`, `object.h`, `cycles.h`, `functions.h`, ...).
  Each file includes what it depends on and compiles on its own.
- `tests/cases/**/*.vpl`: each test is a program with `// output:`, `// error:` (on the erroring
  line), `// panic:` (or `// panic-here:`, which also checks the panic is reported at that line),
  and `// mode: release` comments. Error tests must list every error exactly. Outputs are matched
  in file order, so put a called function's outputs at the call.

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
  there correctly. A new kind of call also belongs in `BoundTree.CallOf`, which the checks and
  analyses use to see calls.
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
  it, they call the same definition, on `VplType`, `IrLocal`, `IrEffects`, or an analysis, not a
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
  `VplType`), never a whole-program analysis, or making an analysis more precise would change
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
- Every feature gets tests for its behavior and for each error it can produce. Run the suite on
  both MSVC and g++ before committing.
- **Comments are concise and explain why, never what.** Don't restate the code, narrate steps, or
  write comments likely to go stale. Prefer clear names.
- Match the existing code style. C# collection expressions, records, and pattern matching are used throughout.
- Commit each coherent change once it passes the suite on both compilers (a step of a plan, not
  only whole milestones), ending the message with the `Co-Authored-By` line. Don't push.

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
