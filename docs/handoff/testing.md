# Testing: measure coverage, then generate tests

**Status:** planned 2026-10-26. Step 1 has landed (`tools/coverage.sh`, baseline below, every
user error and verifier check reached), except the optional runtime coverage, and so has step 2
(`--cc asan`), and step 3 (`TypeSelfTests`); step 4 is next. The unreached `InvalidOperationException` sites are left for the
fuzzer (step 5) to aim at. Delete this file once every step has landed, and
move whatever it leaves as rules into CLAUDE.md and `docs/design/architecture.md`.

## Why

Almost every piece of code we look at turns out to have a bug, and nearly all of them sit where two
features meet: `ref?` into a nullable value, a function type inside a generic argument, `nint` inside
a function type inside a class. The suite tests each feature in its common cases and a few edge
cases someone thought of. Nobody writes tests for combinations they haven't thought of, and the
number of combinations grows much faster than anyone can review. Bugs found by reading code don't
scale.

What we want instead:

1. **A number per feature and per layer**, so "how well tested is this?" has an answer, and we can
   tell when a hardening phase is done.
2. **Tests the computer writes**, aimed at feature boundaries, checked by oracles that need no
   expected output.

New language features wait until this is in place (see the exit criteria at the end).

## The oracles we already have

A generated program has no `// output:` comments, so it is checked against rules that hold for
every program. The compiler already has them; they only need to be run on many programs:

| Oracle | Bug it catches | Where it lives |
|---|---|---|
| The compiler never throws | Crashes anywhere in the pipeline: about 60 `InvalidOperationException` sites that should be unreachable | `Compilation.Compile` |
| The verifier accepts every pass's output | A pass that broke a rule (`Pipeline.Verify` throws) | `Optimize/Pipeline.cs` |
| The generated C++ compiles, warning-free | Emitter bugs (CLAUDE.md: a C++ compile failure is a compiler bug) | `Driver.BuildExecutable` |
| All modes print the same and exit the same | Optimization bugs, and C++ that depends on undefined behavior or on the compiler | MSVC, g++, `--no-opt`, `--no-nest` |
| AddressSanitizer and UBSan stay quiet | Use-after-free, overflow, and the like in generated code and the runtime | the runner's `--cc asan` (clang-cl) |
| Later: the IR interpreter agrees | Emitter and pass bugs, against the program's meaning | not built yet (architecture.md: the unoptimized IR is the oracle) |

The modes may legitimately differ in one case: with `--no-opt`, a budgeted `Memory.collectCycles`
may reach other cycles (CLAUDE.md). A program that calls it is compared on everything but that.

One limit to keep in mind: none of these oracles catch a bug in what lowering *means*, because every
mode and the interpreter share lowering. If lowering gets evaluation order wrong, every mode agrees
on the wrong answer. Expected-output tests and the spec remain the only check for that, which is why
step 4's matrices have reviewed snapshots.

## Step 1: measure coverage

Produce numbers before changing anything, so later steps can show what they bought.

- **C# coverage of the compiler.** Run the test runner under `dotnet-coverage` (Microsoft's
  collector; `dotnet tool install -g dotnet-coverage`) or coverlet, producing Cobertura XML. Turn it
  into an HTML report and a text summary with ReportGenerator. Wrap all of it in one script,
  `tools/coverage.sh`, and add its command to CLAUDE.md. Report:
  - line and branch coverage per layer (Syntax, Semantics, Ir, Lowering, Analysis, Optimize,
    CodeGen), and per file, since files are split by topic (`Binder.Spans.cs`,
    `Lowering.Lambdas.cs`, `CppEmitter.Cycles.cs`), which makes a file a feature in all but name;
  - **diagnostic coverage**: of the binder's ~200 `Error(` sites and the verifier's `Violation`
    sites, which have never produced an error in any test. Each is a user error with no test, which
    CLAUDE.md already requires ("every feature gets tests ... for each error it can produce"). This
    can come straight from the line coverage of those calls; if that proves too noisy, give
    `Binder.Error` and `Violation` `[CallerFilePath]` and `[CallerLineNumber]` parameters and have
    the runner record which sites fired.
  - **unreachable-site coverage**: every `throw new InvalidOperationException` that a test reaches is
    a bug, and the list of the ones no test reaches is the fuzzer's target list.
- **Runtime coverage (optional).** Build the tests' C++ with clang-cl and
  `-fprofile-instr-generate -fcoverage-mapping`, then merge with `llvm-profdata` and report with
  `llvm-cov` (both in `D:\LLVM\bin`). This shows which parts of `runtime/vpl/*.h` (the cycle
  collector, `Weak`, pools) no test runs.
- **Record the baseline numbers** in this file, so each later step can report its change.

Don't chase 100%. The number is for finding the large untested areas and for tracking the trend.

### Baseline (2026-10-26, MSVC suite, 264 tests)

| Layer | Lines | Branches |
|---|---|---|
| Syntax | 95.0% of 2069 | 89.2% of 1417 |
| Semantics | 95.4% of 4677 | 91.4% of 4698 |
| Ir | 95.8% of 928 | 91.6% of 823 |
| Lowering | 97.4% of 1164 | 91.6% of 850 |
| Analysis | 95.1% of 1785 | 88.7% of 1983 |
| Optimize | 98.2% of 668 | 95.6% of 832 |
| CodeGen | 97.3% of 1562 | 94.8% of 1546 |
| Driver | 69.0% of 248 | 52.9% of 136 (the g++ toolchain path isn't run by the MSVC suite) |
| all | 95.4% of 13101 | 91.0% of 12285 |

Sites (reached of all):

- **User errors** (`Error(` in Lexer, Parser, Binder; `Violation(` in the verifier): 216 of 281,
  plus 11 on lines that ran but where a branch didn't, so the site may not have. The 65 unreached
  are mostly the Lexer's literal errors, the Parser's declaration errors, duplicate-definition
  errors in `Binder.Declarations.cs`, extern-struct rules, and the three "refer into the nullable"
  errors in the binder (`Binder.Patterns.cs`, `Binder.Spans.cs`, `Binder.Statements.cs`).
- **Verifier checks** (the verifier's `Error(`, compiler-bug checks after a pass): 14 of 66. These
  are reached only by `IrSelfTests`, which builds bad IR on purpose; each unreached one is a check
  no test shows works.
- **Unreachable** (`throw new InvalidOperationException`): of 61, two are reached, both on purpose
  by `IrSelfTests` (`Callees.Fresh`, `IrLet`'s mismatch). Five more are on lines that ran with a
  `?? throw` or `: throw` that didn't fire. No test reaches one by accident.

**Triage of the user errors (done).** Each unreached one got a test or was removed: the
unpaired-surrogate error is unreachable from a file, so the lexer decodes it to U+FFFD as reading
a file does; "constructors cannot have type parameters" was dead, since a constructor is
recognized by its name and '('; three binder fallbacks (`ref? var` outside a local, a missing
stdlib `Array`, a non-escaping function no variable makes so) are now `InvalidOperationException`s.
It found: a cascade error after a default that depends on itself, "a enum", and the rule that a
reference can't be kept into a nullable value written out three times (`CheckReference`,
`ConvertToNullableReference`, the `is ref` pattern) and the loop check twice, now one
`CheckReferredPlace` and one loop check. What's left in the summary as "partial" is a `?:` inside
a message's text. The verifier checks are next: each gets an `IrSelfTests` case.

**Triage of the verifier checks (done).** Each unreached one got an `IrSelfTests` case
(`tests/Vpl.Tests/IrSelfTests.Rejections.cs`) building IR that breaks that rule alone and must
produce exactly its one error. All 65 are now reached. One check was removed instead: "only a
memberwise constructor's call leaves fields out" was enforced by structure already, since only the
memberwise constructor's parameters carry a `Field`, so the argument check rejects such a call. No
check needed fixing: each fired as written.

Line coverage locates a site well enough (no site lacked coverage data), so `Error` and
`Violation` didn't need caller-location parameters. Runtime coverage (clang profiling of
`runtime/vpl/*.h`) isn't measured yet.

## Step 2: sanitizers in the test runner

Add a toolchain to `NativeCompiler` (`Toolchain.ClangSanitized`, runner flag `--cc asan`) that
compiles with clang-cl, `-fsanitize=address,undefined`, and `-DVPL_DEBUG=1`, using the flags
CLAUDE.md lists today (`_ALLOW_COMPILER_AND_STL_VERSION_MISMATCH`, `-EHsc`, `-std:c++20`). Any
sanitizer report fails the test. It becomes the fifth mode to run before committing, and CLAUDE.md's
manual ASan recipe is deleted ("replace, don't layer").

**Done (2026-10-26).** clang-cl links UBSan here, so the mode runs both; UBSan reports and
continues, so the runner looks for reports in standard error rather than the exit code. A
`sanitizers/live` result builds a deliberate signed overflow and use-after-free and fails unless
both are reported. It runs without a precompiled header, at about 75 s for the suite. Its first run
found two bugs that every other mode passed:

- **Use after free in nesting** (`spans/string_bytes`): `ReadOnlySpan<byte> t = make(3).bytes();`
  nested the string into the statement, so C++ destroyed it at the `;` while the span still pointed
  into it; the freed bytes happened to be intact. Nesting now declines where a local that outlives
  the statement may refer into the temporary, asking `IrExtent.RefersInto`, the rule the hold
  analyses already used (docs/design/ir.md §5.1).
- **Null pointer arithmetic in the runtime** (`basics/string_methods`): appending zero bytes to an
  empty `StringBuffer` computed `block_->bytes()` on a null block.

## Step 3: property tests for the type system

These run in-process and fast, with no C++. Since `VplType` became one sealed subclass per kind,
random types are easy to generate: pick a kind, then pick its parts recursively from the built-in
types and a small set of declared stdlib types, with a depth limit.

Properties to check for random types, as a new set of tests next to `IrSelfTests` (they would be a
`TypeSelfTests`):

- `t.MapParts(p => p) == t` (interning holds, and `MapParts` rebuilds the same type).
- `TypeMap.Empty.Apply(t) == t`, and substituting `T := t` into a parameter-only type gives `t`.
- `t.CppIdentity.CppIdentity == t.CppIdentity`.
- `t.ContainsTypeParameters == t.ContainedTypeParameters.Any()`, and substituting away every
  contained parameter leaves none.
- Conversions: `IsImplicit(t, t)`; `IsImplicit` implies `IsExplicit`; `t` converts to `t.Nullable`
  whenever `t.CanBeNullable`.
- `t.Name` parses back to `t`, through the parser and the binder's type binding.
- The facts agree with each other: `MayOwnObjects` implies `IsCounted`, and a defaultable struct has
  a defaultable type for every field without an initializer.

Each failure is either a bug or an exception to write down in the spec. Both outcomes are useful.

**Done (2026-10-26).** `tests/Vpl.Tests/TypeSelfTests.cs` generates 1,500 random types (seed 1)
from the built-in types, every standard library type, and a fixture's, in the places the binder
allows them (a ref struct, `ref?`, or non-escaping function only as a parameter, `void` only as a
return, type arguments satisfying their bounds), and checks 20 properties in about 1.5 s: the ones
above, plus that the facts decided per C++ type (`IsCounted`, `InlineTypes`, `HasDefaultCode`, ...)
agree between a type and its `CppIdentity`, that a fixed array, a one-field generic struct, and a
nullable value agree with what they hold, that implicit conversions are transitive and
antisymmetric over the random types and those near them, and that distinct types have distinct
names except a function type's two forms. Names bind back by compiling each as a parameter of a
generic class's method and generating the types again from that compilation's symbols.

It found no bug, here or with seeds 2 to 5 at 6,000 types each. Planting two (a nullable function
type printed without parentheses, `MapParts` dropping escaping) fails five properties, so the
properties do bite. The one exception it found is now in the spec (02-types.md §2.3): `char`
converts to `int`, and `int` to `double`, but `char` doesn't convert to `double`.

## Step 4: interaction matrices with snapshots

This is the systematic answer to "did we think about X with Y". A matrix is a table whose rows and
columns are features, and each cell is a small program a generator writes from a template. The first
matrices, where most recent bugs were:

- **Places × operations.**
  - Places: local, `mut` local, field, element of a fixed array, `List` element, span element,
    `ref`/`in` local, `ref?`/`in?` local, nullable value (`int?`, `Vec3?`), captured variable,
    `this` field, and a field reached through a chain.
  - Operations: read, assign, compound assign, pass by value, pass `ref`/`in`, take `ref`/`ref?`,
    view as a span, loop over, capture in an escaping and a non-escaping lambda, narrow (`is`, `!`),
    return, return `ref`, and use while the place is reassigned.
- **Types × contexts.**
  - Types: one of each kind (struct, class, enum with data, nullable value, nullable reference,
    fixed array, function type escaping and not, generic instantiation, `nint`), plus a nesting of
    two.
  - Contexts: local, field, parameter, return, type argument, element, capture, `==`, default value,
    and overloads that differ only in it.

Each cell's program runs through every oracle above. Its result (the errors, or the output and exit
code) is written to a snapshot file per matrix, `tests/matrix/<name>.snapshot`, which is committed
and **reviewed once, cell by cell, against the spec**. That review is the step's real work: it is
where we find that a cell does something the spec doesn't say, or that the spec doesn't decide. After
that, a change that alters any cell shows up as a snapshot diff, which the runner reports as a
failure until the snapshot is updated on purpose.

Each matrix generator lives with the runner, and adding a feature means adding its row or column (a
new CLAUDE.md rule when this lands). The fraction of cells that run, plus the cells that are errors
the spec calls for, is the coverage number for that boundary.

## Step 5: a mutation fuzzer

This is a new project, `tests/Vpl.Fuzz`, that turns the existing tests into many near-valid
programs on feature boundaries and checks every one with the oracles.

**Mutations.** Parse a test with the real parser, use the AST's locations to pick a span, and splice
in replacement text. Mutations are chosen to cross features, not to scramble syntax:

- A type becomes its nullable form, a `ref?`, a one-element fixed array, a generic `Box<T>` of it, or
  `nint`/`long`.
- A local becomes a field of a new struct or class, an element, or a capture.
- An expression becomes a call to a lambda that returns it, a `?:` with itself, or a value read
  through a `ref`.
- A statement goes inside a loop over a span or a list, or inside a lambda that is called.
- A parameter becomes `in` or `ref`; a class becomes a struct; a struct becomes generic.
- Two mutations applied to one program, since boundaries are where two changes meet.

**Tiers**, so cheap checks run on everything:

1. Compile in-process, with and without `--no-opt`, so every pass and the verifier after it run, and
   no C++ is built. Any exception is a finding. Most mutants stop here with ordinary user errors,
   which is fine, and this tier runs thousands of mutants a minute.
2. For mutants that compile, build and run in every mode plus the sanitizers, and compare the
   output, the exit code, and the panic message. Any C++ compile failure, disagreement, or sanitizer
   report is a finding. Generated C++ includes the stdlib, so a build costs about a second per mode,
   and this tier needs parallelism and a time budget.

**Findings.** A finding is reduced automatically: delete lines and statements while the same
failure signature remains (which tier, which oracle, the exception type and its first message line).
It is then written to `tests/fuzz/findings/` with its seed. Fixing it means:

- moving the reduced program into `tests/cases/` as a regression test with expected output or errors
  (a new CLAUDE.md rule), and
- asking the question we already ask of every bug: what structure would have made it impossible?

**Running.** The fuzzer takes a seed, a time budget, and a test filter, and is deterministic for a
given seed, so a finding can be replayed. A short run (for example two minutes) is part of the
pre-commit routine; long runs go in the background.

## Step 6: later

- **A generating fuzzer.** Build well-typed programs from scratch, driven by types (Csmith-style),
  for combinations no existing test is near. Wait for this until mutations stop finding things.
- **The IR interpreter as an oracle.** It lets tier 2 check what a program prints, not only that the
  modes agree, and it checks the emitter and the passes against the program's meaning. It's already
  planned in architecture.md.
- **Mutation testing of the compiler** (Stryker.NET), scoped to one file at a time. It changes the
  compiler's code and checks that some test fails. That measures whether the tests would *catch* a
  bug, which says more than line coverage, but it reruns the suite once per change, so use it only
  for important files (the verifier, `PlacePath`, `Conversions`).

## Exit criteria for the hardening phase

New language features resume when:

- coverage (step 1) has been measured, and every layer's uncovered error sites and
  `InvalidOperationException` sites have been triaged: each gets a test, or is deleted as dead;
- the sanitizer mode passes on the whole suite;
- the first two matrices have been reviewed against the spec, and every cell that disagreed is fixed
  or decided in the spec;
- the fuzzer has run for an agreed budget (for example 50,000 tier-1 and 2,000 tier-2 mutants) with
  no open findings.

The numbers to keep tracking afterwards are: coverage per layer, diagnostic coverage, matrix cells
reviewed, and fuzzer findings per hour of running.
