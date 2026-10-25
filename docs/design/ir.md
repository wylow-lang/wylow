# A mid-level IR: design proposal

Status: proposal for discussion. Nothing here is decided; §9 lists the decisions to make together,
each with a recommendation.

## 1. Summary

Lower each function, after binding and flow analysis, into an IR with:

- **Structured control flow** (blocks, `if`, `loop` with labeled `break`/`continue`), not a CFG
  of basic blocks, because the target is C++ and its scopes are what release references.
- **Flat, ordered instructions** over single-assignment temporaries, so evaluation order is the
  order of the instructions, and C++'s unspecified operand order can't matter.
- **Places as a base plus projections**, where the only step that leaves the current storage is
  a dereference at the base. A place's storage root is then just its base, and so is the object a
  reference into it must hold.
- **Explicit ownership on every read**: `copy` (+1), `move`, or `borrow` (+0), and explicit
  **holds**: locals that pin or borrow the root of a reference or view for an explicit extent.
- **Explicit everything else that is implicit today**: checks that panic, with their source
  location operand; slots for stack objects; closure captures; the hidden `@CallerLocation`
  parameter.

Not SSA. Not monomorphized (C++ templates still do that).

The payoff, concretely:

- The dozen place walkers become one place representation. The eight effect
  predicates behind `Ordered` go away; the emitter only needs a small per-instruction effect table
  to decide when it may nest a temporary back into an expression, and the safe default is not to.
- Pin omission, `ElementAliasing`, and the planned "locals copied out of containers" are one
  rule: *a copy of a place can be a borrow if nothing in the borrow's extent may write the place*.
  Escape analysis gets one flow rule per instruction kind instead of one per bound node kind.
- An **IR verifier** checks the memory-safety invariants mechanically on every test: every use
  of a reference or view lies inside a hold of its root, or the root is provably stable. Missing a
  case stops being a silent memory-safety bug and becomes a verifier failure in the suite.
- A new surface feature needs only lowering. Its analyses and its C++ come from the instructions
  it lowers to.

## 2. What the code does today, and why it doesn't scale

Reading the compiler, the implicit meaning of bound nodes is re-derived in three families of
walkers.

**Places and roots.** `StorageRoot`, `PinRoot`, `ScopeRoot`, `IsHeldByCaller`, `IsPlace`,
`IsFieldPath` (emitter), `ClassifyPlace`, `IsThisRooted`, `IsThisDerived`, `ViewsTemporary`
(binder), `NullableValueRoot`, `ViewedNullableValue` (`BoundTree`), `IsRootedAtThis`
(`ElementAliasing`), and `Roots` (`StackObjects`) each walk the same chain: field of a struct,
element of a fixed array, a `ref`-returning call's receiver, a conversion to `ref?`, a `!` of a
`ref?`, `BoundArrayData` inside a span construction. Each walker stops at a slightly different
set of node kinds. That's why adding `ref?` meant adding a case to each of them.

**Effects and order.** `HasEffects`, `Writes`, `RunsLambdas`, `ReadsState`, `ReadsHeap`,
`ReadsLambdaChanged`, `WritesHeap`, and `MayPanic` describe every node kind so that `Ordered` can
decide when C++ might reorder operands. Their output is an immediately invoked lambda, which
can't return a place, so `Index`, `OrderedReferenceCall`, `vpl::with`, and `LocatedAssignment`
each find another way around that.

**Lifetimes and holds.** `Operand`'s `PinRoot`, `PinValue`, `Keep`, `Copy`, and `TakesAddress`
flags; `IsStable`, `IsFixedField`, `MayBeReplaced`, `MayOwnObjects`, and `IsCopiedForReference`;
`holders` for `is ref`; `HoistTry`; `DeclareSlots`, `EndSlots`, and `EndReplaced`. These are all
decisions about how long something is held and whether a copy is needed, and the emitter makes
them while it prints.

The analyses walk the bound tree with their own notion of each of these: `StackObjects.Flow` has
its own `Value` rules for 20-odd node kinds, and `ElementAliasing.Collector` its own idea of a
write. `WholeProgram.Unit` exists because a constructor's code is spread across its body, the
field initializers, and an implicit base constructor call.

## 3. The IR

### 3.1 Structure

```
IrProgram   = functions (including every lambda, and the stdlib's), plus the symbol tables, unchanged
IrFunction  = symbol, parameters, location parameter?, captures (for lambdas), body: Block
Block       = declared locals, in order; statements
Statement   = let x = Rvalue            define a temporary or local
            | store Place = Operand     assign: store the new value, then release the old
            | do Rvalue                 evaluate for effect
            | check Condition @Loc      panic unless (bounds, null, no_match, ...)
            | if Operand Block [else Block]
            | loop L Block [continue Block]  with break L / continue L inside
            | return Operand?
            | end x                     end a hold or a stack slot before its scope does
            | unreachable @Loc
Rvalue      = Operand | unary/binary (checked ones carry @Loc) | call | new | construct | variant
            | array | default T | closure | lambda | choose | ref Place | addr Place
            | data Place | test (null, tag, class, has_value) | convert | hold Place
Operand     = constant | copy Place | move Place | borrow Place
              (move: a temporary, or part of one, that isn't used again)
Argument    = Operand | ref Place | in Place | addr Place      (a call's receiver and arguments)
Place       = Base Projection*
Base        = local | temporary | this | *x      (*x: what the object or reference in x refers to)
Projection  = .field | [i] (unchecked; a check precedes it) | .Variant.field | ! (assumed value of a T?)
Loc         = a source location constant | the function's location parameter
```

Locals have a kind that the emitter maps to a C++ declaration: **value** (`T x`), **reference**
(`T& x` for `ref`/`in`, `T* x` for `ref?`/`in?`), **borrowed** (`const T& x`, an alias of a place
that no one writes during its life), **hold** (`vpl::Ref<T>` from `vpl::pin`, or `vpl::Borrow<T>`
from `vpl::borrow`), and **slot** (`vpl::StackSlot<T>`).

Some notes on the choices:

- **Dereference only at the base.** `a.b.x = f()` can't be one place `(*(*a).b).x`, since `f`
  could replace `a.b` between finding the target and storing to it. Lowering must read `a.b` into
  a temporary first. That is exactly the spec's rule that the object holding the target is
  evaluated before the value (§3.4, Evaluation order and the heap), and the temporary is exactly
  the pin the emitter adds today. So "the root to hold" isn't computed by a walker. It's the base,
  by construction.
- **Checks are separate from places.** `arr[i]` lowers to `check.index i, 4 @loc` followed by the
  unchecked place `arr[i]`, as MIR does with `Assert`. Evaluating a place then never panics and
  runs no code, which makes places safe to use anywhere. The emitter fuses the check back into
  `vpl::at` when they're adjacent, to keep the output readable.
- **Temporaries are single-assignment**, and the verifier checks it. Values that come out of
  control flow (`?:`, `??`, switch expressions, `&&` whose right side needs statements) are a
  local assigned in each branch, or in the emitter an immediately invoked lambda where the type
  can't be declared first. This is the one place SSA would be cleaner (block arguments, as in
  SIL). It doesn't seem worth SSA's cost for everything else (§9.2).
- **Generic code stays generic.** Types are `VplType`, and analyses substitute with `TypeMap` at
  calls as they do now.
- **IR nodes are mutable classes with reference identity**, not records. Passes rewrite in place
  (a `copy` becomes a `borrow`; a hold is removed), and side tables key on nodes. Bound nodes stay
  immutable records.

### 3.2 Mapping to C++

| IR | C++ |
|---|---|
| `Block` | `{ ... }`, left out when it declares nothing whose destruction matters |
| `let %t = rvalue` (value) | `auto tN = ...;`, or nested into its single use (§5.1) |
| `let %r = ref P` / `addr P` | `auto& tN = P;` / `T* tN = &P;` |
| borrowed local | `const T& x = P;` |
| `let %h = hold P` / `hold.borrow P` | `auto tN = vpl::pin(P);` / `auto tN = vpl::borrow(P);` |
| `store P = v` | `P = v` or `vpl::assign(P, v)`, as `StoreText` decides today |
| `check.index i, n @loc` + `a[i]` | `vpl::at(a, i, loc)` when adjacent, else a check call and `a[i]` |
| `loop` whose head is `if !c break` with `c` nestable | `while (c)`, or `for (;; update)` when the update nests |
| `loop` in general | `while (true) { ... }`. A `continue` that must skip to statements in the update block is `goto` to a label after the body's block, which skips no declaration |
| `end %h` | `vpl::unhold(h)` / `slot.end()` |

Destruction order inside a statement becomes a C++ scope's reverse declaration order, which is
fully specified. Today a by-value parameter's destruction point is ABI-dependent, which is why
`vpl::keep` exists. In the IR, an argument that may own objects is passed as `copy %t`, where
`%t` belongs to the statement's scope. That's the same guarantee, made explicit.

## 4. Where it sits

```
Lexer → Parser → Binder → FlowAnalyzer → Lower → (verify) → IR passes → (verify) → CppEmitter
                     ↑ checks use the shared PlacePath (§7, step 1)
```

**Stays on the bound tree:** binding, types, overloads, every check that reports an error
(`ClassifyPlace`, the reference, span, and nullable-value checks, `MatchSpace`), and
`FlowAnalyzer`. Errors need source terms. Several checks run during binding and steer it (a
conversion is chosen only if the place qualifies). `FlowAnalyzer`'s narrowing is already encoded
in the tree (`AssumesNonNull`), which lowering simply reads. The place-shaped checks share one
decomposition with lowering, so a new kind of place is taught once (§7, step 1).

**Moves to lowering:** every decision the emitter makes today about order, temporaries, holds,
copies, `try`, and pattern matching.

**Moves onto the IR:** `ElementAliasing`, `StackObjects`, the pin-omission decision, the
whole-program write and escape summaries, and the call graph `FindRecursive` walks.

**Stays as it is:** `CyclicClasses` (type-level; it reads closures' captures, which are now
explicit), `Overrides`, `FunctionValues`, the type and declaration half of the emitter
(`CppEmitter.Types.cs`, `CppEmitter.Cycles.cs`, signatures, templates, memberwise forms), and the
runtime.

`WholeProgram.Unit` disappears: lowering puts field initializers and the implicit base
constructor call into the constructor's IR, so a unit is a function.

## 5. How the existing features lower

The listings use the syntax of §3.1. `%` names temporaries.

### 5.1 Evaluation order, and getting readable C++ back

`print(obj.x + f())`: `obj.x` must be read before `f` runs.

```
let %a = copy (*obj).x
let %b = call f()
let %c = add.checked %a, %b @"t.vpl:3:13"
do call print(move %c)
```

The emitter may **nest** a temporary into its single use if moving its evaluation there can't
change anything. Concretely, nothing evaluated between its definition and its use, or as a sibling
operand C++ might order first, conflicts with it, by the per-instruction effect table (§6.1). Here
`%a` can't be nested, because `f` may write any object. `%b` and `%c` can:

```cpp
{ auto tmp_0 = l_obj->f_x; vpl::print(vpl::add(tmp_0, fn_f(), "t.vpl:3:13")); }
```

That replaces `Ordered`'s lambda with a scope. The important property is that nesting is the
emitter's only freedom, and declining to nest is always correct.

### 5.2 Places, holds, and borrows

`a.b.x = f()`, where `b` is a `mut` field:

```
let %h = hold (*a).b        // the object holding the target, read before the value
let %v = call f()
store (*%h).x = move %v
```

If `b` isn't `mut` and `a` is stable, hold elision (§6.2) makes `%h` a `borrow (*a).b`. The C++ then
is `l_a->f_b->f_x = fn_f();`, as it is today.

`list[i()] = f()`, on a `Borrowable` list:

```
let %h = hold.borrow list                       // pin + beginBorrow; endBorrow when %h ends
let %i = call i()
let %r = call List.index(borrow %h, %i) @loc    // ref, rooted at %h
let %v = call f()
store *%r = move %v
```

`list[0] = 5` lowers the same way. Hold elision then removes the borrow, because nothing runs
between taking `%r` and storing through it. That reproduces the spec's table of borrow extents
(§3.4, Borrow counters) from one lowering rule and one elision rule, instead of from the cases in
`Assignment`, `LocatedAssignment`, and `ArgumentOperand`.

Every pin the emitter takes today is a hold of a counted value: an object (a receiver, the object
holding a place, or an `is ref` binding's holder), a stored closure (a call of a replaceable
function value, or `IsCopiedForReference`), or a string, whose copy shares its byte block. So
`PinValue`, `Copy`, and the receiver pin are one instruction.

### 5.3 `ref?`, and a reference test that lets go early

`if (bag.get(2) is ref int slot) { ... } else { ... }`, where `bag` is a `mut` local:

```
{
  let %h = hold bag                       // the function itself may reassign bag meanwhile
  let slot = call Bag.get(borrow %h, 2)   // ref? int, rooted at %h
  let %found = test.not_null slot
  if %found { ... } else { end %h; ... }
}
```

A `ref?` is a reference local whose value may be null. `r!` is `check.not_null r @loc` followed
by the place `*r`. A narrowed `r` is just `*r`, and `r?.m()` is an `if`. A conversion from a place
to `ref?` is `addr P`. None of these introduce anything an analysis has to learn about.

### 5.4 Nullable values

A narrowed `v` of type `Vec3?` is the place `v!` (`vpl::assume_value`). `v!` checks first. `v?.m()`
copies `v` into a temporary and branches, as today. The binder's `NullableValueRoot` check stays
on the bound tree. The verifier also checks the invariant behind it: no reference whose place
goes through `v!` is used after an instruction that may write `v`. That is the same check as for
holds (§6.4), and a future, more permissive rule (§9.6) would be stated in exactly those terms.

### 5.5 Spans and views

A view is a value whose root is a place. `data P` gives the pointer to a fixed array's first
element, rooted at `P`'s base, and a span built from it, or returned by a method of a receiver,
has that root. A span variable's hold is a hold local declared just before it. For-each is
already lowered by the binder to a span local and a counting loop, so it needs nothing new:

```
{
  let %h = hold.borrow enemies
  let span = call List.asReadOnlySpan(borrow %h)     // rooted at %h
  let i = 0
  loop L {
    let %n = call ReadOnlySpan.length(in span)
    let %more = lt i, %n
    if %more { } else { break L }
    {
      let %p = call ReadOnlySpan.index(in span, i) @loc
      let e = copy *%p             // the borrow rule (§6.3) may make this `borrow *%p`
      ...
    }
  } continue {
    let %next = add.checked i, 1 @loc
    store i = move %next
  }
}
```

### 5.6 `try`

`int x = try parse(s);`

```
let %r = call parse(s)
let %failed = test.tag %r, Err
if %failed {
  let %e = variant Result.Err(move %r.Err.0)
  return move %e
}
let x = move %r.Ok.0
```

`HoistTry`'s reason to exist, that C++ can't return from inside an expression, goes away. With
it go the binder's restrictions that exist only because of it: `try` only as a statement's whole
value, and not in a `for` update (§9.7).

### 5.7 Patterns and switches

A pattern becomes tests, projections (`.Variant.field`, `downcast`, `!`), and binding copies, in
order. `Is`'s rule that every bound value is copied before any is stored is visible as the
instruction order. A switch evaluates its value once, as a borrow of a stable place or else a
temporary held for the switch (what `MatchedValue` does), and becomes an `if` chain ending in
`check.no_match`. Switch expressions become a local assigned in each case. `CheckCaseCaptures`
exists because a switch expression's cases run in a C++ lambda, so it could be lifted too (§9.7).

### 5.8 Lambdas and closures

Each lambda is an `IrFunction`.

- A **non-escaping** lambda's body refers to its creator's locals as places directly. `let %f =
  lambda λ3` has no effect, and the emitter nests it into its use, so it's still a C++ `[&]`
  lambda passed to a template. A call that receives it, or calls a function value that may be it,
  may run its body. Its writes to captured locals are ordinary `store`s in that body, which
  replaces `FindLambdaChanged` and `RunsLambdas` with the callee's summary (§6.1).
- An **escaping** lambda is `closure λ4 [copy n, copy this] in heap` (or `in slot %s`). Its
  captures are explicit operands, and in the lambda's function they are parameters. That's what
  `StackObjects` and `CyclicClasses` need to see: a closure holds its captures as an object holds
  its fields.
- A **choice** of non-escaping functions is `choose %index [λ1, λ2, borrow g]`. The index is
  computed by ordinary control flow, and an escaping option is evaluated only on its branch, into
  a held local, as `vpl::choose_held` does.

### 5.9 Stack slots and the cycle collector

`new C(args) in slot %s` and `closure ... in slot %s` put the object in slot `%s`, a slot local
of some enclosing block. `end %s` ends it early where a reassignment releases it. The escape
analysis (§6.5) picks the slot and its block. Today's constraints (one slot per `new`, so the
`new` must run at most once per run of the slot's block) become a structural fact: the `new` isn't
inside a `loop` nested in the slot's block.

The collector's per-class and per-closure cyclic flags stay type-level (`CyclicClasses`). `new`
and `closure` carry the flag so the emitter just prints it. Releases are now visible (a `store`
over a counted value, a scope end, an `end`), so a later rule could skip `add_to_buffer` at
releases that provably can't leave a cycle. Nothing needs that yet.

### 5.10 `@CallerLocation`

A function with `@CallerLocation` has a location parameter. Every instruction that may panic
carries a `@Loc` operand: a constant, or that parameter. A call to such a function passes its own
`@Loc`. The emitter prints it. `SourceLocation`'s dependence on `currentFunction` goes away, along
with the question of what it means inside a lambda in such a function.

### 5.11 Constructors

A class constructor's IR is the base constructor call (explicit, even when implicit), then the
field initializers, then the body. A struct constructor builds a value local `self` (the `this`
of a struct constructor is that local's place). The one place the emitter still needs expressions
is the C++ mem-initializer list for the base constructor call. Braces keep its arguments in order,
as today, and an argument that needs statements gets an immediately invoked lambda. That works
because super-call arguments are rarely places.

## 6. The analyses on the IR

### 6.1 Facts: one effect table

One function gives each instruction's effects:

- the places it reads and writes, classified as: local `x`; field `f` of objects of class `C`; an
  element of type `T`; anything through a reference or pointer of type `T`; or unknown;
- what it may run: callees (through `WholeProgram.Targets`), lambdas (passed, or the
  `FunctionValues` of a called function value), and destructors (the types it releases);
- whether it may panic, and what it allocates.

A call's reads and writes come from its callees' summaries, computed with the existing
`Summaries<T>` worklist. A summary is the union of its instructions' effects, so it only grows.
This replaces the eight effect predicates and `ElementAliasing`'s `Write` collection. It is the
only place that knows what each instruction does, and there are about twenty instruction kinds
rather than about forty bound node kinds.

Releases being explicit makes one imprecision the spec documents cheap to fix (§3.4, "One
destructor can slow down unrelated loops"). The destructors that may run at a release of a `T` are
those of the classes reachable from `T` through strong fields, which is the graph `CyclicClasses`
already builds. A loop that releases no `Squad` would no longer be slowed by `~Squad`.

### 6.2 Rule: hold elision (today's pin omission)

> `let %h = hold P` over extent E can be `borrow P` if no instruction in E may write `P` or a prefix of it.

A first version uses exactly today's `IsStable`. That rule is sound because only the function
itself can assign a local that no lambda assigns, `this`, or an immutable field. It keeps the
output unchanged. The general version uses the effect table: a `mut` field `f` needs no pin if
nothing in E may write `f` of that class.

> `let %h = hold.borrow P` can be removed if E runs no code.

Here E is from the reference's creation to its last use, which reproduces the "reading an element
by value needs no borrow" and "storing a value that runs no code" rows of the spec's table.

### 6.3 Rule: borrowed locals (today's `ElementAliasing`, generalized)

> `let x = copy P`, where x is never written or moved, can be `let x = borrow P` if no instruction
> in x's live range may write P or a prefix of it, and P's root is held over that range.

A loop element is the case where `P` is an element of the loop's held span: the writes that
matter are writes to elements of `x`'s type, and destructors. That's today's analysis, with the
same whole-program summaries. The general rule also covers "avoiding reference counting for locals
copied out of containers", struct-valued locals (which save a copy of every counted field), and
receivers. Tests keep their `// in-place:` markers.

Swift's semantic ARC optimizations do the same thing on SIL, turning a `load [copy]` into a
`load_borrow` when nothing writes the memory during the value's lifetime. Koka's Perceus and
Lean 4's IR are the precedents for putting reference-count operations explicitly in an IR and
optimizing them with rules.

### 6.4 The verifier

The verifier runs after lowering and after each pass in debug builds (always in the test suite).
It checks:

1. Temporaries are defined once, before use, inside their scope, and an owned one is moved at most once.
2. Places dereference only at the base.
3. **Every use of a reference or view lies inside the extent of a hold of its root, or the hold
   was elided and the elision condition (§6.2, §6.3) still holds.** This re-check is cheap. It
   makes every optimization self-checking, and it is the "places and the storage behind them kept
   alive and unmoved" rule from `CLAUDE.md`, enforced instead of trusted.
4. No reference through `v!` of a `mut` local is used after an instruction that may write `v`.
5. A slot's `new` is in the slot's block, not inside a loop nested in it.

Failures are compiler bugs, reported with the IR dump (`vpl ir file.vpl`, a new CLI command that
prints the IR, which is also the way to debug lowering).

### 6.5 Escape analysis (today's `StackObjects`)

Values are allocation sites, and each instruction kind has one flow rule:

- `copy`, `move`, and `borrow` pass values along;
- a `store` to a local declared in the analyzed scope flows into it, and any other `store` escapes;
- a `return` flows to the result;
- a call passes values to its callees' parameter summaries;
- `closure` flows its captures into the closure, as `StackObjects.Lambda` does.

The summary shape stays the same (escaping parameters, `this`, returned parameters, captures).
What goes away is the per-node `Value` switch and the heuristics about where a local's
declaration is, since blocks and slots are explicit. The `// stack:` markers keep the behavior
pinned while it's ported.

### 6.6 Dataflow, and whether to use Datalog

Everything above needs three generic pieces:

- the effect table;
- region walking with a "may write P in extent E" query (an extent is a range of instructions in
  one block's tree, and loops count as whole);
- liveness, a backward pass over structured regions, for live ranges and last uses.

Interprocedural facts use `Summaries<T>`. No general fixpoint engine is needed. Structured
regions are reducible by construction, and every intraprocedural question here is answered by one
walk, treating a loop body as a whole, as `FlowAnalyzer` already does.

**Datalog-style rules** (facts like `writes(instr, placeClass)`, `calls`, `flows`, and
`inExtent`, with rules over them) would make the rules above very readable. Rust's Polonius project
expressed borrow checking as Datalog rules (via the datafrog engine). For now it's overkill:

- there are four analyses, each a few predicates over the effect table;
- an engine is new infrastructure to write and debug;
- error messages and test markers want the derivations explained, which the C# code gives more
  directly.

**Recommendation:** write the rules as C# predicates over the effect table, and keep the facts in
a form that could feed a small semi-naive evaluator later. Revisit when lifetime analysis lands.
That's the first analysis with genuinely recursive relations (region outlives region), where
Datalog earns its keep.

## 7. Migration order

Each step keeps the suite passing on MSVC and g++.

1. **`PlacePath` on the bound tree.** One function decomposes a bound expression into a base and
   steps, knowing that a conversion to `ref?` or a `!` of a `ref?` is the same place, and that a
   `ref` call is a place rooted at its receiver. Rewrite `ClassifyPlace`, `IsThisRooted`,
   `IsThisDerived`, `ViewsTemporary`, `NullableValueRoot`, `ViewedNullableValue`, `StorageRoot`,
   `IsPlace`, `IsFieldPath`, `IsRootedAtThis`, and `Roots` over it. The output doesn't change.
   This pays off even if the IR never lands, and lowering is built on it.
2. **IR types, printer (`vpl ir`), and verifier.** The pipeline doesn't change yet.
3. **Lowering plus an IR emitter, behind a flag** (`--ir` in the CLI and the test runner). The
   analyses still run on the bound tree, and lowering consumes their results, which are keyed by
   bound nodes and locals (`MadeBy`, `EndedBy`, `AliasesElement`). That way the analyses needn't be
   ported before the switch. Grow coverage in this order, running the suite under `--ir` each time:
   - straight-line code, calls, places, and holds;
   - control flow;
   - patterns and switches;
   - `try`;
   - lambdas, closures, and choices;
   - slots and borrowed loop elements;
   - `@CallerLocation`;
   - constructors and super calls.
4. **Switch the default to the IR**, and delete the emitter's expression and statement half
   (most of `CppEmitter.Expressions/Statements/Ordering/Patterns/Lambdas.cs`, about 1,900 lines
   in all). Signatures, templates, and the declaration half stay.
5. **Port the analyses onto the IR**, one per step, with the markers unchanged: `ElementAliasing`
   as the borrowed-local rule restricted to loop elements, then hold elision with `IsStable`'s
   predicate, then `StackObjects`. Delete `WholeProgram.Unit`'s special parts.
6. **Generalize**, with each step a measurable improvement:
   - field-precise write summaries;
   - borrowed locals beyond loop elements;
   - destructor precision (§6.1);
   - last-use `move`s;
   - lifting the C++-only binder restrictions (§9.7).

The risky part is step 3: matching today's destruction order inside statements, which tests
with destructor output observe. The spec allows the order to vary by statement but requires it to
be the same everywhere. The IR makes it exact, so the spec could then state it.

## 8. Costs

- **Code.** New: the IR model, printer, and verifier (about 900 lines); lowering (about 2,000,
  mostly moved out of the emitter); the IR emitter (about 900); the effect table and region queries
  (about 300). Rewritten: `StackObjects` and `ElementAliasing` (about 850 lines today, likely
  less). Removed: about 1,900 emitter lines and most of `BoundTree`'s walkers. Several sessions of
  work, with the codebase about the same size afterwards.
- **Generated C++.** Fewer immediately invoked lambdas, more named temporaries in scopes. Runtime
  at `-O2` should be the same (the temporaries are locals the optimizer removes). C++ compile time
  is probably slightly better without the lambdas. Debugging the output gets easier. Every
  `tests/.out` file changes once.
- **Compile time of `vpl`.** One more pass over every function, stdlib included. Negligible at
  this size.
- **Ongoing.** Two representations to keep consistent: the bound tree for checks, the IR for
  everything after. `PlacePath` is the shared seam that keeps them from drifting.

## 9. Decisions to make together

1. **Structured IR or a CFG?** A CFG (MIR, SIL) is the standard for analyses, but emitting C++
   from one means `goto` everywhere and locals declared at the top with manual destruction. That
   gives up C++ scopes doing the releasing, which is exactly where undefined behavior would creep
   in. MLIR's `scf` dialect and WebAssembly show structured control works as an IR. The analyses
   here don't need arbitrary control flow. *Recommend structured*, with a CFG view derived if an
   analysis ever wants one.
2. **SSA or places with single-assignment temporaries?** SSA (SIL's OSSA) helps scalar
   optimization, which the C++ compiler already does. The questions VPL asks are about places,
   writes, and extents, as MIR's are. *Recommend places, with ownership explicit on operands*,
   which captures what OSSA gives SIL for reference counting.
3. **How much the emitter nests temporaries back.** Not at all is simplest, but the C++ would be
   much harder to read and audit. Nesting with the effect table keeps the output close to today's.
   *Recommend nesting from the start*, since `tests/.out` audits depend on readable output.
4. **Flow analysis: stay or move?** *Recommend stay* for now. It works, it reports source-level
   errors, and narrowing is already in the tree. Revisit if the nullable-value rule (6) moves to
   the IR, which would make the IR the natural home for flow-sensitive checks, as Rust moved
   borrow checking to MIR.
5. **Bounds checks as separate instructions?** *Recommend yes* (pure places), with the emitter
   fusing them back into `vpl::at`.
6. **Accept more programs once the IR can check them?** For example, allow a `ref` into a `mut`
   nullable local as long as nothing in its extent may write the local. This is a language change,
   for the spec. *Recommend deferring* until after step 5.
7. **Lift the restrictions that exist only because of how C++ is emitted?** `try` only as a
   statement's whole value, `try` in a `for` update, and `CheckCaseCaptures`. Each is a spec change
   that removes code. *Recommend yes*, at step 6.
8. **Datalog?** *Recommend not now* (§6.6).
