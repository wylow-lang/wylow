# Generated programs

`tests/Wyl.Generate` makes random, well-typed Wylow programs from a seed, without expected outputs:
the IR interpreter is the program's meaning, so a generated program needs only to agree with every
native mode. Findings are written here, each named by the finding's key and signature.

Tier 2's modes include a `--release` build, which the fuzzer's don't: a generated program never
overflows, so release, where overflow wraps instead of panicking, must agree with the interpreter.

A seed and a program index together choose one program; `--seed 7 --replay 12` rebuilds it. The
same pair gives the same text in every process and on every machine. Stage 1 makes integers,
`bool`, `char`, `float`, `double`, and `String` programs; later stages add structured values,
classes and containers, then generics and lambdas.

Each run works in `tests/.out/generate/seed<S>-<pid>/p<N>/`, where `p<N>.wyl` is the source and the
subdirectories are the modes' builds. Without `--keep` the run directory is deleted; the findings
here survive.

A finding is a bug until it becomes a test in `tests/cases/`, with `// output:`, `// error:`, or
`// panic:`, and the finding file is deleted. Its header names the seed, program, stage and size,
and the `--replay` command that reproduces it.

A generated program that does not bind, or whose meaning panics or never finishes, is a **generator
bug**, printed as `GENERATOR BUG`, written here with the binder's diagnostics or the panic, and
exiting with code 2; it is never a compiler finding. Any
generator bug means the typed emitter, not the compiler, is wrong.

`Wyl.Generate --self-test` checks the generator itself: the same seed generates the same text,
every operation's range contains its true result, its table of the spec's implicit conversions
agrees with the compiler's, every constructor of every type can make that type, the random corpus
reaches every type, the bound corpus contains each mixed-type construct as the compiler reads it
(widenings, adopted constants, casts), the budgets hold, and no program contains the text the
oracles scan for (`unsafe`, `@Unchecked`, `// mode: optimized`, `Memory.collectCycles`,
`cycleStats`, `panic(`).
