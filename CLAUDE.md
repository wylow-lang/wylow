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

## Architecture

`Lexer → Parser (AST) → Binder (bound tree, types, overloads) → FlowAnalyzer → CppEmitter → MSVC/g++`

- `src/Vpl.Compiler/Syntax`: tokens, AST records, parser with error recovery.
- `src/Vpl.Compiler/Semantics`: `Types.cs` (interned `VplType`, `TypeMap` substitution,
  conversions, equality/defaultability), `Symbols.cs`, `BoundTree.cs`, `Binder*.cs`, `FlowAnalyzer.cs`,
  `MatchSpace.cs` (switch exhaustiveness and unreachable cases).
- `src/Vpl.Compiler/Prelude.vpl`: VPL source compiled with every program (`Result`). Its
  declarations come before the user's; the binder recognizes `Result` by its source.
- `src/Vpl.Compiler/CodeGen/CppEmitter*.cs`: bound tree to C++.
- `Parser`, `Binder`, and `CppEmitter` are partial classes split by topic (`Binder.Calls.cs`,
  `CppEmitter.Ordering.cs`, ...); put new code in the file for its topic, and split a file that
  grows past ~600 lines.
- `runtime/vpl.h`: C++ support included by every generated program (checked arithmetic, panics, print).
- `tests/cases/**/*.vpl`: each test is a program with `// output:`, `// error:` (on the erroring
  line), `// panic:`, and `// mode: release` comments. Error tests must list every error exactly.

## Rules

- **All safety checking happens in the binder and flow analysis.** The emitter only translates and
  must never produce C++ with undefined behavior. A C++ compile failure is a compiler bug.
- **Evaluation order is left to right.** C++ doesn't guarantee it, so `CppEmitter.Ordered` (`CppEmitter.Ordering.cs`) hoists
  operands into an immediately-invoked lambda when they conflict. Any new expression kind that can
  read or write state must be described correctly in `HasEffects`, `Writes`, and `ReadsState`.
- Every feature gets tests for its behavior and for each error it can produce. Run the suite on
  both MSVC and g++ before committing.
- **Comments are concise and explain why, never what.** Don't restate the code, narrate steps, or
  write comments likely to go stale. Prefer clear names.
- Match the existing code style. C# collection expressions, records, and pattern matching are used throughout.
- Commit after each milestone, ending the message with the `Co-Authored-By` line. Don't push.

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
