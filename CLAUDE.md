# VPL

A personal language for 3D games: Java-style syntax, C++-level control, no undefined behavior by
default. The compiler is written in C# (.NET 10) and compiles VPL to C++20.

## Read first

- `docs/spec/` is the language definition and the source of truth. Read `docs/spec/README.md`
  before designing anything. If you make a design decision the spec doesn't cover, add it to the spec.
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

`Lexer → Parser (AST) → Binder (bound tree, types, overloads) → FlowAnalyzer → Analyses (CyclicClasses) → IrLowering → IrVerifier → Optimize (HoldElision, InPlaceElements, BorrowedLocals, StackSlots, each followed by IrVerifier) → CppEmitter → MSVC/g++`

- `src/Vpl.Compiler/Syntax`: tokens, AST records, parser with error recovery.
- `src/Vpl.Compiler/Semantics`: the bound program and the checks that report errors on it.
  `Types.cs` (interned `VplType`, `TypeMap` substitution, conversions, equality/defaultability),
  `Symbols.cs`, `BoundTree.cs`, `PlacePath.cs` (a place or view as a base and the steps that stay
  in its storage), `FlowAnalyzer.cs`. `Binder/` holds the binder's partial files and
  `MatchSpace.cs` (switch exhaustiveness and unreachable cases).
- `src/Vpl.Compiler/Analysis`: whole-program facts the emitter uses to optimize, found after every
  check has passed; they report no errors. `Analyses.cs` runs the one still on the bound tree
  (`CyclicClasses`, with `WholeProgram.cs`: the lambdas and overrides); the rest run on the IR, where
  `Callees.cs` resolves what each instruction runs (`IrEffects.Runs`) to the lowered functions, each
  one unit of code (a constructor's field initializers and base call are in its own body). `Summaries.cs` finds a summary per unit that depends on its callees' (with a
  worklist, so a summary must only grow as those it reads do).
  `Writes.cs` (the types of storage and the fields each lowered function may write, and the types
  of what it may release, resolved to their destructors' writes by `Writes.Of`). `Escapes.cs` (which
  parameters, `this`, and captures each lowered function may let escape or return).
  `CyclicClasses.cs` (which class instantiations may form cycles, so the cycle collector buffers and
  visits only their objects; `CppEmitter.Cycles.cs` emits what the collector calls; and which
  classes releasing a value may destroy, `Destroyable`, for `Callees` to resolve a release).
  `Overrides.cs` (the methods a dispatched call may run, from the overrides and interface
  implementations the binder records) and `FunctionValues.cs` (which lambdas a function value may be).
- `src/Vpl.Compiler/Optimize`: optimizations, each a rewrite of the lowered IR that no program can
  observe, run in order only without `--no-opt` and verified after each. Lowering itself decides
  nothing optional: the unoptimized IR is the program's meaning, which a future interpreter is to
  run as an oracle. `HoldElision.cs` (§6.2 of the design: turns a hold nothing in its extent can
  disturb into an alias of its place, or drops its borrow, then flattens the statement scopes that
  no longer release anything observable). `InPlaceElements.cs` (borrows loop elements in place, skipping their reference
  count, where nothing can store over the element; the verifier re-checks it with `Writes`).
  `BorrowedLocals.cs` (borrows the place a named class-typed local is copied from, where nothing
  to the end of its block may change the place; `Aliasing.cs` is shared with `HoldElision`). Tests
  mark their loops with `// in-place: <n>`. `StackSlots.cs` (puts objects and closures that can't
  outlive their scope in a `vpl::StackSlot`; docs/design/stack-objects.md). Tests mark their lines
  with `// stack: <n>`, and a test observing an optimization through diagnostics is
  `// mode: optimized`.
- `src/Vpl.Compiler/Stdlib/*.vpl`: the standard library, VPL source compiled with every program
  (`Result`, `Array`, `List`, `Span`, `Memory`, ...), declared before the user's code. The compiler
  knows stdlib types only through `StdlibSymbols` (found by name in stdlib sources) and narrow
  hooks; prefer writing library code in VPL over adding compiler special cases. `extern` functions
  (stdlib only) are implemented in `runtime/vpl/intrinsics.h`.
- `src/Vpl.Compiler/Ir`: the mid-level IR every function body is lowered to (`docs/design/ir.md`).
  `Ir.cs` and `Ir.Values.cs` (the model), `IrPrinter.cs`, `IrEffects.cs` (what each instruction
  may write, run, or panic on; every analysis asks it), `IrAccess.cs` (which storage an instruction
  touches, and the walkers over instructions), `Lowering*.cs` (bound tree to IR: conservative
  holds and copies), and `IrVerifier*.cs` (scoping,
  single assignment, moves, places, and that every reference, view, and borrowed local is used
  only while what it points into is held). `tests/Vpl.Tests/IrSelfTests.cs` builds IR the verifier
  must accept or reject.
- `src/Vpl.Compiler/CodeGen/CppEmitter*.cs`: C++ declarations (types, signatures, templates) from
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

- **All safety checking happens in the binder and flow analysis.** The emitter only translates and
  must never produce C++ with undefined behavior. A C++ compile failure is a compiler bug.
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
  and a new way to hold a place or a view beyond one operation must reject such places.
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
