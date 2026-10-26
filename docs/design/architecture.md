# Compiler architecture: layers and who answers what

The compiler kept growing two answers to one question: one layer did work that belongs to
another, so the other layer computed the same thing again from what it had, or *guessed*, when
what it needed only existed later. The answers then drifted, and each drift was found a day or two
later as a crash, a miscompile, or a missed check (docs/handoff/simplification-inventory.md).

This document is the contract that prevents that. Each layer owns a set of questions. A question
is answered in exactly one place, and every rule here says **how it is enforced**. A rule that is
enforced only by people remembering it is a rule that will be broken, by any of us.

## The rule

**A layer uses only what exists at that layer. If it needs something only a later layer knows,
the work belongs to that later layer.** Derived facts are computed by the analysis that owns
them, from the current IR. They are never flags that one step sets and another must remember to
correct.

## Layers

| Layer | Input → output | Owns | Never |
|---|---|---|---|
| **Syntax** | text → AST | tokens, grammar, syntax errors | meaning |
| **Semantics** (binder) | AST → bound program | names, types, overloads, conversions; **facts about types** (`VplType`: counted, escaping, defaultable, has default code, may be Borrowable); errors about names, types, and local structure (e.g. "cannot assign to a field of a temporary") | anything about order or flow: assigned, narrowed, moved, changed, alive |
| **IR** (model) | none; it is data | the instruction set, what each instruction does (`IrEffects`, `IrAccess`), printing | analyses, decisions |
| **Lowering** | bound program → IR | translation | asking questions: it **always** holds, **always** copies at an operand's turn, **always** keeps an argument's copy for its statement, and gives every statement's temporaries a block |
| **Analysis** | IR → facts | one flow analysis (definite assignment, narrowing, moves, what each reference needs held, what lambdas change), plus the whole-program facts (callees, writes, escapes, cycles); **the user's flow errors**; **the verifier** | changing the IR |
| **Optimize** | IR + facts → IR | rewrites that the facts justify, one decision each | facts of its own: a pass asks the analysis |
| **CodeGen** | IR + facts → C++ | translation, and nesting, which asks the same effects model | decisions about meaning |

The **pipeline** is: lower, then analyze (reporting the user's errors), then for each pass: run it,
analyze again, and verify. The facts a pass is given always describe the IR it is given.

### The verifier is the flow analysis, checking

User errors and IR invariants are one check run at different times. On freshly lowered IR, a
violation is the user's error, reported at the statement's source location. After any pass, a
violation is a compiler bug. There is no second storage model and no second dataflow: the verifier,
the passes, and nesting all compare storage with `IrAccess`, which the verifier makes precise by
resolving references through what it knows they refer to.

## Enforcement

| Rule | Mechanism |
|---|---|
| Layers depend only downward (Syntax ← Semantics ← IR ← Lowering, Analysis ← Optimize ← CodeGen) | **One project per layer** with project references in that direction only, so reaching across is a compile error (`src/Vpl.Compiler.<Layer>`). |
| The binder answers nothing about flow | Semantics can't reference the IR project, and FlowAnalyzer is deleted: the flow errors are the verifier's (`IrVerifier.Violation`), on the lowered program. Where the source relies on narrowing, lowering says so with `check.narrowed`, which every access of a source local goes through (`IrLowering.Accessed`). |
| Lowering asks nothing | Lowering's project references Semantics and the IR model only, with no analysis types, and asks nothing about what may change: it holds every object a place goes through, copies every operand at its turn (`IrLowering.Read`), keeps every by-value argument (`Kept`), and gives every statement a block. The passes undo what nothing needs. |
| Facts can't be stale | The pipeline (`Optimize/Pipeline.cs`) is the only code that runs passes, which are internal to its project. A pass gets its facts from the pipeline, and they are computed for the IR's current `Generation`; the pipeline bumps the generation after each pass, and a stale facts object throws when asked (`Callees.Fresh`). Facts live in the analysis's result, **not on IR nodes**: what lambdas change is `LambdaWrites`, found with `Writes`, and the IR model only names the question (`IIrLambdaChanges`). `IrLocal.IsElement` is the one settable fact left on a node (step 7), and nothing checks it for staleness. |
| What the source says can't be forgotten | Every statement has a **required** `Origin` (`IrStatement.Origin`, null only for what the source doesn't write), and so do `IrNew` and `IrClosure`, so building one without it doesn't compile. A loop element's declaration is on its symbol (`LocalSymbol.LoopElement`). These replaced side tables that some code paths filled and others didn't, which lost a user error once. |
| A local's kind follows from its value | `IrLet.Defines` decides a `let`'s local kind and reference kind from its rvalue. `IrLet`'s constructors either make the temporary by it or throw for a local that disagrees, and the verifier checks every `let` against it after each pass, which may change both. |
| Every instruction is described | Already: visitors over every statement and rvalue kind. The flow analysis's transfer function is one too, so a new instruction can't compile without a rule. |
| Each type fact has one definition | On `VplType`, used everywhere. This is **enforced only by review**: C# can't forbid a private predicate that recomputes the same thing. Searching for an existing answer stays a CLAUDE.md rule. |

Anything only review can enforce is listed as such, so it's known to be weaker.

## What naive lowering cost (step 5, 2026-10-26)

With lowering naive and the passes below, the optimized C++ of every test has as many
`vpl::pin`, `vpl::borrow`, and `vpl::StackSlot` as before, and fewer `vpl::keep` (162, from 167):

- **Holds** are HoldElision's. An alias of a whole local is renamed to the local.
- **Copies at an operand's turn, and kept copies,** are `ReadsInPlace`'s: a temporary copy of a
  place is replaced by the place where nothing may change it while the copy matters, which for a
  kept copy that may own objects is until its block ends. It runs first, so that HoldElision's
  `Flatten` then finds the statement blocks that release nothing observable.
- **More kept copies are right now:** `l_back = pass(l_back)` keeps the argument, since the store
  replaces it within the statement; `IsStable` had made its release depend on the C++ ABI.
- **One is lost:** a store through a reference, which `IrAccess` takes as unknown storage, keeps
  a copy of a local of an unrelated type (`panics/list_ref_borrowed.vpl`). Telling them apart
  needs the type of the storage behind a reference.

What only worked because of the shortcuts' shapes: the runtime's `vpl::argument` returned its
first argument's type (base constructor calls with arguments of different types), and the
verifier rooted a copy of a view in the local it was copied from, which a statement's block then
ended too early.

## Migration

Each step deletes the old home of what it moves, and passes all four modes before the next.

1. **Projects and the pipeline** (done). Remove the three crossings, split into projects, and give
   the pipeline its generation check. Behavior is unchanged: the generated C++ and IR of every
   test were identical before and after.
2. **Type facts on `VplType`** (done): inventory findings 2, 4 (predicate half), and 8. This fixed
   the `Weak`-in-enum compile failure, and LastUseMoves now moves strings. Widening InPlaceElements
   and BorrowedLocals past class types is still open: their rule doesn't see stores into a struct's
   fields.
3. **Required origins and the local-kind rule** (done): the two failures naive lowering exposed.
   `IrProgram.Origins`, `Creations`, and `LoopElements` are gone, and so is the verifier's copy of
   the kind rule. The generated C++ and IR of every test were identical before and after.
4. **One flow analysis** (done). The verifier's dataflow is the analysis: its
   violations on the lowered program are the user's errors, so only a program that binds is
   lowered, and error tests keep binder and flow errors in separate files. FlowAnalyzer is deleted.
   Lambda changes are `LambdaWrites`, a fact found per generation (findings 1 and 10). The analysis
   narrows through `?:` as the spec says, which FlowAnalyzer didn't. There is one storage model,
   `IrAccess`, with paths (finding 5): the verifier passes it what its references are known to
   refer to, and asks `Writes.CodeMayWrite` with the lambdas made so far. What `*x` reaches is
   `IrLocal.Deref`. Unknown storage now may be a frame local for the verifier too, and a write
   above the last nullable a place lies in destroys it (IrSelfTests
   `ref_argument_replaces_inner_nullable_in_use`). The generated C++ and IR were identical.
5. **Naive lowering** (done): every lowering shortcut is deleted (finding 3, `capturedByLambda`,
   and `MayBeBorrowable`'s copy, now `BoundProgram.MayBeBorrowable`: finding 6), and
   `ReadsInPlace` owns the copies. StackSlots needed no change. A by-value argument is now
   released at its statement's end even where the call replaces its place
   (`classes/kept_arguments.vpl`).
6. **`CyclicClasses` onto the IR** (done): it finds closures and their captures from the lowered
   program, and `Callees` finds it and `Overrides` per generation, so `WholeProgram` and `Analyses`
   are gone and the emitter gets its facts from the pipeline's last `Callees`. Erasing reference
   nullability has one definition, `VplType.WithoutReferenceNullability` (finding 9). The generated
   C++ and IR were identical but for the order of instantiation traits.
7. **Local roles and the remaining walkers** (findings 6, 7, 11).
