# Next: finish the concurrency review, before `Mutex`

Repo `E:\Repos\Wylow\wylow`, branch `dev`. Do this work before `concurrency-step-7.md`. Read
`docs/design/runtime.md` and `docs/design/testing.md` first.

The review's bugs and its duplicated answers are fixed. The commits from `f53d63e` to `9e47bff` fix
the order in which a closed channel drops its values, the receiver that spun, the order in which a
thread releases its lambda, the missing panic when a thread cannot start, the two answers for member
bounds and for the isolated check, and the interpreter's second dispatch. They also make the small
cleanups. The fix for member bounds also fixed two bugs that the review did not list: a `where`
clause did not apply inside a lambda, and it did not apply to a type written in the member's body.
Issue #35 records the allocator and cycle collector optimizations, which wait until later.

## How to work

- Ask the user whether CI passed on the commits above, and fix any failure first.
- After you write a test for a bug, put the bug back and confirm that the test fails.
- Keep every run short, with a timeout. A filtered native run takes about 45 seconds because the runner
  also runs its self-tests, so run several filters in parallel.

## Split the large files

Architecture.md says that a file past about 600 lines is split. Split these files by topic, in commits
that move code and change nothing else, so that a real change never hides in the diff:

| File | Lines |
|---|---|
| `IrVerifier.Values.cs` | 1183 |
| `Binder.Declarations.cs` | 891 |
| `CppEmitter.Ir.cs` | 767 |
| `IrVerifier.cs` | 717 |
| `Binder.Expressions.cs` | 709 |
| `Interpreter.cs` | 652 |
| `Binder.Patterns.cs` | 630 |
| `Interpreter.Cycles.cs` | 617 |

## Smaller duplicates the fixes found

- `BindBounds` and `BindWhere` in `Binder.Declarations.cs` repeat the `PropertyBound` switch and the
  check that a bound is an interface.
- `TypeSymbol.IsWeak` and `StdlibSymbols.IsWeak` answer one question, as the isolated check did before
  `e4decbc`. Keep the mark and move every caller.
- `MemoryIntrinsic` and `StringMethod` in the interpreter still switch on names as strings, so a wrong
  name fails only when a program runs it. Give each intrinsic its own method, as `02c6206` did for
  threads, channels, and isolated boxes.

## Finish the review

The review read the runtime, the interpreter's concurrency code, constructors, and moves closely. It only
skimmed the verifier, the emitter, the parser, and pattern lowering, or tested them from outside. It did
not read spec chapters 18 to 22, which came from the same run of commits. Read these areas, and report
what you find before you change anything large.
