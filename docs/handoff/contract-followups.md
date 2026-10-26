# Follow-ups to the layer contract

A review after the layer migration (2026-10-26) checked docs/design/architecture.md against the
code. The structure holds: the project graph is strictly downward, passes are internal to
Optimize, facts check their generation, and lowering asks no "may change" questions. The review
found one bug, gaps where a rule isn't enforced yet, and a second storage model. This file plans
the fixes. Delete it when the last step lands, and mark the architecture.md rows it names as
enforced.

## Read this first: how every step is done

These are project rules (CLAUDE.md, Rules), not suggestions. They exist because duplicated
answers kept drifting apart and the drift was found days later.

- **Enforce by structure.** A rule holds only if breaking it breaks the build or the suite: a
  project boundary, an `internal` member, a required constructor argument, a visitor, a type, a
  pipeline check, or a test. Each step below says which mechanism it adds. A rule that only review
  can enforce is listed as such in architecture.md's enforcement table.
- **One answer, in one place.** A step that moves a question deletes the old home in the same
  commit. Don't patch both copies and add a cross-check.
- **Language rules are never optimizations.** What the spec says a program does is decided by
  lowering or a mandatory pass, which uses only what the language defines (`IrEffects`, facts on
  `VplType`). If turning a pass off changes a program's output, it was a language rule.
- **Docs match the code in the same change**, CLAUDE.md, `docs/design/`, and the spec alike.
- **Each step** passes all four modes (MSVC, `--cc gcc`, `--no-opt`, `--no-nest`) and builds with
  no warnings. Where a step should change no behavior, show it: build the previous commit in a git
  worktree, run the tests in both trees, and `diff -r` the two `tests/.out`.

## Step 1: make the enforcement rules true (done)

Warnings are errors, the fact constructors are `internal` to Analysis, and lowering keeps every
by-value argument (`ReadsInPlace` passes a temporary itself where its release runs no code). The
optimized C++ and IR of every test were identical before and after.

## Step 2: mandatory passes, and the borrow rule in one of them (done)

`--no-opt` and the optimized build disagreed on `box[1] = 5` for a `Borrowable` of the program's
own. Lowering now always borrows, and the mandatory `Borrows` pass (Lowering project, a
`MandatoryPass` only lowering can make) applies the spec's table, using `IrExtent`, which
HoldElision shares. `classes/borrowable_observed.vpl` covers each row in every mode. The optimized
C++ and IR of every other test were identical.

## Step 3: one storage model, then wider borrowing

### The problem

Storage is described two ways:

- **`IrAccess`** (per function): a frame local, a field of some object, or `Unknown`, which carries
  no type. So a store through an `int&` may overlap any local, and keeps a copy of a local of an
  unrelated type (`panics/list_ref_borrowed.vpl`, which the migration listed as open).
- **`Writes.StoredType`** (whole program): storage by its type, with an `Element` flag. It doesn't
  treat a store into a struct element's field (`items[i].x = 1`) as changing an element of type
  `S`. That's why InPlaceElements and BorrowedLocals still accept only class types (they test
  `IsReference`), the other open item from the migration.

### The decision this rests on

Spec 01-safety.md (§1.2, Strict aliasing; §1.4, `unsafe`), decided 2026-10-26: storage of one type
is never read or written as another. A write through a reference or pointer of type `T` changes
only storage that holds a `T`, directly or inside a struct, enum, or fixed array. Reinterpretation
is `bitcast<T>`'s job, and unsafe code that breaks the rule is undefined behavior, as in C++. The
C++ compiler that builds the output assumes the same.

### The change

1. **`IrAccess` carries the type of the storage it touches,** behind a reference or pointer too.
   Two accesses can't overlap if neither type can contain the other inline (`VplType.InlineTypes`).
   A type parameter can be anything, so it overlaps everything.
2. **The whole-program summaries are `IrAccess` without the locals.** `WriteSet` keeps the accesses
   a function may write, with locals erased and paths kept. "May store over an element of type `E`"
   becomes an overlap with an element access of type `E`, so a write into a field of an `S`
   element counts. Delete `StoredType` and the separate `unseen` types.
3. **Widen the two passes** that test `IsReference` (InPlaceElements, BorrowedLocals) to every
   type that borrowing saves something for: counted types (`VplType.IsCounted`) and structs, whose
   copies a borrow skips.
4. **Tests:**
   - a loop over struct elements that stores into an element's field must not borrow it in place;
   - a borrowed struct local whose place's field is then stored must stay a copy;
   - a loop over strings is borrowed in place, with its `// in-place:` marker;
   - `panics/list_ref_borrowed.vpl` loses the extra copy;
   - IrSelfTests cases for an unrelated typed write that doesn't overlap, and a type parameter that
     does.

   Update the `// in-place:` counts the widening changes, and check each new borrow by reading the
   generated C++.
5. **Docs.** In architecture.md, mark the "Storage is described one way" row enforced, and remove
   both items from "Still open". In ir.md, §6.1 and §6.3 describe the one model. If nothing else
   remains in this file, delete it.
