# A mid-level IR: design proposal

The task for this session is a **design proposal**, not an implementation. Read the code, form a
view, and write the proposal up for discussion; decisions get made together afterwards. Don't
settle questions that are really the user's to decide: lay out the options and recommend one.

## The problem

Every check, analysis, and emitter helper walks the bound tree (`BoundTree.cs`) directly. That
tree is close to the source: it has many node kinds, and much of their meaning is implicit. A
`BoundConversion` to `ref? T` takes an address, a `BoundNonNull` of a `ref?` is a place, a call
returning `ref` is a place that borrows its receiver, and so on. So every new language feature has
to teach every walker what its nodes mean. Adding `ref? T` meant touching `StorageRoot`,
`ClassifyPlace`, `IsThisRooted`, `NullableValueRoot`, `HasEffects` and its siblings,
`StackObjects` (both the flow and `Replacements`), `ElementAliasing`, `FunctionValues`, and the
`FlowAnalyzer`, each with one more case. Missing a case is a bug, often a memory-safety one, and
two such bugs turned up while adding it.

The optimizations feel the same way. Pin omission, `ElementAliasing` (loop elements that skip
their reference count), and `StackObjects` (objects on the stack) are each a hand-written walker
with its own idea of places, writes, and lifetimes. More are planned: escape and lifetime
analysis, avoiding reference counting for locals copied out of containers, and others. Writing
each one as another walker over the bound tree won't scale.

The user would like optimizations to read more like rules over a few primitive operations, and
less like "add a case for node kind X".

## The idea

Lower the bound tree, after binding and flow analysis, into a mid-level IR with a small set of
explicit operations, in the spirit of Rust's MIR or Swift's SIL. SIL is especially relevant: Swift
is reference counted, and its reference-count optimizations work on explicit retain/release
instructions. Things that are implicit today would become explicit operations: places and reading
and writing them, taking references, holding and borrowing what a reference points into, reference
count increments and decrements, null tests, evaluation order, and control flow.

The analyses and optimizations would then run on the IR, and the C++ emitter would translate it
fairly directly, without making safety decisions of its own. A new surface feature would only need
lowering.

## What the proposal should cover

Roughly, and in whatever shape fits once you have read the code:

- What the IR's operations and structure are, and why: tree or flat, SSA or not, how places are
  represented, how it maps to C++.
- Where it sits in the pipeline (the Architecture section of `CLAUDE.md` describes the current
  one): what stays on the bound tree (type checking surely; maybe flow analysis) and what moves.
- How the existing features lower, at least the ones with subtle runtime behavior: `ref`/`ref?`
  and their holds and borrows, spans, nullable values, lambdas and closures, `try`, patterns,
  left-to-right evaluation order (today `CppEmitter.Ordered` does this in the emitter), the cycle
  collector's buffering, stack slots, and `@CallerLocation`.
- How today's analyses would look on it, and whether they become instances of a few general
  rules or dataflow passes. Consider whether a more declarative form (e.g. Datalog-style rules over
  facts extracted from the IR) is worth it, or overkill for now.
- What it costs, and a migration order that keeps the test suite passing at each step.

## Context

- The language is about five days old and has moved very fast, so large refactors are fine and
  expected. Prefer the design that makes future work easy over one that minimizes churn.
- The spec in `docs/spec/` is the source of truth for the language; the IR is an implementation
  matter and needn't appear there, except where it changes observable behavior.
- `CLAUDE.md`'s rules say what the emitter must guarantee today: no undefined behavior, left-to-right
  evaluation, places and the storage behind them kept alive and unmoved. The IR should make those
  easier to guarantee, not harder.
