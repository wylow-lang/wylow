# Testing strategy

Bugs sit where two features meet (`ref?` into a nullable value, a function type inside a generic
argument), and nobody writes tests for combinations they haven't thought of. So the suite has two
goals: **a number per feature and layer** that says how well it is tested, and **tests the
computer writes**, aimed at boundaries and checked by oracles that need no expected output.

CLAUDE.md has the everyday commands and when each kind of run happens; `architecture.md`'s
enforcement table says how each harness's rule is enforced. This file is how to run them, why they
exist, what each can't see, and what to add next.

## Oracles

| Oracle | Bug it catches | Where |
|---|---|---|
| The compiler never throws | crashes anywhere in the pipeline | `Compilation.Compile` |
| The verifier accepts every pass's output | a pass that broke a rule | `Optimize/Pipeline.cs` |
| The C++ compiles, warning-free | emitter bugs | `Driver.BuildExecutable` |
| Every mode prints and exits the same | optimization bugs; C++ that depends on UB or on the compiler | clang-cl, g++, `--no-opt`, `--no-nest`, `--release` (generator only) |
| ASan and UBSan stay quiet | use after free, overflow in generated code and the runtime | `--cc asan` |
| ThreadSanitizer stays quiet | a data race in generated code or the runtime | `--cc tsan`, on tests marked `// tsan` |
| The interpreter agrees | emitter and pass bugs against the meaning; a pass bug without a C++ build | `--interpret` |
| Expected output, reviewed snapshots | a wrong answer every mode agrees on | `tests/cases`, `tests/matrix`, `tests/cases/values` |
| Every diagnostic is cataloged, tested, and explained | an error or warning with no code, spec section, test, or page | `CatalogSelfTests`; a test's `// error[WYLnnnn]: <message>` or `// warning[WYLnnnn]: <message>` marker matches the code and the message exactly, and a warning no marker lists fails the test |
| The spec's examples compile and mark the errors they produce | an example that drifts from the compiler or contradicts a rule | `SpecExampleSelfTests` |
| `std` is documented, the docs' links work, and the grammar is whole | a `pub` declaration with no doc comment, a panic with no `# Panics`, a link to nothing, a grammar name defined twice or never | `DocSelfTests` |
| Mutation testing | a test that wouldn't notice a wrong answer | `tests/Wyl.Mutation` |

The native modes build with clang-cl. MSVC is not one of them: its `/O2` optimizer raises internal
compiler errors (C1001) on correct generated C++, and a compiler that cannot build a valid program
makes every run fail. `--cc msvc` still selects MSVC for a caller who asks for it.

**What no oracle sees:** every mode and the interpreter share lowering, so a wrong meaning
(evaluation order, release order) is agreed on everywhere. Only expected-output tests, the
value-printing files, and the reviewed matrix snapshots check it. The interpreter is a second
executor, not a trusted reference: a disagreement means one of the two is wrong.

The modes may differ in one documented case: with `--no-opt`, a budgeted `memory::collectCycles`
may reach other cycles, so a program calling it is compared on everything but stdout. Stdout that
differs only in a printed zero's sign is equal where a native build is compared
(`RunComparison.WithoutZeroSign`). A run that ended because a thread ran off its stack is compared
with nothing, like a timeout: a stack overflow aborts (spec `introduction.aborts`), through the OS guard page
(runtime.md), so the code it aborts with is the platform's, and a sanitizer build reports the
same overflow as an access violation or not at all. Any further exception must be decided in the spec
and named in the comparison rule, never added quietly.

The pools hold memory that no program can observe, so `runtime/pools` (`tests/runtime/pools.cpp`, in every
native mode) tests them in C++. Ownership follows a `Pools` object and not a thread, so the test drives two
of them from one thread and counts the chunks that stay mapped, which makes its result deterministic. Its
last scenario uses real threads for the TSan mode.

## Running the tools

Commands (`CLAUDE.md` has the everyday ones):

```
dotnet tests/Wyl.Tests/bin/Debug/net10.0/Wyl.Tests.dll --cc gcc     # all tests, g++
dotnet tests/Wyl.Tests/bin/Debug/net10.0/Wyl.Tests.dll --cc asan    # clang-cl with ASan and UBSan
dotnet tests/Wyl.Tests/bin/Debug/net10.0/Wyl.Tests.dll --cc tsan    # ThreadSanitizer, tests marked '// tsan' only
dotnet tests/Wyl.Tests/bin/Debug/net10.0/Wyl.Tests.dll --all-modes  # CI's seven modes, one process each
dotnet tests/Wyl.Fuzz/bin/Debug/net10.0/Wyl.Fuzz.dll --seconds 120  # fuzz, tier 1 (in-process)
dotnet tests/Wyl.Fuzz/bin/Debug/net10.0/Wyl.Fuzz.dll --tier 2 --seconds 900  # also build and run every mode
dotnet tests/Wyl.Fuzz/bin/Debug/net10.0/Wyl.Fuzz.dll --replay 42    # rebuild one mutant of the same seed
dotnet tests/Wyl.Generate/bin/Debug/net10.0/Wyl.Generate.dll --seconds 60  # generate, tier 1
dotnet tests/Wyl.Generate/bin/Debug/net10.0/Wyl.Generate.dll --tier 2 --seconds 600
dotnet tests/Wyl.Generate/bin/Debug/net10.0/Wyl.Generate.dll --replay 12
dotnet tests/Wyl.Generate/bin/Debug/net10.0/Wyl.Generate.dll --self-test  # the generator's own checks
dotnet tests/Wyl.Mutation/bin/Debug/net10.0/Wyl.Mutation.dll --list-targets
dotnet tests/Wyl.Mutation/bin/Debug/net10.0/Wyl.Mutation.dll --target ir-effects [--filter <text>] [--max N] [--seconds S]
bash tools/coverage.sh [runner args]   # coverage merged over all modes, in tests/.coverage/
dotnet run --project tools/Dashboard   # local UI: builds, tests, fuzzing, generation, mutation, findings, code size
```

- **Filters and sections.** A filter runs the test cases whose path contains it. The runner's
  other tests come in sections, and the names of a section's results share a prefix such as `ir/`,
  `catalog/`, `spec/`, or `fuzz/`. The runner runs a section only if the filter is part of its
  prefix, as `catalog` is, or starts with the prefix, as `ir/borrows` does. A filter such as
  `structs` therefore runs only test cases. The matrices are a section that matches the filter
  against each matrix's name itself. The sections run at the same time as the test cases. The
  spec's and the catalog's examples are compiled without the optimizations, since only their
  diagnostics are checked and an optimization never reports one. The test runner, the fuzzer,
  and the generator use the server garbage collector, and every project is optimized in the Debug
  configuration too, because the harnesses compile programs on many threads at once.
- **Modes.** `--no-nest` and `--no-opt` must pass too: declining to nest a temporary or to run an
  optimization is always correct. `--no-opt` skips the `// stack:` and `// in-place:` markers,
  which count optimizations. Generated C++ lands in `tests/.out/` (`--all-modes`: one directory
  per mode); a program isn't rebuilt when its C++ and everything the build reads match the `.key`
  beside its executable. To check C++ by hand: `g++ -std=c++20 -Wall -Wextra -Wshadow
  -Wno-sign-compare -Wno-overflow -fsyntax-only -Iruntime <file>` (unused parameters in test
  programs are fine; integer conversion warnings are left out, since g++ draws them after constant
  folding on explicit casts, and a narrowing that changes a value disagrees with the interpreter).
- **ThreadSanitizer.** `--cc tsan` runs only the tests marked `// tsan`, since TSan is much slower
  and the suite's other tests start no thread. TSan does not work on Windows, so the mode runs on
  `ubuntu-24.04` in CI, where it finds clang++ or g++ on PATH; the toolchain exists only after a
  probe program builds and runs with `-fsanitize=thread`, which is why the mode disappears rather
  than failing when the platform cannot do it. A report fails the test but does not enter the
  output comparison (`sanitizers/live-tsan` checks that the mode still reports a deliberate race).
  Reproduce with `wyl run <file> --cc tsan`.
- **Linux through WSL.** On Windows, WSL 2 with Ubuntu runs the Linux modes locally: `--cc gcc`,
  `--cc tsan`, and `--interpret`. The distribution needs the .NET 10 SDK, g++, and clang, and
  `libsqlite3-dev` for `examples/sqlite`. Build in a clone inside the Linux file system, made with
  `git clone /mnt/<drive>/<path to the repository> ~/wylow`, and bring each new commit across with
  `git pull`. A build in the Windows checkout under `/mnt` would overwrite the Windows build's
  `bin/` and `obj/` directories, and the Windows file system is slow from Linux. An agent runs a
  command there with `wsl -d Ubuntu -- bash -lc "<command>"`. A command that needs `sudo` asks for
  a password, so the user runs installations.
- **Threads.** The interpreter runs each logical thread on an operating-system thread, so two runs
  of one program may interleave differently (decision 0057). A test of threads or channels
  synchronizes before each line it prints, with a `join` or a `receive`, and expects a property of
  the run rather than an order. The fuzzer's corpus leaves out every test marked `// tsan`, which
  is every test that starts a thread, because a mutant's output is then not stable enough to compare.
- **Native code.** A test that calls an extern function of its own calls C++ code, which the
  interpreter does not run (decision 0087). The interpreter refuses such a program before it runs
  anything, and names the first call. The test is marked `// native`, so `--interpret` skips it,
  and the fuzzer compares its mutants only among the native modes (`RunComparison.IsNative`). A
  test that calls C++ code without the marker fails in the interpreted mode. A package test keeps
  the C++ it calls in its own `native/` directory, so the suite needs no library installed. If a
  `// native` test lists errors and binds without any, the runner builds it and compares the
  errors that the C++ build reports, such as a failed signature or layout check
  (`interop/signature_mismatch`, `interop/layout_mismatch`).
- **Sanitizers.** `--cc asan` uses the clang-cl of Visual Studio (runtime.md, "Toolchains"). Any
  report fails the test, and the runner checks that both sanitizers still report a deliberate bug
  (`sanitizers/live`). ASan finds a use after free even if the freed memory still holds the old
  bytes. UBSan reports and continues, so the runner finds a report in stderr, not in the exit code.
  With Clang 19's ASan and the MSVC 14.51 runtime, every sanitized program printed a harmless
  `interception_win: unhandled instruction` line, and the matrices still drop that line. Clang
  22's ASan prints no such line. Reproduce a failure with `wyl run <file> --cc asan`.
- **Refactoring without behavior change**: build the commit before it in a git worktree, run the
  tests in both, and `diff -r` the two `tests/.out` (C++ and `.ir`).
- **The fuzzer** (`tests/Wyl.Fuzz`) mutates `tests/cases/**/*.wyl` and checks every mutant with the
  oracles above. Tier 1 is in-process and also interprets the optimized IR against the meaning;
  tier 2 builds and runs every mode, the interpreter first. A failure is reduced and written to
  `tests/fuzz/findings/` with its seed. Package tests are skipped (`Wyl.Harness/TestCases.cs`). A
  finding in `unsafe` code is the program's undefined behavior; a mutant that edits `unsafe` code
  is compiled but not run in tier 2.
- **The generator** (`tests/Wyl.Generate`) builds well-typed programs from a seed with the same
  oracles, plus a `--release` build in tier 2 (a generated program never overflows). Stage 1 covers
  integers, `bool`, `char`, `float`, `double`, and `String`; stages 2–4 are the backlog. A generated
  program that doesn't bind, panics, or doesn't finish is a **generator bug** (exit code 2), not a
  compiler finding; a compiler that throws on one is a compiler finding. Findings go to
  `tests/generate/findings/`. `--self-test`, which runs before every generation and in CI on every
  push, checks determinism, the range and conversion facts, constructor coverage, that the corpus
  contains each mixed-type construct as the compiler reads it, and budgets.
- **Mutation testing** (`tests/Wyl.Mutation`) changes one operator, constant, pattern, condition,
  or statement in the compiler, rebuilds that project in a private copy, and reruns the tests
  `tests/mutation/targets.json` names for it; neither the working tree nor the build output is
  touched. Results land in `tests/mutation/results/` and on the dashboard.
- **CI** uploads findings and mutation results as artifacts: a finding moves into `tests/cases/`
  like a local one, and a results JSON copied into `tests/mutation/results/` shows on the
  dashboard. Read a failed run with `gh run view --log-failed`.

## What each harness is for

- **Matrices** (`tests/matrix/*.snapshot`, generated by `MatrixPlaces.cs`, `MatrixTypes.cs`, and
  `MatrixValues.cs`) cross a shape with a shape (place × operation, type × context, value edge ×
  operation), one small program per cell, run through every oracle, the interpreter included.
  Their snapshots were reviewed against the spec cell by cell; after that, a changed cell fails
  until `--update-snapshots` records it, and the diff is reviewed in that change. A cell the spec
  refuses is in the snapshot too, with its error or panic. A cell the spec doesn't decide is a
  finding as much as a wrong one.
- **Value-printing tests** (`tests/cases/values/`, one file per builtin kind, thirty or more results
  at the edges) catch a wrong answer every mode agrees on, so every operator, conversion, and
  format specifier of a kind belongs in its file.
- **Type properties** (`tests/Wyl.Tests/TypeSelfTests.cs`): interning, substitution,
  `CppIdentity`, conversions, names that bind back, facts that agree, on random types from a fixed
  seed, reported as `types/<property>`. A new kind of type or type fact gets a generator case or a
  property.
- **The fuzzer** mutates existing tests at feature boundaries and value edges: what crashes and
  what disagrees. A mutation that changes nothing a program does, such as `constructor-chain`,
  can be held to more: `FuzzSelfTests` interprets each of its mutants of the corpus on every
  suite run and requires the original's output, which is how it found its first bugs.
- **The generator** builds well-typed, trap-free programs from types, reaching combinations no test
  is near.
- **Mutation testing** changes the compiler and asks whether the tests notice: what is quietly
  wrong. Its survivors are the only systematic list of answers no test checks.
- **Coverage** (`tools/coverage.sh`, merged over all modes) finds large untested areas and unreached
  error sites. Don't chase 100%; mutation answers whether a test would notice.

## Triage

A fuzzer finding, a generator finding, or a mutation survivor is a bug until it is one of:

- a test in `tests/cases/` (or `IrSelfTests`, `TypeSelfTests`, `InterpreterSelfTests`,
  `FuzzSelfTests`) that pins it;
- a deleted line, if nothing depends on it (a line no behavior depends on is a rule enforced by
  nothing);
- for a survivor, a recorded equivalent in `tests/mutation/targets.json`, with its reason.

Each fix also asks: what structure would have made this impossible?

Mutation notes: a parameter's default value is not a mutant (C# compiles it into every caller);
filters match paths with the OS separator, so a filter that matches nothing is a silent hole.

## Numbers to track

Coverage per layer, diagnostic coverage (error sites a test reaches), matrix cells reviewed,
mutation score per target, and fuzzer and generator findings per hour.

Status when the hardening phase closed (2026-10-31): 98.2% of lines and 94.0% of branches merged
over all modes; every user-error and verifier site reached; `ir-effects`, `ir-access`, and
`conversions` at 100%, and `emitter-types`, `emitter-ir`, and `place-path` triaged. The interpreter
targets haven't been triaged; they are picked up when convenient, since the interpreter's own bugs
surface as disagreements.

Mutation status on 2026-11-01: all 21 targets then in `tests/mutation/targets.json` measured,
85.2% overall, 335 survivors untriaged. This includes `binder-names`, `binder-imports`, and
`manifest` (packages and modules, added the same day). The analyses (`analysis-*`), `verifier`,
`verifier-values`, and `borrows` were added with the analysis self-tests and haven't run yet; their
first CI run is triaged like the others.

Proposed triage order: the interpreter's `arithmetic` and `cycles` (143 survivors, mostly
`RuntimeArithmetic.cs`'s total order, NaN, and conversion guards), then `binder-imports`,
`manifest`, and `binder-names` (46), then `text`, `calls`, `core`, `memory`, and `intrinsics`
(121), then the rest (25). Per-target results are in the latest `mutation.yml` run's artifacts.
The harness also reported 253 sites whose mutant doesn't compile and 24 timeouts; compiling a
mutant before counting it, and a larger budget, would remove that noise.

## Known risks

- **Two definitions of an instruction's meaning.** `IrEffects` says what an instruction may write,
  run, and panic on; the interpreter says what it does. `IrSelfTests.Effects` and the differential
  runs keep them honest; a new instruction adds both and a self-test for each.
  `IrSelfTests.EffectKinds` fails until every instruction kind has an effect case.
- **A fact that under-reports is invisible.** An analysis (`Escapes`, `Writes`, `Callees`,
  `LambdaWrites`, `CyclicClasses`) that misses a route costs nothing until a pass relies on it, so
  `AnalysisSelfTests` asks each fact of a lowered fixture for every way code reaches something.
  A `sound/` case must hold forever; a `precise/` case pins an answer a pass depends on (its
  comment says which) and may change when an analysis gets sharper.
- **Unstated orders.** Aggregate assignment's release order is not in the spec; decide it when a
  test can see it.
- **`memory::allocate`'s "too large" check** needs C++ `sizeof(T)`, which the interpreter only
  models.
- **Diagnostics under optimization.** A program can read costs the spec treats as unobservable
  (`cycleStats().buffered`). Stack objects are the documented exception; anything else is a spec
  decision.

## Backlog

Surveyed from LLVM (lit/FileCheck, `llvm-reduce`, `XFAIL`/`UNSUPPORTED`), GCC (torture runs under
many option sets), rustc (UI diagnostics with `--bless`, `mir-opt` golden MIR), Csmith and YARPGen
(UB-free random programs with differential oracles), EMI (mutating code the input never runs),
Alive2 (translation validation), Miri (a UB-detecting interpreter), OSS-Fuzz (coverage-guided
front-end fuzzing), CompCert, and test262.

Highest value first:

1. **Generator stages 2–4:** structs, enums, nullables, `switch`, fixed arrays; then classes
   (destructor order is an oracle), spans, `List`; then generics, lambdas, `Weak`.
2. **Front-end fuzzing of arbitrary input:** a syntax-chaos mutator (insert, delete, duplicate
   tokens) requiring only no exception and no hang; SharpFuzz if coverage guidance is wanted. Error
   recovery is where the current fuzzer, which starts from valid tests, is blindest.
3. **Per-pass golden IR:** a few reviewed `.ir` snapshots per optimization, so a pass that silently
   stops firing fails.
4. **Metamorphic/EMI transformations as a named oracle:** rename locals, wrap in `if (true)`, insert
   dead declarations, mutate code the input never runs; output must not change.
5. **Runner and CI hygiene:** `XFAIL`/`UNSUPPORTED`; compile-time and C++-size budgets per commit.
6. **Runtime and stdlib model tests:** random operation sequences against a simple model; cycle
   collector, `Weak`, and pool stress; MSan/LSan on a Linux CI; runtime coverage of
   `runtime/wyl/*.h` with clang profiling.
7. **Mutation testing widened** to the binder and lowering, a file at a time.
8. **A spec-rule index:** one id per normative rule, and a test citing each.

Not worth doing: formal verification, SMT translation validation (the verifier after each pass plus
the interpreter is the practical version), and any new oracle that shares lowering.
