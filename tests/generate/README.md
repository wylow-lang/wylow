# Generated programs

`tests/Wyl.Generate` makes random, well-typed Wylow programs from a seed, without expected outputs:
the IR interpreter is the program's meaning, so a generated program needs only to agree with every
native mode. This directory holds the findings the runner will write, each named by the finding's
key and signature.

A seed and a program index together choose one program (`--seed 7 --replay 12` will rebuild it
once the runner lands); the same pair gives the same text in every process and on every machine.
Stage 1 makes integers, `bool`, `char`, `float`, `double`, and `String` programs; later stages add
structured values, classes and containers, then generics and lambdas.

A generated program that does not bind is a **generator bug**, not a compiler finding: the tool
prints its diagnostics and exits with its own code. A finding is a bug until it becomes a test in
`tests/cases/`, with `// output:`, `// error:`, or `// panic:`, and the finding file is deleted.

`Wyl.Generate --self-test` checks the generator itself: the same seed generates the same text,
every constructor of every type can make that type, the random corpus reaches every type, the
budgets hold, and no program contains the text the oracles scan for (`unsafe`, `@Unchecked`,
`// mode: optimized`, `Memory.collectCycles`, `cycleStats`, `panic(`).
