# Function types written `fn(Params) -> Return`

**Status:** decided in the 2026-10-31 design review and already in the spec (§6.7); not
implemented. The compiler still reads and prints `(Params) -> Return`. Delete this file when it lands.

Read first: `docs/spec/06-syntax.md` §6.7 (the design, which wins over this file), CLAUDE.md, and
`docs/design/architecture.md`.

## What to do

Make the compiler read and print function types as the spec writes them, and nothing else:

```java
fn(int, int) -> int add = (a, b) -> a + b;      // lambdas stay Java's
fn(ref Vec3) -> void scale;                     // modes are part of the type
fn() -> fn(int) -> int curried;                 // '->' groups to the right
(fn(int) -> int)? maybe;                        // parentheses only group, for a suffix
fn(@Escaping fn() -> void) -> void schedule;    // annotations on a parameter, as today
```

- `fn` becomes a keyword (no test uses it as a name). A function type starts with `fn`, so `(` no
  longer starts a type except to group one for a suffix. That should let the parser drop guesses it
  makes today to tell a parenthesized type from an expression: `ScanType`'s and
  `ScanFunctionTypeParameters`' handling of `(`, `StartsParenthesizedType` (annotation arguments
  vs. `@Escaping (int) -> int`), the `pub (int) -> int f;` special case in `ParseVisibility`, and
  `SyntaxFacts.IsTypeStart` including `(`. Simplify them rather than adapting them; a guess the
  grammar no longer needs is a second way of reading the same thing.
- The old spelling goes away entirely ("replace, don't layer"). Where a `(` type would have parsed,
  an error naming the new spelling (`function types are written 'fn(int) -> int'`) is worth having
  if it is cheap to detect without bringing back the guess; if not, skip it.
- `FunctionType.BuildName` (`TypeKinds.cs`) prints the new form, so every message does too, and
  `Grouped` keeps parenthesizing a function type before a suffix: `(fn(int) -> int)?`.
  `TypeSelfTests`' "names parse back" property checks that what it prints binds back to the type.
- Messages that show an example in the old spelling: `Binder.Escaping.cs`, `Binder.Expressions.cs`,
  and `Binder.Lambdas.cs` (grep the binder for `) -> `). Comments with the old spelling in `Ast.cs`,
  `Parser*.cs`, `SyntaxFacts.cs`, `Types.cs`, and `TypeKinds.cs` change with the code.
- Every test that writes a function type: 76 files under `tests/cases/` (grep `) -> ` and look,
  since lambdas use `->` too), the inline programs in `IrSelfTests.cs`, `InterpreterSelfTests.cs`,
  and the matrix generators (`MatrixPlaces.cs`, `MatrixTypes.cs`), and `tests/Wyl.Fuzz/Mutations.cs`.
  A script that rewrites types but not lambdas needs care: a lambda's typed parameters
  `(int a) -> ...` have names, a function type's parameters don't.
- `tests/matrix/types.snapshot` prints function types, so it changes. Update with `--interpret
  matrix --update-snapshots` and check the diff differs only in how function types are written.
- The spec is already right; check `docs/design/` and CLAUDE.md for the old spelling (none was found
  when this was written) and fix any in the same commit.
- Tests for the new syntax and each error it adds go in `errors/` (parser errors and binder errors
  in separate files, since binding stops at syntax errors) and a passing program that uses each form
  above. The fuzzer's mutations of function types (`Mutations.cs`) must keep producing parseable
  types.

One commit is fine if it passes the pre-commit check; split the parser and the test rewrite only if
reviewing them together is hard.

## After this

The pattern additions (§6.9: `when` guards, struct patterns, `case null`), then `operator []=`
(§6.6). Design sessions queued with the user: threading (statics, what jobs may call, isolates),
the standard library's collections and iteration interfaces, and a proposal to make the cycle
collector easier or automatic. The live list is the roadmap, GitHub issue #6.

## Working notes

- Commit on `dev` after the pre-commit check: the whole suite `--interpret`, plus MSVC filtered to
  the areas touched, one filter per run (`Wyl.Tests.dll lambdas`, then `errors`, ...). A change that
  alters the C++ of every program (std's code) needs MSVC unfiltered, which takes a minute or two;
  say so before running it. Don't push; the user pushes and CI runs every mode. Subjects are
  `area: what changed` (hook-enforced; `parser, binder: ...` for two); comments cite the spec, never
  this file.
- Tests import the std names they use (`import std::{List, print};`); only the language's own types
  are seen without an import (§6.1). Programs a self-test or matrix cell writes get all of std
  through `Wyl.Harness/TestPrograms.WithStd`, on their first line so no line moves.
- A change that shifts lines in the matrix generators changes panic locations in
  `tests/matrix/*.snapshot`; after `--update-snapshots`, check the diff differs only in numbers.
- The Bash tool mangles heredocs with quotes or backslashes in them: write scripts with the file
  tool to the scratchpad and run them. In PowerShell, `git commit -F -` with a here-string doesn't
  work; write the message to a file and pass `-F <file>`.
- `wyl run` writes its build into a `.wyl` directory beside the file; don't run it on files under
  `tests/cases/` (use `wyl interpret`, or the runner, which builds under `tests/.out/`).
- The test runner takes one filter per run, matched against the path with the OS separator.
