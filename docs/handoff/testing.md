# Testing: measure coverage, then generate tests

**Status:** planned 2026-10-26. Steps 1–5 have landed (coverage, `--cc asan`, `TypeSelfTests`,
both interaction matrices and the value axis, every line no test runs triaged, and the mutation
fuzzer, below). Step 5b (mutation testing the compiler) has its harness, its dashboard page, and
its first runs; the survivors those runs found are triaged below, and the long runs continue.
Step 6 records a survey of what other compilers test, and the work it suggests. Delete this file
once every step has landed, and move whatever it leaves as rules into CLAUDE.md and
`docs/design/architecture.md`; keep Step 6's survey as the phase's research backlog.

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
| The IR interpreter agrees | Emitter and pass bugs, against the program's meaning; a pass bug, without a C++ build | `--interpret`: part of `--all-modes`, running the meaning, the optimized IR against it, and the matrices (`interpreter.md`) |

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
  `llvm-cov` (both ship with LLVM). This shows which parts of `runtime/vpl/*.h` (the cycle
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

**Done (2026-10-27).** Coverage is measured over all six modes at once: `tools/coverage.sh` runs the
suite under each compiler and flag set, clears the precompiled-header cache so building one is
measured too, and merges the Cobertura files, so a line counts as ran when any mode ran it.
`tools/coverage-report.cs` takes the merged files; `--uncovered` lists every line no test ran and
`--partial` every line whose branches didn't all run.

The merged baseline before this step was 385 unrun lines (97.1% of lines, 92.9% of branches). It is
now 234 (98.2% of lines, 94.0% of branches), and each remaining line falls into one of these groups:

| Lines | Why it needs no test |
|---|---|
| 57 | **Unreachable throws.** `throw new InvalidOperationException` in the binder, lowering, verifier, and emitter. The report's "Unreached Unreachable sites" section is the fuzzer's target list (step 5). |
| 80 | **Guards after an error.** `return new BoundErrorExpression(...)`, `return null`, and `return false` once an operand or type has failed, so one error doesn't cause a second. The plan's kind 2: the ones whose absence would look like a second bug have a test; the rest are left. |
| 12 | **Arms and merges only hand-built IR reaches.** The `_ => false` and `fits = false` arms of the verifier's rvalue and argument checks (each check's rule has an `IrSelfTests` case), the reference-kind message of `IrLocal.Mismatch`, "the lambda is never made", the two "may reassign" reasons for a deref whose local is rewritten (no source form writes a reference or pointer while its deref is in use), and `Dependency.Union` (a local with a dependency can't be stored twice, since references and views are single-assignment). |
| 36 | **Unreachable by construction**, kept as guards: a returned reference from a call with no receiver (`PlaceBase.Returned`; free and static functions can't return references, 03-memory-model.md); a chosen escaping function local (the choice holds it through a conversion); the compound arms of `IrEffects`'s visitor; the emitter's narrowed-check arm and `vpl::unreachable` after a switch expression's void case; `FunctionValues`'s pointer and fixed-array arms; the map and overlap arms of variant and assumed projections (only fields and elements are written); the conversion steps of `PlacePath` and `Lowering.Places`; a literal object; a conversion between two nullable reference forms passed to `super(...)`; and the binder's arms for `super`, `ref`, named arguments, and type arguments in positions the parser doesn't produce. |
| 12 | **Reachable but left**, each for a stated reason: an address argument inside a hold (`IrExtent`), a by-value argument kept from a copied read (`Lowering.Expressions`), a tie between exactly one generic and one non-generic overload, a deferred local assigned from an alias-copying lambda, the final-error path of inference (its message is tested where inference fails normally), the rvalue overload of `IrWalk.Places` (a nesting path no test reaches), a pattern's integer-key format, and `SyntaxFacts`' default operator name. |
| 10 | **Parser recovery.** The `-1`s of `ScanType`, the `Advance()` after a statement that consumed nothing, and two lookahead returns: malformed syntax no error test writes yet, which the fuzzer will. |
| 16 | **The CLI and a debugger.** `Diagnostic.ToString` and its enumerator (the `vpl` CLI prints them; the runner compares messages), the eight `Symbol` and IR-node `ToString` overrides, and building a compilation with errors. |
| 11 | **The machine's toolchain.** The "no compiler found" messages, the PCH mutex's abandoned case and an IO failure, and the timeout kill. |

`--partial` finds 449 lines that ran with an unrun branch: the other side of `?? throw`, `?.`,
`is { }`, a `switch` arm's `_`, and the singular/plural inside messages. Kind 3, needing nothing.

The unrun lines that meant something now have tests. New cases reach a lambda's returns converting
inside `while`, `for`, `for-range`, and `switch` under an inferred return type (`lambdas/returns.vpl`);
`int main()`; an inherited method called through a `static_cast`; `switch (this)`; a write set over 64
places; a delegating base call that also passes the location; a `ref?` base argument; a view returned
by a type parameter's method; a struct's implicit default; a switch over each integer kind; and the
call, operator, pattern, array, interpolation, annotation, and generics errors the matrices didn't
reach. Extended cases cover `%` on doubles, `.length` of a non-place, a unary default value, constant
conditions and unreachable statements, a literal and a `var` pattern, `null == x`, a reference defined
by another, a string literal's view, a pointer assigned twice, choices of function locals and of
capturing lambdas, an overload tie, every compound assignment and prefix `++`/`--`, a compound
assignment through a computed index, the `\r` and `\0` escapes, a closed block comment, and a
narrowed `int` widening to `long`.

Dead code is gone: `BoundTree.CallOf` and `CallSite` (nothing called them, though CLAUDE.md told a
new call kind to update them), the unused `Count` of `BorrowedLocals` and `LastUseMoves`, `IrPrinter`'s
`and`/`or` arms (logical operators lower to `IrChoose`), `Callees`' implicit-struct-constructor branch
(an implicit constructor runs as `IrRunDefault`, never as a method), and `IrPlace.Prefix`.

It found one rule the spec didn't state: a statement that can never run, after `return`, `break`,
`continue`, `panic`, or a call that never returns, is dropped silently. It is now in 06-syntax.md and
tested (`basics/control_flow.vpl`), and lowering's four `if (!reachable) break` guards are reached
only that way.

## Step 4c: the cells test shapes, not values

The matrices cross a shape with a shape (a place × an operation, a kind of type × a context), and
that axis is covered well; the reviewed snapshots are what made it worth doing. But reading the two
generators shows that every cell's *value* comes from a very narrow set, and that hides a different
kind of bug — the simple one, in the middle of a feature, that no mode and no oracle can notice:

| Both matrices | So invisible to the snapshots |
|---|---|
| every integer is `1`, `2`, `3`, `5`, or `7` | `0`, negatives, `INT_MAX`/`INT_MIN`, overflow at a size or count |
| no cell computes: one `+= 2` in places, a cast and a `??` in types | precedence, `- * / %`, comparison results, widening and narrowing conversions, formatting |
| `equality` compares `3` with `3` | the unequal side of `==`, and `!=`, `<`, `>`, `<=`, `>=` entirely |
| a fixed array or list always has three elements, and the place is index `0` (the fixed-array reader prints all three, so a write inside the array does show) | empty containers, one element, the last index, a length of `0` |
| a nullable value is never null, so `?? -1` and `?.value ?? -1` never take their fallback | the null side of `??`, of `?.`, and narrowing's else |
| the fixture declares `Shape.Empty` and no cell makes one | a variant without data |
| rows are one per *kind* of type | `String`, `double`, `long`, `char`, `bool` — not kinds, but what programs compute with |

The fix is additive; none of this reworks the two matrices:

- **Widen cells whose shape hides the point.** Each equality cell compares an equal pair and an
  unequal one; each nullable cell prints once non-null and once null; each place is used at index `0`
  and at the last index, and once on a one-element container. That changes most of both snapshots, so
  it is one deliberate `--update-snapshots` and one reviewed diff.
- **Add rows** for `String` (empty, one character, a multi-byte character, concatenation), `double`
  (`-0.0`, a value that does not print exactly, `%`, comparison), and `long` (past `INT_MAX`). A row
  per value kind follows the same rule as a row per type kind.
- **A third matrix, values × operations** (`MatrixValues.cs`): value edges (`0`, `1`, `-1`,
  `INT_MAX`, `INT_MIN`, empty, one element, last index, null, unequal) against operations
  (arithmetic, comparison, indexing, iteration, conversion, formatting, switch), one small program
  per cell, about 70 cells, reviewed once like the others.
- **Value-printing tests** in `tests/cases/values/`: one file per builtin value kind that prints
  thirty or more computed results — every operator, every conversion, every formatting — at the
  edges. An expected-output test is the only mechanism here that catches a wrong answer every mode
  agrees on, and one such file catches dozens of them at once.

The review is the same work it was for steps 4 and 4b: "the spec doesn't decide this value" is as
much a finding as a wrong one.

**Done (2026-10-27).** All four parts landed:

- **Widened cells.** Every comparable row's equality cell now prints an equal pair, an unequal pair,
  and `!=`; both nullable rows print their null side in every context that can express one, and
  compare with null; the element places became three rows each (`-first`, `-last`, `-single`), so a
  place is read and written at index 0, at the last index, and on a one-element container. The
  `types` matrix grew from 120 to 180 cells and `places` from 210 to 300.
- **Added rows** for `String` (empty, one character, a multi-byte character, concatenation), `double`
  (`-0.0` and an inexact `0.1 + 0.2`), and `long` (past `INT_MAX`); `char` and `bool` are covered by
  the values matrix and the value-printing tests instead of a type row.
- **`MatrixValues.cs`**: 80 cells of value edges (`0`, `1`, `-1`, `INT_MAX`, `INT_MIN`, a long past
  it, `-0.0`, an inexact double, an empty array, a one-element list, the last index, the empty and
  multi-byte strings, a code point past ASCII, `true`, null as a value and as a reference, an
  unequal pair) against the operations each supports. Panics are recorded as results: overflow at
  `INT_MAX` and `INT_MIN`, division by zero, and an index past the end.
- **Value-printing tests** in `tests/cases/values/`, one file per builtin kind (15 of them: `bool`,
  `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `nint`, `nuint`, `float`,
  `double`, `char`, `String`), thirty or more computed results each.

The review found four places the spec didn't decide, and one compiler bug:

- **A `String`'s and a primitive's default value** were not stated (2.13 said `String` is
  defaultable, but not that its default is the empty string, nor what a primitive's is). Now:
  zero for primitives, the empty string for `String`.
- **Floating-point division by zero** was not decided: "Division by zero panics in every build" read
  as if it covered floats, but the language (and `conversions.vpl`'s `0.0 / 0.0`) means IEEE. 2.4
  now says integer division panics and floating-point division is an infinity or NaN.
- **A floating value's printed text** was unspecified beyond examples. `String` interpolation now
  says it is the shortest text that reads back exactly, in `std::to_chars`'s choice of plain or
  exponential form, with `.0` on a whole number, and `inf`/`-inf`/`nan`.
- **A case literal must be written with a suffix when it is past `INT_MAX`** (`case 2147483648L`),
  since a literal too large for `int` is an error before it takes the subject's type.
- **The bug:** `float.vpl` and `double.vpl` failed to compile under MSVC, which diagnoses
  `1.0 / 0.0` in generated C++ as C2124 (divide or mod by zero) even though VPL defines the result
  as an infinity. The emitter now emits floating division through `vpl::fdiv`, which is not a
  constant expression, and the value tests show `inf`, `-inf`, and `nan` in every mode.

The value files also pinned down what the two toolchains agree on: both MSVC and libstdc++ print
`2^63` as `9223372036854775808.0`, not in exponential form, so the suite holds each to the same
text. `--update-snapshots` was run once for each matrix; the reviewed diff is in the change.

## Step 5: a mutation fuzzer

This is a new project, `tests/Vpl.Fuzz`, that turns the existing tests into many near-valid
programs on feature boundaries and at value edges, and checks every one with the oracles.

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
- **Value edges, not only crossings.** A literal becomes `0`, `1`, `-1`, `INT_MAX`, or `INT_MIN`; a
  length becomes `0` and `1`; a collection initializer keeps only its first element; an index becomes
  the last one. These aim at the middle of a feature, where an off-by-one, an overflow, or an empty
  case lives, which a crossing mutation never reaches.
- **A single, in-feature mutation.** One integer kind becomes another (`int` to `long`), an operator
  becomes its neighbor. Every crossing above needs two features to meet; most of the compiler's
  ordinary code sits inside one feature, and this is the mutation that reaches it.

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
pre-commit routine for compiler changes; emitter, docs, and dashboard changes rely on the suite, and
long runs go in the background.

**Done (2026-10-27).** `tests/Vpl.Fuzz` is a console project in the solution:

- **Mutations.** It parses a test with the real parser and its AST locations, then splices text at
  one site, or at two for half the mutants. The catalog crosses features (a type becomes nullable,
  a fixed array, its integer neighbor; a parameter becomes `in`; a local becomes `mut`, a
  reference, a nullable reference, or null; a statement goes inside an `if`, a range loop, a fixed
  array loop, or a non-escaping lambda; an expression is duplicated through `?:`, a switch, a
  one-element fixed array, a capturing lambda (`T eval<T>(() -> T)`) or an `in` parameter
  (`T read<T>(in T)`) — the last two append their helper declaration) and values (every literal
  kind's edges, `0`/`1`/`-1`/`INT_MAX`/`INT_MIN`, collection initializers reduced to one element,
  array lengths `0`/`1`, indexes `0` and `last`, every operator moved to its neighbor). Error tests
  are skipped by default (`--errors` includes them), and a mutation whose `unsafe`/`@Unchecked` base
  may legitimately do undefined behavior has its sanitizer reports ignored as inconclusive.
  Three crossings need an edit at every use of the name, so they are left out: a local becoming a
  field, a struct becoming generic, and a parameter becoming `ref` (its calls would all need
  `ref` too).
- **Tiers.** Tier 1 compiles each mutant in-process, with and without `--no-opt`, compares the
  error sets (they must not depend on optimization), and compiles it a second time to check that
  compilation is deterministic. Tier 2 adds the six modes (the interpreter, MSVC, g++, ASan/UBSan,
  `--no-opt`, `--no-nest`), the interpreter as the reference: the C++ must compile under `-Wall
  -Wextra -Wconversion -Wsign-conversion -Wshadow`, every sanitizer must stay quiet, and the modes
  must agree on stdout, the exit code,
  and the panic text (output is not compared when the program calls `Memory.collectCycles`). It
  builds with a capped, time-limited runner, so a mutant that loops forever or prints without end
  is inconclusive rather than fatal. Throughput on this machine: about 1,500 tier-1 mutants a
  minute, and about 40 tier-2 mutants a minute with eight workers.
- **Findings.** A failure is reduced by deleting statements, switch cases, and declarations while
  the same coarse signature remains (the exception type, the C++ error, the sanitizer kind, the
  disagreeing modes), writes `tests/fuzz/findings/<key>-<hash>.vpl` with the seed, mutant index,
  and mutations, and replays with `--replay <index>`. Its per-mutator reducer re-parses after every
  accepted deletion, so a span is never used after the text moved; a simple statement's span is
  extended over its `;`, which the parser leaves out.
- **The first run found a binder hole**: `in` or `ref` on a *nullable* function-typed parameter
  (`in ((int) -> int)? f`) passed `CheckParameterMode`, which looked through `NullableReferenceType`
  but not the nullable-value wrapper, so lowering produced an operand argument the verifier
  rejected as a compiler bug. Running it down found the same hole for `ref?` locals and `ref`
  returns, and `TypeSelfTests` failed at once on `ref? (() -> int)?` names that no longer bind. The
  question is now one type fact (`VplType.PassedWithoutCopying`), used by all three checks, and
  `tests/cases/errors/lambda_binding.vpl`, `operands.vpl`, and `references.vpl` have the errors.
- **The long runs (30,720 tier-1 and 256 tier-2 mutants) found three more**:
  - `switch (null)` bound, and the emitter had no C++ type for `null`; it is now a binder error
    (`errors/switch.vpl`).
  - A `void` call passed to a generic function inferred `T = void`, emitted as a `const void&`
    parameter, and failed to compile; inference now rejects a `void` binding (`errors/generics.vpl`).
  - Two switch temporaries nothing reads drew `-Wunused-variable` and `-Wunused-but-set-variable`:
    the emitter's `[[maybe_unused]]` was applied only to source-named locals, not temporaries kept
    for what making them does. It now covers every zero-use local; `basics/control_flow.vpl` has a
    `default`-only switch over a literal, and the strict warning check is the oracle that sees it.
- **The next runs found four more**, each with a new test:
  - Indexing an `in` or `ref` pointer parameter lowering dereferenced the parameter's storage (the
    pointer) rather than the pointer's target, so `values[2]` made an error-typed projection and the
    verifier threw. Only a local whose dereference is its pointee takes the direct place now; the
    one that isn't is read into a temporary first. `unsafe/memory.vpl` indexes through `in int*`
    and `ref int*`, and writes through the former.
  - An `is ref` name over a method called on a temporary receiver (a conditional's value, a call's
    result) had no holder outside the condition's block, so the verifier reported the name as used
    after its object went out of scope. A temporary receiver is held like a local now;
    `references/temporary_holders.vpl` prints the destructor's turn on both a matching and a
    failing test.
  - The pointer case exposed the emitter writing `const int32_t*&` for a reference to a pointer,
    where the `const` belongs on the pointer itself (`int32_t* const&`). `ConstReferenceType` is
    now the one place that spelling is written, for `in` parameters, reference and borrowed locals,
    returns, and captures.
  - A narrowed `String?` receiver of a method that returns a view (`.bytes()`) took the no-hold
    path, because `SharesStorage` named `String` but not a nullable value of it. The fact now looks
    through the nullable, the receiver holds the value (`ObjectLocal` reuses the access's place
    rather than its raw local), and a `!` on a nullable value checks `HasValue`, not `NotNull`.
    `spans/string_bytes.vpl` takes views through a narrowed and an asserted nullable string, and
    `TypeSelfTests` requires a nullable value and its value to agree on `SharesStorage`.
- **The fuzzer's oracles were narrowed for four kinds of non-finding**, each with a comment saying
  why:
  - a `// mode: optimized` test is not compared under `--no-opt`, which the suite skips too;
  - a warning about the program's own constant comparison (`-Wtautological-compare`,
    `-Wtype-limits`) is the source's, not the emitter's;
  - a mutant whose edit is in `unsafe` code is compiled but not run in tier 2 (the compiled C++ and
    its warnings are still checked), since its behavior is undefined;
  - an AddressSanitizer stack overflow is the runaway-recursion counterpart of a timeout, so the
    mode is inconclusive rather than a finding.

The fuzzer's catalog is the matrix rule's peer: a new feature adds a mutation or value edge, and a
finding becomes a test in `tests/cases/` and its finding file is deleted. Both are enforced only by
review, like the matrices. The suite runs the fuzzer's own `fuzz/catalog` and `fuzz/determinism`
checks (also `--self-test`), so a mutation that no longer applies anywhere, an edit that changes
nothing, or generation that stops being reproducible from a seed fails like any other test.

## Step 5b: mutation testing the compiler

Every oracle in steps 4 and 5 checks that the compiler is *consistent*: no exception, every pass's
output accepted, every mode agrees. A simple defect — `<` emitted as `<=`, an index into `args` off
by one, a field not zeroed, a message naming the wrong type — is none of those, and all five modes
make the same mistake, so every oracle passes it. (The interpreter checks the passes against the
meaning, and its agreement with the native modes checks the emitter, but it shares lowering.) Line
coverage does not help here either: it says the line ran, not that it did the right thing.

Mutation testing asks the other question: *would the tests notice?* It changes the compiler's own
source — flip a comparison, delete a statement, swap a constant — reruns the tests, and reports every
change no test caught as a **surviving mutant**. A survivor is either a missing test or a line
nothing needs, and that list is the only systematic way to find a wrong answer every mode agrees on.

- **Tool.** Try Stryker.NET (`dotnet tool install -g dotnet-stryker`). It drives a test framework
  through `dotnet test`, and this project's runner is a console app (`Vpl.Tests`, with a path
  filter), so if the adapter isn't straightforward, write the loop directly: apply one mutation to
  the compiler's source, `dotnet build`, run `Vpl.Tests <filter>`, restore, repeat. Either way the
  output is the same survivor list.
- **Scope, first pass**, one file at a time: `Ir/IrEffects.cs`, `Ir/IrAccess.cs`,
  `Semantics/Conversions.cs`, `Semantics/PlacePath.cs`, `CodeGen/CppEmitter.Types.cs`,
  `CodeGen/CppEmitter.Ir.cs`. These decide operators, storage, conversions, and printed C++, which is
  where a survivor means a wrong answer, not merely an unexercised line.
- **Cost.** The suite builds C++, so a whole-suite mutant costs minutes; that is why this runs on a
  few files, in the background (announce it), and why the covering tests are cut down with a path
  filter as soon as a file's feature is known. Record a mutation score per file here.
- **Triage.** Every survivor becomes a test (step 4c's value edges and value-printing tests are
  largely what this will demand) or a deleted line. The second outcome is a finding too: a line no
  behavior depends on is a rule enforced by nothing.

This runs alongside step 5 rather than after it: the fuzzer finds what crashes and what disagrees,
and this finds what is quietly wrong. They need different harnesses and share no findings.

**Done (2026-10-28).** `tests/Vpl.Mutation` is a console project in the solution, and the dashboard
runs it from its Mutation page:

- **The loop, not Stryker.** Stryker.NET drives `dotnet test`; this suite is a console runner with a
  path filter that builds C++ for every case, so the adapter would have to be the whole runner, and
  it would rebuild the solution for every mutant. The loop is written directly: write the mutated
  file into a private copy of its project, `dotnet build` only that project, copy its assembly into
  a private copy of the test runner, and run the covering tests. Neither the working tree nor
  `tests/Vpl.Tests/bin` is touched, so a cancelled run leaves nothing behind and the next run starts
  from a refreshed copy. The runner's precompiled header (250 MB) is copied once and reused.
- **The catalog.** Roslyn (`Microsoft.CodeAnalysis.CSharp`) finds the sites, so an operator in a
  string, comment, or generic argument is never one: `==`/`!=`, `<`/`<=`, `>`/`>=`, `&&`/`||`,
  `+`/`-`, `*`/`/`, `+=`/`-=`, `is T` → `is not T`, `is null`/`is not null`, `0`/`1`, `true`/`false`,
  a removed `!` or unary `-`, a negated `if`/`while`/`do`/`for`/`?:` condition, and a whole-line
  expression statement deleted. A parameter's default value is left out: C# compiles it into every
  caller, so a test built against the old value can't see the change.
- **Targets and filters.** `tests/mutation/targets.json` names the six files and the test filters
  that cover each feature. Filters run in order and stop at the first that fails, so a cheap
  in-process filter kills most mutants before the C++-building ones run. A filter that matches
  nothing is a silent hole: on Windows the runner matches `values`, not `values/`, because case
  paths use `\`.
- **Results.** `tests/mutation/results/<target>.json` is rewritten after every mutant, so a stopped
  run still records what it saw. The dashboard's Mutation page shows the score per target, the
  survivors (with the `site N` that `--only` reruns), and the recent runs. `--max`, `--seconds`, and
  `--seed` sample a target deterministically; `--only N` reruns one survivor with another filter,
  the whole suite included, for triage.

**The first runs and their triage.**

- **`IrEffects.cs`.** With the verifier's self-tests (`ir/`) alone, 6 of 41 mutants were killed, and
  the full suite killed 3 of the first 5. The two it missed — `IrEffects.None`'s and
  `IrEffects.Releasing(type)`'s `MayPanic` — are facts no program reaches: every mode agrees with
  the wrong answer. `tests/Vpl.Tests/IrSelfTests.Effects.cs` now states every effect fact the
  verifier, the passes, and the emitter decide by: which locals a release runs, what a block
  releases and what `end` leaves out, what a store writes and what a move of a named local writes,
  which calls run and may panic, what a constructor and a default run, when an operation may panic,
  and when a hold borrows. With it and the default filters, all 40 mutants die (100%); `ir/` alone
  kills 40 of the 41 the old catalog had, and the survivor was the harness artifact below.
- **`containers` is the filter that sees releases.** Under the full suite,
  `containers/loop_elements_destructor.vpl` is what kills the `IrEffects.Ending` and
  `ReleaseMayRunCode` mutants, so it is in the default filters (7 tests, 3 s).
- **A parameter's default is not a mutant.** C# compiles it into every caller, so a test built
  against the old value cannot see the change; the catalog leaves one out.

The other five targets have their filters in `targets.json` and are run from the Mutation page; each
run records its score in `tests/mutation/results/` as it goes, and the page shows the newest run.

The six targets are a first pass, not the end: the survivors left by a sampled run are triaged the
same way, into a test or a deleted line, and the runs that would find more are the ones with a
bigger budget.

## Step 6: later

- **A generating fuzzer.** Build well-typed programs from scratch, driven by types (Csmith-style),
  for combinations no existing test is near. Wait for this until mutations stop finding things.
- **The IR interpreter as an oracle (done, `interpreter.md` step 6).** It lets tier 2 check what a
  program prints, not only that the modes agree, and it checks the emitter and the passes against
  the program's meaning. It is the sixth mode, runs the matrices, and is a mutation target.
- **Mutation testing of the compiler, widened.** Step 5b runs it on the six files where a wrong
  answer hides; once it is routine, extend it to the binder and lowering, at a file at a time.

### What other compilers test, and what to add (survey, 2026-10-28)

From LLVM (lit/FileCheck regression tests, unit tests, a whole-program test-suite, `llvm-reduce`,
`XFAIL`/`UNSUPPORTED`), GCC (DejaGnu directives, and "torture" runs of one test under many option
sets), rustc (exact UI diagnostics with `--bless`, expected MIR after optimizations, and codegen,
assembly, and incremental suites), Csmith and YARPGen (UB-free random program generation with
cross-compiler and cross-optimization oracles), EMI (mutating code the current input never runs),
Alive2 (SMT translation validation, plus an IR interpreter), Miri (an interpreter that detects UB in
unsafe code), OSS-Fuzz (coverage-guided front-end fuzzing with sanitizers), and CompCert (a verified
compiler that still runs Csmith, YARPGen, differential tests, and GCC's torture suite). test262 and
the WebAssembly spec tests are the model for an external conformance suite.

| Practice | VPL today | Next step |
|---|---|---|
| Expected-output, exact-diagnostic, unit and property tests | `tests/cases`, `IrSelfTests`, `TypeSelfTests` | — |
| One reduced test per bug | fuzzer reducer, `tests/cases` | — |
| Option/mode matrices ("torture") | six modes, matrices | keep every case in every mode |
| Reviewed golden IR after each optimization (rustc `mir-opt`) | `.ir` written, `// stack:`/`// in-place:` markers | a small reviewed `.ir` snapshot per pass |
| Well-typed random program generation (Csmith/YARPGen) | none | the generating fuzzer above |
| A differential oracle | the interpreter, on every test and in tier 2 (done) | the generating fuzzer above, with no expected outputs |
| Metamorphic/EMI transformations | some fuzzer mutations | name them, and add dead-code mutation |
| Translation validation | verifier after each pass (invariants only) | interpreter differential; no SMT |
| UB detection | ASan/UBSan mode | interpreter for `unsafe`/stdlib; MSan/LSan on Linux CI |
| Coverage-guided front-end fuzzing (OSS-Fuzz) | mutations of valid tests | syntax chaos, then SharpFuzz if wanted |
| `XFAIL`/`UNSUPPORTED` | none | runner directives for toolchain and platform cases |
| Compile-time and code-size tracking | dashboard has code size | budgets and trends per commit |
| External conformance suite (test262/wasm) | matrices and spec chapters | an id per spec rule, cited by a test |
| Formal verification (CompCert) | — | not worth it; test the untrusted edges instead |

**Order, highest value first.**

1. **The IR interpreter as a universal oracle.** Run every test through the unoptimized IR and
   compare its stdout, exit code, and panic with the native modes. Every current oracle shares
   lowering, so this is the only one that checks what lowering means.
2. **The generating fuzzer, with the interpreter as its oracle,** comparing the interpreter with
   every native mode. This is the Csmith/YARPGen model and the source of most historical compiler
   bugs.
3. **Front-end fuzzing of arbitrary input.** A syntax-chaos mutator in `tests/Vpl.Fuzz` (insert,
   delete, duplicate tokens) that requires only no exception and no hang; SharpFuzz if coverage
   guidance is wanted. Error recovery is where OSS-Fuzz finds clang bugs and where the current
   fuzzer, which starts from valid tests, is blindest.
4. **Per-pass golden IR.** A few reviewed `.ir` snapshots per optimization, so a pass that silently
   stops firing fails even though the verifier accepts its unchanged output.
5. **Metamorphic/EMI transformations as a named oracle.** Rename locals, wrap statements in
   `if (true)`, insert dead declarations, and mutate code the input never executes; output must not
   change.
6. **Runner and CI hygiene.** `XFAIL`/`UNSUPPORTED`, a nightly five-mode run plus fuzz plus one
   mutation target, and compile-time and generated-C++-size budgets.
7. **Runtime and stdlib model tests.** Randomized operation sequences compared with a simple model,
   cycle collector/`Weak`/pool stress, and MSan/LSan on a Linux CI.
8. **A spec-rule index.** One id per normative rule, and a test citing each.

Not worth doing now: formal verification (CompCert's own testing still finds bugs at the untrusted
edges), SMT translation validation (the verifier after each pass plus the interpreter differential is
the practical version), chasing line coverage (mutation answers whether a test would notice), and
any new oracle that shares lowering.

Keep this subsection when the rest of this file is deleted; it is the phase's research backlog.

Sources: [LLVM Testing Guide](https://llvm.org/docs/TestingGuide.html),
[GCC testsuites](https://gcc.gnu.org/onlinedocs/gccint/Testsuites.html),
[rustc-dev-guide](https://rustc-dev-guide.rust-lang.org/tests/intro.html),
[compiler-fuzzing survey](https://ar5iv.labs.arxiv.org/html/2306.06884),
[YARPGen](https://github.com/intel/yarpgen), [Alive2](https://github.com/AliveToolkit/alive2),
[Miri](https://github.com/rust-lang/miri), [OSS-Fuzz](https://github.com/google/oss-fuzz),
[CompCert testing](https://hal.science/hal-04096390v1/file/tests_CompCert_TAP23_preprint.pdf),
[test262](https://github.com/tc39/test262).

## Exit criteria for the hardening phase

New language features resume when:

- coverage (step 1) has been measured, and every layer's uncovered error sites and
  `InvalidOperationException` sites have been triaged: each gets a test, or is deleted as dead;
- the sanitizer mode passes on the whole suite;
- the first two matrices have been reviewed against the spec, and every cell that disagreed is fixed
  or decided in the spec;
- every line no test runs, measured across all six modes, has been triaged (step 4b);
- the value axis has landed (step 4c): the widened cells, the added rows, and the values × operations
  matrix are reviewed against the spec, and the value-printing tests are in the suite;
- the fuzzer has run for an agreed budget (for example 50,000 tier-1 and 2,000 tier-2 mutants) with a
  catalog covering crossings, value edges, and in-feature single mutations, and no open findings;
- mutation testing (step 5b) has run on its six files, and every surviving mutant is triaged to a
  test or a deleted line.

The numbers to keep tracking afterwards are: coverage per layer, diagnostic coverage, matrix cells
reviewed, mutation score per file, and fuzzer findings per hour of running.
