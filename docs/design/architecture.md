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
violation is a compiler bug. There is no second storage model and no second dataflow.

## Enforcement

| Rule | Mechanism |
|---|---|
| Layers depend only downward (Syntax ← Semantics ← IR ← Lowering, Analysis ← Optimize ← CodeGen) | **One project per layer** with project references in that direction only, so reaching across is a compile error (`src/Vpl.Compiler.<Layer>`). |
| The binder answers nothing about flow | Semantics can't reference the IR project. FlowAnalyzer is deleted once its checks are on the IR. |
| Lowering asks nothing | Lowering's project references Semantics and the IR model only, with no analysis types. Its shortcuts (`IsStable`, `capturedByLambda`, lowering's `MayChange`, `Settle`) are deleted, so nothing remains to call. |
| Facts can't be stale | The pipeline (`Optimize/Pipeline.cs`) is the only code that runs passes, which are internal to its project. A pass gets its facts from the pipeline, and they are computed for the IR's current `Generation`; the pipeline bumps the generation after each pass, and a stale facts object throws when asked (`Callees.Fresh`). Facts live in the analysis's result, **not on IR nodes**: no settable `IsChangedByLambda`, `IsElement`, `ChangesAround` on `IrLocal`/`IrFunction`. Those three remain until step 4, and nothing checks them for staleness. |
| What the source says can't be forgotten | Every statement has a **required** `Origin` (`IrStatement.Origin`, null only for what the source doesn't write), and so do `IrNew` and `IrClosure`, so building one without it doesn't compile. A loop element's declaration is on its symbol (`LocalSymbol.LoopElement`). These replaced side tables that some code paths filled and others didn't: naive lowering lost a user error because `Settle` inserted statements that bypassed `Add`, which recorded origins. |
| A local's kind follows from its value | `IrLet.Defines` decides a `let`'s local kind and reference kind from its rvalue. `IrLet`'s constructors either make the temporary by it or throw for a local that disagrees, and the verifier checks every `let` against it after each pass, which may change both. (Naive lowering broke because `Settle`'s `Temp` bypassed `Let`'s rule.) |
| Every instruction is described | Already: visitors over every statement and rvalue kind. The flow analysis's transfer function is one too, so a new instruction can't compile without a rule. |
| Each type fact has one definition | On `VplType`, used everywhere. This is **enforced only by review**: C# can't forbid a private predicate that recomputes the same thing. Searching for an existing answer stays a CLAUDE.md rule. |

Anything only review can enforce is listed as such, so it's known to be weaker.

## What naive lowering costs (measured 2026-10-26)

Lowering was made naive in an experiment, then reverted. It always held, always copied at an
operand's turn, always kept argument copies, and always gave statements blocks. Then the optimized
C++ was compared with today's:

- **Holds are fully recovered:** `vpl::pin` and `vpl::borrow` counts were identical (28, 60).
  HoldElision already owns them, so lowering's hold shortcuts in `ObjectLocal`, `Receiver` and
  `Invocation` just duplicate it.
- **Statement blocks are mostly recovered** by HoldElision's `Flatten`. What remains follows from
  the next two points.
- **Kept copies are not recovered:** `vpl::keep` went from 162 to 999. A pass must own that
  decision, from the analysis's facts: drop a kept copy where the release order can't be told
  apart. Lowering's `IsStable` guess for it is also what caused the release-order bug.
- **Reads in place are not recovered:** a copy at an operand's turn
  (`Ref t = a; f(std::move(t))`) stays, since nesting can't fold an owning copy. A pass must turn
  it back into a read in its use where nothing in between may change the place.
- **StackSlots lost an object** (`cycles/stack_unbuffered.vpl`): its pattern matching expects the
  shapes the shortcuts made.

Naive lowering also failed nine tests, showing code that worked only because of the shortcuts' shapes:

- `Settle`'s copies of `ref?` values had the wrong local kind (they bypass `Let`'s rule). Fixed in step 3.
- References that must last for a local's scope came from temporaries inside a statement block,
  which ended too early.
- A user error was lost: `Settle`'s statements have no origin. Fixed in step 3.
- The emitter's `vpl::Arguments` path for base-constructor calls fails to compile for a struct
  argument. It had never been reached, since `IsStable` kept those arguments direct.
- The user's nullable-value error depends on the IR's shape. That is acceptable only because
  lowering will be naive, and so fixed by the language rather than by heuristics.

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
4. **One flow analysis.** The verifier's dataflow becomes the analysis. FlowAnalyzer's checks move
   onto it one at a time (assignment, narrowing, returns, constructor fields, assigned once), and
   FlowAnalyzer is deleted. Lambda changes become a fact of the analysis, which removes most of
   705e2fc (finding 1, and findings 5 and 10).
5. **Naive lowering:**
   - Add the kept-copy and read-in-place passes.
   - Fix the scope and emitter failures above.
   - Make StackSlots match the naive shapes.
   - Delete every lowering shortcut (finding 3, `capturedByLambda`, `MayBeBorrowable`'s copy).
6. **`CyclicClasses` onto the IR;** `WholeProgram` and `Analyses` go (finding 9).
7. **Local roles and the remaining walkers** (findings 6, 7, 11).
