# Wylow

A personal, general-purpose language: C++-level control and no undefined behavior by default. The
compiler is C# (.NET 10) and compiles Wylow to C++20.

## Read first

- `docs/spec/`: the language definition and the source of truth; start at its `README.md`. It
  states rules only: no rationale, implementation, costs, or status (decision 0001). A design
  decision puts its rules in the spec and its reasons in a decision record
  (`docs/design/decisions/`, see its README).
- `docs/design/architecture.md`: the compiler's layers, where the code is, and how each rule is
  enforced. Check every change against it: a question that seems to need a second answer in
  another layer means the work is in the wrong layer.
- `docs/design/testing.md`: the oracles and harnesses, how to run them, triage, and the backlog.
- `docs/diagnostics/README.md`: how error messages are written; follow it in every new message.
- `docs/design/working.md`: how we work: design principles, design sessions, handoffs, long runs.
  For design, spec, docs, and review work, load the `rigor` skill.
- `docs/handoff/`: task prompts for upcoming work. Project knowledge goes in committed files, not
  assistant memory, so it reaches every machine.

## Everyday commands

```
dotnet build
dotnet tests/Wyl.Tests/bin/Debug/net10.0/Wyl.Tests.dll --interpret         # whole suite, interpreter only (~20 s)
dotnet tests/Wyl.Tests/bin/Debug/net10.0/Wyl.Tests.dll structs             # MSVC, tests whose path contains "structs"
dotnet tests/Wyl.Tests/bin/Debug/net10.0/Wyl.Tests.dll matrix --update-snapshots
dotnet src/Wyl.Cli/bin/Debug/net10.0/wyl.dll run|build|check|emit|ir|interpret <file.wyl | dir> [--release] [--cc gcc|asan] [--no-nest] [--no-opt]
```

Other modes, the fuzzer, the generator, mutation testing, coverage, and the dashboard:
`docs/design/testing.md`.

## Writing tests

`tests/cases/**/*.wyl`: each test is a program with `// output:`, `// error:` (on the erroring
line; list every error exactly), `// panic:` or `// panic-here:` (also checks the line), and
`// mode: release` comments. Outputs match in file order, so put a called function's output at the
call. Binder errors and flow errors go in separate files (flow analysis runs only on a program that
binds). A test imports the `std` names it uses (`import std::{List, print};`). A directory with a
`package.wylcfg` (`tests/cases/packages/`) is one test of all its files.

## Rules

- **All safety checking happens before the emitter**: names and types in the binder, order and
  flow in the IR verifier. The emitter only translates and never produces C++ with undefined
  behavior; a C++ compile failure is a compiler bug.
- **Evaluation order is left to right**, made explicit by lowering; the emitter nests an operand
  back only where `IrEffects`/`IrAccess` show nothing in between can tell, so a new instruction must
  be described there correctly.
- **What a reference or span points into stays alive and unmoved while it is used**, and **nothing
  keeps referring into a nullable value** (`PlacePath.NullableValueRoot`). A new way to produce or
  hold a place is taught to `PlacePath.Of` and checked by the verifier.
- **Each question about the program has one answer, in one place.** Before writing a predicate,
  search for one that answers the same question; if it's wrong for you, fix it there. If a layer
  needs an answer only a later layer has, the work is in the wrong layer.
- **Every rule is enforced by structure** (a project boundary, `internal`, a required argument, a
  visitor, a type, a pipeline check, a test), or listed in architecture.md's enforcement table as
  enforced only by review. A change that adds a rule adds its mechanism and its table row.
- **Language rules are decided by lowering or a mandatory pass, never by an optimization.** If
  `--no-opt` changes output, it was a language rule. Mandatory passes use only what the language
  defines, never a whole-program analysis. Every observable rule has a test that prints it.
- **Facts come from the pipeline**; passes and the emitter never build their own.
- **Replace, don't layer.** A new mechanism moves every caller and deletes the old one in the same
  change.
- **Warnings are errors** (`TreatWarningsAsErrors`). Fix the cause.
- **Docs match the code in the same change**: CLAUDE.md, `docs/design/`, and the spec.
- **Docs state checked facts.** The spec never compares Wylow with another language. Elsewhere, a
  comparison names the exact feature and links its primary source (a spec section, official docs,
  a proposal), or is cut: never a bare "as in C++". Old text is source material: rewrite it and
  check its claims; never copy it over verbatim. A rule adopted partway through a session applies
  to everything written earlier in it.
- **Examples are generic**: `Point`, `Node`, `Order`, `Parser`, `Buffer`, never game development.
  Wylow is general-purpose; no design is justified by what a game needs.
- **Every feature gets tests** for its behavior and each error it can produce, its row or column in
  the matrices, and a fuzzer mutation where one can cross it (enforced only by review). Every
  fuzzer, generator, or mutation finding is triaged into a test or a deleted line.
- **Comments are concise and explain why, never what.** Match the existing style (collection
  expressions, records, pattern matching).

## When tests run

| When | What |
|---|---|
| Editing | one mode with a filter: seconds |
| Before each commit | whole suite `--interpret`, and MSVC filtered to the area touched. Docs-only changes run nothing; dashboard-only changes only build. Nothing else unless asked |
| Push to `dev` or `main` (CI) | all seven modes, each on its own runner, and the generator's `--self-test` |
| Nightly / weekly (CI) | fuzzing and generation; mutation testing |

Long runs (fuzzing, generation, mutation, coverage) go through CI or the user; hand over the
command rather than waiting on one.

## Commits

- Commit each coherent change on `dev` once it passes the pre-commit check, ending the message with
  the `Co-Authored-By` line. **Never push**: the user pushes `dev`, and `main` is fast-forwarded to
  it when CI is green.
- Subject: `area: what changed`, enforced by `.githooks/commit-msg` (`git config core.hooksPath
  .githooks`). Areas: `lexer`, `parser`, `binder`, `types`, `verifier`, `lowering`, `analysis`,
  `opt`, `emitter`, `interp`, `runtime`, `stdlib`, `cli`, `spec`, `docs`, `tests`, `fuzz`,
  `generate`, `mutation`, `dashboard`, `build`; two are written `lowering, emitter: ...`.
- Name the feature or component, never a plan step or handoff. A fix names the symptom and its
  trigger. The body says why, then what, and names the test that pins it.

## Pitfalls already hit

- Tests compile against the copy of `runtime/` in the build output: `dotnet build` after changing
  the runtime.
- The test filter matches paths with the OS separator: on Windows use `values` or `enums\values`.
- If `dotnet` reports no SDKs, a Visual Studio update is running; wait for it.
- MSVC 14.50/14.51 `/O2` miscompile `-static_cast<double>(static_cast<int16_t>(~f()))`; the emitter
  emits `wyl::bnot` for that reason. Don't inline the `~` back.
- C# `?:` unifies branch types before boxing (`cond ? 1 : 1.0` is a `double`); cast each branch to
  `object` when building literal values.
- `record with { ... }` keeps computed base-constructor arguments (`access with { AssumesNonNull =
  true }` keeps the old `Type`); construct a new node instead.
- For multi-line edits, `python3` in bash beats perl. In perl replacements, write `${1}` before a
  digit, and escape the `$` of C# interpolated strings as `\$"`.
