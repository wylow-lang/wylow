# Next: decide what to build now that the spec is written

Load the `rigor` skill first. Repo `E:\Repos\Wylow\wylow`, branch `dev`. Don't implement anything
until the user has chosen. Finish the survey below, then give them the options with one
recommendation. The user thinks unique ownership has to be implemented because it is a critical
language feature, and that `std`'s types depend on it, but is open to other things.

## Where it stands

The spec restructure is done: every chapter and appendix A are written, and decisions 0039 to 0046
settled what the chapters left open. Examples, `std`'s doc comments, links, and the grammar are
tested (`SpecExampleSelfTests`, `DocSelfTests`). `dev` was level with `origin/dev` at `8572347`.

Finish first:

1. CI on `8572347` was in progress, and the nightly fuzz and generate runs haven't been seen on the
   double-literal or `loop` changes. Check them.
2. Fuzz and Generate failed on `dev` at `eef74c4` (2026-11-03), and the cause wasn't read. Generate's
   seed 95227 drew g++'s `-Wshadow` on `compare.h`, since fixed by `4936c7b`; whether that explains
   both failures is unchecked.
3. Issue #6 has this work, but its "Suggested order" still puts the stdlib design first. Update it
   once the user has chosen.
4. Branch `fix/tier2-shadow-warning` (worktree `E:\Repos\Wylow\wt-fix`) holds one commit whose change
   `dev` already has. Ask the user before deleting it.

## Unresolved: a long linked list

`wyl run` on a program that builds a list of 2,000,000 `Node`s and prints `built` printed nothing.
The same run with its output redirected to a file exited 127 with nothing on stderr, also at 50,000
nodes, and the release build did the same. `wyl interpret` printed `built`, then stopped with its
budget exceeded. The cause is unknown, and it may be a tooling problem. A hello program printed
through `wyl run`, piped.

Issue #6 already lists exhausting the stack when releasing a long chain. Check first:

- Run hello and the list the same way, and with `wyl build` and the `.exe` directly, at several sizes.
- If it is the stack, whether `built` being lost is right: the runtime flushes in `core.h`, and an
  abort reports no location (`introduction.aborts`), but what was printed before it should appear.

## What the survey found

**Unique ownership is less work in the back end than it sounds, but it doesn't stand alone.**

- Already there: `IrOwnership.Move`, `Moved` state in `IrVerifier`, the interpreter's `MovedValue`, the
  emitter's `std::move`, and `LastUseMoves`. How much of the use-after-move check this gives wasn't
  read.
- Not there: `unique` appears in the compiler only as a reserved word in `Lexer.cs`. Parsing, binding,
  `copyable`, `discard`, and destructors on structs are all to do.
- `List.first`, `List.last`, and `new Array<T>(n, value)` copy `T`. Generic code is checked once
  (`generics.checking`), so with `copyable` enforced these need another home. The plan in #14 is
  extension methods bounded by `copyable`, and those aren't implemented either.

**A design hole to settle before implementing it** (read in the spec, not tried, since nothing
compiles yet):

- `patterns.references`: a unique field is bound by reference, and moving one out with a pattern is
  a compile-time error. `memory.moves`: only a whole local or by-value parameter moves.
- So nothing in the spec gets a unique value out of a `Result<Handle, E>` but `try`, and
  `case Ok(var value) -> value` in `result.wyl` can't work for a unique `T`. The same goes for a unique
  enum's payload.
- A unique field of an object can't be taken out either: `std::replace` and `std::swap` are named in
  #14, and the spec has neither.
- Compare the whole models with sources, such as Rust's moving match and Swift's `consume` and
  noncopyable types (decision 0009 cites SE-0390 and SE-0377), then recommend one.

**What a program can't do today.** Its only input is its arguments and its only output is `print`. No
file or stdin reading was found in `std`, extern blocks aren't implemented, and a manifest's
`dependencies` and `native` are errors. There is no `Map`, `StringBuilder`, string splitting, threads,
or clock. `std` is about 900 lines.

## What is on the table

Weigh these for the user; the order here is not a recommendation.

- **Unique ownership**: with the design hole above, destructors and `discard`, and `copyable`.
- **Extension methods**: specified, small, and needed by the plan for `copyable`; also the path for
  moving `String`'s methods into Wylow.
- **Interop and I/O**: extern blocks, or at least file and stdin, so real programs can be written.
  Writing real programs is the only test the language hasn't had.
- **Standard library design (#14)**: `Vec<T>` against `List<T>`, iteration, `Map`. Much of it assumes
  unique structs.
- **Statics**: they need `sendable` and `copyable`, so at least those properties come first.
- **Threads** (`shared`, sendable, `Isolated`): depends on unique ownership and statics.
- **`defer`**: rules decided (0040), and independent of the rest.
- **The long-chain release**, depending on what the check above finds.
- **#22**: the compiler hangs on generic code that instantiates itself with growing type arguments.
- **Testing backlog**: 335 untriaged mutation survivors, generator stages 2 to 4 (`docs/design/testing.md`).
- **Performance and structure**: #1 LLIR, #5 C++ leaking into the interpreter and own generics, #10,
  #11, #12. A hello program took about 4 s through `wyl run` and 0.7 s through `wyl check`, one
  measurement each.
- **The user's unsorted syntax ideas**: #3 lambda syntax, #4 `match`, #7 export against visibility,
  #8 `sizeof`, #15 a reminder to explain `@CallerLocation`, `ref struct`, and `@Pure`. Better settled
  before more programs depend on the syntax.

A permanent test comparing the grammar with the parser was offered and not requested: the check of
every valid program against the grammar was a one-off script, and new syntax can drift without it.

## When the user has chosen

Delete this handoff, update issue #6's order, and start on the design points as `docs/design/working.md`
says: what the spec says now, what you propose, and the alternatives.
