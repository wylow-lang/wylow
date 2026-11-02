# Testing strategy

Bugs sit where two features meet (`ref?` into a nullable value, a function type inside a generic
argument), and nobody writes tests for combinations they haven't thought of. So the suite has two
goals: **a number per feature and layer** that says how well it is tested, and **tests the
computer writes**, aimed at boundaries and checked by oracles that need no expected output.

CLAUDE.md has the commands and when each runs; `architecture.md`'s enforcement table says how each
harness's rule is enforced. This file is why they exist, what each can't see, and what to add next.

## Oracles

| Oracle | Bug it catches | Where |
|---|---|---|
| The compiler never throws | crashes anywhere in the pipeline | `Compilation.Compile` |
| The verifier accepts every pass's output | a pass that broke a rule | `Optimize/Pipeline.cs` |
| The C++ compiles, warning-free | emitter bugs | `Driver.BuildExecutable` |
| Every mode prints and exits the same | optimization bugs; C++ that depends on UB or on the compiler | MSVC, g++, `--no-opt`, `--no-nest`, `--release` (generator only) |
| ASan and UBSan stay quiet | use after free, overflow in generated code and the runtime | `--cc asan` |
| The interpreter agrees | emitter and pass bugs against the meaning; a pass bug without a C++ build | `--interpret` |
| Expected output, reviewed snapshots | a wrong answer every mode agrees on | `tests/cases`, `tests/matrix`, `tests/cases/values` |
| Mutation testing | a test that wouldn't notice a wrong answer | `tests/Wyl.Mutation` |

**What no oracle sees:** every mode and the interpreter share lowering, so a wrong meaning
(evaluation order, release order) is agreed on everywhere. Only expected-output tests, the
value-printing files, and the reviewed matrix snapshots check it. The interpreter is a second
executor, not a trusted reference: a disagreement means one of the two is wrong.

The modes may differ in one documented case: with `--no-opt`, a budgeted `memory::collectCycles`
may reach other cycles, so a program calling it is compared on everything but stdout. Stdout that
differs only in a printed zero's sign is equal where a native build is compared
(`RunComparison.WithoutZeroSign`). A run that ended because a thread ran off its stack is compared
with nothing, like a timeout: the spec gives a stack overflow to the OS guard page (spec
01-safety.md §1.2), so the code it aborts with is the platform's, and a sanitizer build reports the
same overflow as an access violation or not at all. Any further exception must be decided in the spec
and named in the comparison rule, never added quietly.

## What each harness is for

- **Matrices** cross a shape with a shape (place × operation, type × context, value edge ×
  operation). Their snapshots were reviewed against the spec cell by cell; a cell the spec doesn't
  decide is a finding as much as a wrong one.
- **Value-printing tests** (`tests/cases/values/`) catch a wrong answer every mode agrees on, dozens
  per file.
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
surface as disagreements. `binder-names`, `binder-imports`, and `manifest` (packages and modules,
2026-11-01) haven't run yet; their first CI run is triaged like the others.

## Known risks

- **Two definitions of an instruction's meaning.** `IrEffects` says what an instruction may write,
  run, and panic on; the interpreter says what it does. `IrSelfTests.Effects` and the differential
  runs keep them honest; a new instruction adds both and a self-test for each.
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
