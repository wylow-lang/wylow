# Fuzzer findings

`tests/Vpl.Fuzz` writes a reduced mutant here when an oracle fails: the compiler threw, its passes
disagreed with the verifier, the generated C++ did not compile warning-free, two modes printed or
panicked differently, or a sanitizer reported something. Each file starts with the finding, the seed,
the mutant's index, and the mutations that made it, so it can be replayed with `--replay <index>`.

A finding is a bug until it is explained. Fixing one means moving the reduced program into
`tests/cases/` as a regression test with `// output:`, `// error:`, or `// panic:` comments, then
deleting the finding file. A finding that was a false positive of the harness (for example, a
mutation of `unsafe` code that the sanitizer rightly reports) is fixed in `tests/Vpl.Fuzz`, not by
weakening the test.
