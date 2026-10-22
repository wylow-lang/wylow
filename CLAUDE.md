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
dotnet src/Vpl.Cli/bin/Debug/net10.0/vpl.dll run|build|check|emit <file.vpl> [--release] [--cc gcc]
```

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

`Lexer → Parser (AST) → Binder (bound tree, types, overloads) → FlowAnalyzer → Analyses (ElementAliasing → StackObjects, CyclicClasses) → CppEmitter → MSVC/g++`

- `src/Vpl.Compiler/Syntax`: tokens, AST records, parser with error recovery.
- `src/Vpl.Compiler/Semantics`: the bound program and the checks that report errors on it.
  `Types.cs` (interned `VplType`, `TypeMap` substitution, conversions, equality/defaultability),
  `Symbols.cs`, `BoundTree.cs`, `FlowAnalyzer.cs`. `Binder/` holds the binder's partial files and
  `MatchSpace.cs` (switch exhaustiveness and unreachable cases).
- `src/Vpl.Compiler/Analysis`: whole-program facts the emitter uses to optimize, found after every
  check has passed; they report no errors. `Analyses.cs` runs them all. `WholeProgram.cs` is what
  they share: a unit of code per function, implicit constructor, and lambda, and what each call may
  run. `Summaries.cs` finds a summary per unit that depends on its callees' (with a worklist, so a
  summary must only grow as those it reads do).
  `ElementAliasing.cs` (which loop elements can skip their reference count, from a whole-program
  summary of what each function writes).
  `CyclicClasses.cs` (which class instantiations may form cycles, so the cycle collector buffers and
  visits only their objects; `CppEmitter.Cycles.cs` emits what the collector calls).
  `StackObjects.cs` (which `new`s can be on the stack, from a whole-program summary of which
  parameters each function lets escape; `vpl::StackSlot` holds them). Tests mark their lines with `// stack: <n>`.
  `Overrides.cs` (the methods a dispatched call may run, from the overrides and interface
  implementations the binder records) and `FunctionValues.cs` (which lambdas a function value may be).
- `src/Vpl.Compiler/Stdlib/*.vpl`: the standard library, VPL source compiled with every program
  (`Result`, `Array`, `List`, `Span`, `Memory`, ...), declared before the user's code. The compiler
  knows stdlib types only through `StdlibSymbols` (found by name in stdlib sources) and narrow
  hooks; prefer writing library code in VPL over adding compiler special cases. `extern` functions
  (stdlib only) are implemented in `runtime/vpl.h`.
- `src/Vpl.Compiler/CodeGen/CppEmitter*.cs`: bound tree to C++.
- `Parser`, `Binder`, and `CppEmitter` are partial classes split by topic (`Binder.Calls.cs`,
  `CppEmitter.Ordering.cs`, ...); put new code in the file for its topic, and split a file that
  grows past ~600 lines.
- `runtime/vpl.h`: C++ support included by every generated program (checked arithmetic, panics, print).
- `tests/cases/**/*.vpl`: each test is a program with `// output:`, `// error:` (on the erroring
  line), `// panic:` (or `// panic-here:`, which also checks the panic is reported at that line),
  and `// mode: release` comments. Error tests must list every error exactly. Outputs are matched
  in file order, so put a called function's outputs at the call.

## Rules

- **All safety checking happens in the binder and flow analysis.** The emitter only translates and
  must never produce C++ with undefined behavior. A C++ compile failure is a compiler bug.
- **Evaluation order is left to right.** C++ doesn't guarantee it, so `CppEmitter.Ordered` (`CppEmitter.Ordering.cs`) hoists
  operands into an immediately-invoked lambda when they conflict. Any new expression kind that can
  read or write state must be described correctly in `HasEffects`, `Writes`, `RunsLambdas`, and `ReadsState`.
- **Places can't come out of `Ordered`'s lambda**, which returns a copy. Anything that may be
  assigned or referred to (`vpl::at`, a method returning `ref`) keeps its order another way; see
  `CppEmitter.Index` and `OrderedReferenceCall`.
- **Whatever a reference or span points into must stay alive and unmoved while it is used.**
  `CppEmitter.StorageRoot` finds the object to hold, and whether to borrow it (`vpl::borrow`) or
  just pin it. A new way to produce a place or a view must be handled there.
- **Nothing may keep referring into a nullable value** (`int?`, `Vec3?`, `T?`), since assigning the
  variable null destroys it: `BoundTree.NullableValueRoot` finds the narrowed local a place lies in,
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
- Python isn't installed; use the editing tools, PowerShell, or bash.
- `record with { ... }` copies computed base-constructor arguments instead of recomputing them:
  `access with { AssumesNonNull = true }` keeps the old `Type`. Construct a new bound node instead.
- Tests compile against the copy of `runtime/vpl.h` in the build output, so run `dotnet build`
  after changing the runtime.
- The test filter matches paths with the OS separator: on Windows, `enums/values` matches nothing;
  use `values` or `enums\values`.
- In a perl substitution, `$1` followed by a digit (`$110.`) is read as a later group and silently
  drops the text; write `${1}`.
- In perl replacement text, escape the `$` of C# interpolated strings as `\$"`: `$"` is perl's list
  separator (a space), and `$"{what}` a hash element, so both silently drop text.
