# Next: design how a unique value gets out, then start the ownership work

Load the `rigor` skill first. Repo `E:\Repos\Wylow\wylow`, branch `dev`. This is a design session:
implement nothing until the user has agreed the rules. The user chose ownership first, in the order
in issue #6's "Suggested order".

## Finish first

Fuzz (run 41767967578) and Generate (run 41768003881) on `8445182` were still running when this was
written. They are the first to cover the double-literal and `loop` changes. Read them with
`gh run view <id>`, download findings with `gh run download <id>`, and triage each into a test or a
deleted line. A stored finding can be reduced past the bug: if it passes, rebuild the program from
its header and run it at the failing commit before calling it fixed. CI on `f571d20` was also
running.

The nightly runs on `main` only, so `dev` is fuzzed when `main` is fast-forwarded or a run is
dispatched with `--ref dev`.

## Decided in the last session

- `print` keeps the C library's buffering, and an abort may lose output when stdout isn't an
  interactive device (0047). The user rejected flushing on every print because of its cost.
- A long chain of objects is released by a destructor that loops, and the runtime adds nothing
  (0048).
- Where other languages agree on ordinary behavior, the user wants that behavior, not something
  Wylow invents.

## The design session

Issue #6, "Getting a unique value out", lists the gaps and a proposal marked "Proposed, not
decided". The user accepted the order of the work, not that proposal. Nothing in it is compiled,
since `unique` is still a reserved word. Reread `patterns.references`, `memory.moves`, `flow.moves`,
`types.copyable`, `enums.unique`, `expressions.try`, and `functions.extensions` before relying on it.

Take the points in this order, each as what the spec says now, what you propose, and the alternatives:

1. **`T y = x` in generic code with a class `T`.** An unbounded `T` isn't copyable, so this reads as
   a move, but a class is copyable, and moving or copying changes when destructors run. Generic code
   is checked once and instantiated per type, so the answer must not depend on the instantiation
   without the spec saying so. Other points depend on this one. 56 of the 351 test programs use
   generics.
2. **What a by-value binding does**, and which matched values it may move from: `case Ok(var value)`
   on a temporary, a whole local, or a whole by-value parameter.
3. **Whole-value or per-field moves.** The proposal tracks the whole value. Still open: when the
   fields a case doesn't bind are released, which is observable.
4. **Guards**: the binding is a reference until the case is chosen.
5. **A struct with a destructor**: no field moved out. Check this against `discard`.
6. **Consuming `this`.** A non-`mut` method's `this` is read-only, so `Result`'s `orElse`, `map`,
   `mapErr`, and `orPanic` have no way to consume it. The proposal is extensions with a by-value
   `this T`, whose text covers only a copy or a class reference today.
7. **Opening a `T?` of a unique `T`.** `List.removeLast` returns one, and no rule was found.
8. **Matching a unique value in an object.** `patterns.references` copies it into a local first,
   which can't work.
9. **Whether `try` moves a local operand**, on both its paths.
10. **`std::replace`, `std::swap`, and a `take` for `T?`**: their signatures, how they interact with
    a value in use (0028), and that `std` implements them in its unsafe floor.
11. **Unique enums and destructors.** `enums.unique` says they have none, and issue #6's Ownership
    list plans them. Correct whichever is wrong.
12. **`Comparable.compareTo(T other)`** takes `other` by value (issue #6, follow-ups of 0036 to 0038).
13. **Issue #4**, "match like rust instead of switch". It is one line, so ask what the user wants
    before designing around it. It changes the same rules as points 2 to 4.

Try each rule against a lambda that captures the matched local, a loop, a panic in a case, a
destructor that runs during the switch, and an optimization that removes a copy, as `rigor` says.

## Sources

Issue #6 links SE-0432, Rust's binding modes, and C++'s `*std::move(opt)`. The session also used
[SE-0390](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0390-noncopyable-structs-and-enums.md),
[SE-0429](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0429-partial-consumption.md),
[SE-0437](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0437-noncopyable-stdlib-primitives.md),
[Rust's destructors](https://doc.rust-lang.org/reference/destructors.html),
[E0509](https://doc.rust-lang.org/error_codes/E0509.html),
[`mem::replace`](https://doc.rust-lang.org/std/mem/fn.replace.html), and
[`Option`](https://doc.rust-lang.org/std/option/enum.Option.html).
These were read through a tool that summarizes pages, so check the wording against the page before
quoting it in a decision record.

## After the design

Rules go in the spec, reasons in a decision record, and the order in issue #6, in the same session.
Then the work is extension methods, `copyable` in generic code, `unique` and moves, and destructors
and `discard`, as issue #6 lists. `IrOwnership.Move` is documented for temporaries, the verifier's
"used after it is moved" is an internal check rather than a catalog error, and `copyable` and
`sendable` appear nowhere in `src`, so the back end has less of this than its names suggest.
`gh issue edit` may need the user's permission, so have the text ready to hand over as a command.

Delete this handoff when the design is written down.
