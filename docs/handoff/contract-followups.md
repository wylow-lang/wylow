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

## Step 1: make the enforcement rules true

Two CLAUDE.md rules are stated but not yet true of the code. Do this step before anything else.

1. **Warnings are errors.** Add `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` to
   `Directory.Build.props`, then fix every warning in every project, tests and CLI included. One is
   known: `BorrowedLocals` takes a `program` it no longer reads (CS9113). Don't suppress a warning
   without a comment saying why it is wrong there.
2. **Only the pipeline builds facts.** Make the constructors and finders of facts `internal` to
   Analysis: `Callees`'s constructor, `Writes.Find`, `Escapes.Find`, `LambdaWrites.Find`, and
   whatever builds them. Add `InternalsVisibleTo` for `Vpl.Compiler.Optimize` (the pipeline) and the
   test project (`IrSelfTests` builds facts for hand-written IR). The fact types stay public, since
   the emitter reads the ones the pipeline hands it (`Optimized`). Check that CodeGen no longer
   compiles if it calls `new Callees(...)`.
3. **Lowering keeps every by-value argument.** `IrLowering.Kept` (Lowering.Expressions.cs, the
   `!operand.Type.MayOwnObjects` case) skips the kept copy for a type that can't own objects. Delete
   that case, so lowering asks nothing. The optimized output should be unchanged, since
   `ReadsInPlace` removes the copies nothing needs; diff `tests/.out` to show it. If it isn't
   unchanged, teach `ReadsInPlace` the reason rather than restoring the shortcut.
4. Mark the "No leftovers" and "Only the pipeline builds facts" rows of architecture.md enforced,
   and drop step 1's exception from the "Lowering asks nothing" row.

## Step 2: mandatory passes, and the borrow rule in one of them

### The bug

`--no-opt` and the optimized build behave differently on a valid program:

```java
class Box implements Borrowable {
    mut int[2] items;
    void beginBorrow() { print("begin"); }
    void endBorrow() { print("end"); }
    ref int operator [](int index) { return ref items[index]; }
}
...
box[1] = 5;        // optimized: nothing; --no-opt: begin, end
box[1] = five();   // both: begin, end
```

Spec 03-memory-model.md (Borrow counters on containers) says that reading an element by value, or
storing a value that runs no code, needs no borrow. So the optimized build is right, and the IR
meant to be the program's meaning is wrong.

**Cause:** that language rule is split in two. Lowering skips the borrow for a place read right
away (`use != Use.Read`, Lowering.Places.cs, the `PlaceBase.Object` case). HoldElision drops it
where no code runs while it is referred to (HoldElision.cs, `hold.Borrows && !referenced.RunsCode
&& !named`), and `--no-opt` turns HoldElision off. No test caught it, because none uses a
`Borrowable` of the program's own.

### The change

1. **Two kinds of pass, typed apart.** Mandatory passes always run. Optimizations run only without
   `--no-opt`. Make the difference structural, not a list order:
   - **Put the mandatory passes in the Lowering project.** It references Semantics and the IR model
     only, so a mandatory pass can't reach a whole-program analysis: making an analysis more
     precise must never change what a program does. `IrLowering` exposes them in order.
   - **The pipeline runs each one and verifies after it,** as it does for optimizations. The user's
     errors are reported on the IR after the mandatory passes: that IR is the program's meaning,
     the oracle the planned interpreter runs.
   - **Optimizations stay `internal` to Optimize,** behind the `optimize` switch.
2. **One definition of a hold's extent.** The borrow pass and HoldElision both need it: from the
   first reference into what a hold holds to the last use of anything that depends on it, and
   whether that use is in a call. Move `HoldElision.Extent`, `Refers`, and `InUse` into the IR
   model project (e.g. `IrWalk`, or an `IrExtent` type). It uses only `IrWalk` and `IrEffects`. Both
   passes use that one definition, and HoldElision's copies are deleted. The verifier keeps its own
   dependency dataflow, since it is the checker: the one allowed second opinion, and loud when they
   disagree.
3. **The borrow rule is one mandatory pass,** implementing the spec's table exactly. Lowering
   always borrows what may be `Borrowable` (delete `use != Use.Read`). The pass drops a borrow
   where no code runs from the first reference into it to its last use (`IrEffects`'s `RunsCode`)
   and no source variable or loop refers into it, whose borrow lasts for its scope. Delete
   HoldElision's borrow-dropping lines: HoldElision then only turns holds that nothing disturbs into
   aliases.
4. **Tests.** Add `tests/cases/classes/borrowable_observed.vpl`, a `Borrowable` of the test's own
   that prints in `beginBorrow` and `endBorrow`, with one case for each row of the spec's table and
   its expected output:
   - a read by value;
   - a store of a constant, and a store of a call's value;
   - a `ref` variable (borrowed until its scope ends);
   - a `ref` argument (until the statement ends);
   - a struct method's receiver.

   It runs in all four modes, so `--no-opt` and the optimized build can't disagree unnoticed.
   Check every existing test still passes in all modes.
5. **Docs.** In architecture.md, mark the "Language rules aren't optimizations" row enforced, and
   drop step 2's exception from the "Lowering asks nothing" row. In ir.md, §6.2 loses the borrow
   rule, and §9.11 says the oracle is the IR after the mandatory passes. The CLAUDE.md
   architecture line shows the mandatory passes.

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
