# The type-directed generator

`tests/Wyl.Generate` makes random *well-typed* Wylow programs, in the Csmith/YARPGen tradition,
and checks each one against the IR interpreter and every native mode. There are no expected
outputs: the interpreter is the program's meaning, so every difference between it and a native
mode is a compiler or runtime bug. The shared oracle is `tests/Wyl.Harness` (the checker, the
comparison rule, the reducer, and the finding writer); the fuzzer and the test runner use it too.

## Shape

The generator is a typed printer, not an AST: every expression is source text plus the `WylType`
it has and, for an integer, a conservative value range (`GenExpr`). Every compound expression is
fully parenthesized, so adding a construct cannot break precedence, and the reducer shrinks at
statement granularity. Types are `WylType` values, so names and facts come from the compiler.

Generation is top-down: the emitter is asked for an expression of a given type and tries that
type's constructors in a seeded order. A constructor either yields exactly that type or is rejected
before it is written, and `Ground(type)` (a literal, or a local of exactly that type) is total, so
generation cannot fail. **The binder is the validator:** a generated program that does not bind is
a generator bug, reported with its diagnostics and never as a compiler finding.

`Rng` is SplitMix64. A program uses `Rng.Derive(runSeed, index)`, so one program can be regenerated
alone, and the same pair gives byte-identical text in every process and on every machine.
Generation happens before any parallel checking.

## Stages

- **Stage 1 (this one):** integers (`sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`,
  `ulong`), `bool`, `char`, `float`, `double`, and `String`; locals, assignment, `if`/`else`,
  bounded range loops, and zero to three helpers. `nint`/`nuint` are excluded: their width is the
  platform's, and the spec reserves them for unsafe code and interop.
- **Stage 2:** fixture structs and enums, nullables, `??`/`?.`, `switch`, fixed arrays and
  constant indexing.
- **Stage 3:** classes with constructors, methods, and destructors (destructor order is an oracle),
  heap arrays, spans, and `List`.
- **Stage 4:** generics, lambdas, `Weak`, and a bounded `while`.

## Trap-free by construction

A panic truncates output and hides later bugs, and integer overflow differs by build mode (debug
panics, release wraps; the interpreter is debug). So `IntegerFacts` is the one home for range
arithmetic and literal spelling, and a constructor is emitted only when its result range fits the
type. Division and remainder need a divisor range excluding zero and never `MIN / -1`; shifts use
constant counts in `0..width-1`; loops are `for (int i : 0..N)` with `N` in `1..4`; calls go only
to earlier helpers. Float arithmetic is emitted only when both operands are exact dyadic rationals
and the result is exactly representable (`FloatFacts`), so a C++ compiler cannot contract an add
and a multiply into an FMA and change a bit.

Never generated: `unsafe`, `@Unchecked`, pointers, `panic`, integer-to-`char` or integer-to-enum
casts, `Memory.*`, or `cycleStats`. `GeneratorSelfTests` checks that no program contains the text
the oracles scan for.

## Self-check

Every run, and `--self-test`, checks the generator before it generates: determinism, forbidden
text, constructor and type coverage (each constructor is forced once per type), and budgets. A
generator that silently collapsed to one constructor or one type says so instead of fuzzing.

## Limits

- A lowering bug shared by the interpreter and the emitter is invisible to this oracle; mutation
  testing and the expected-output tests remain the guard.
- Stage 1's programs do not exercise ownership, containers, or generics; those arrive with later
  stages.
- A difference in float results that survives the exactness rule is a real disagreement, not a
  reason to add a comparison exception without a spec decision.

`tests/generate/README.md` covers triage: a finding is a bug until it becomes a test in
`tests/cases/`.
