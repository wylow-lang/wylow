# The mid-level IR

Status: implemented; the design decisions are in §9. Which layer answers which question, and how
that is enforced, is [architecture.md](architecture.md), which wins where this document differs.

## 1. Summary

Lower each function, after binding, into an IR with:

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

## 3. The IR

### 3.1 Structure

```
IrProgram   = functions (including every lambda, and the stdlib's), indexed by the symbol each
              instruction names, plus the symbol tables, unchanged
IrFunction  = symbol, parameters, location parameter?, captures (for lambdas), body: Block
Block       = statements; a local is scoped to the block that declares it
Statement   = let x = Rvalue            declare and define a temporary or local
            | declare x                 declare a local stored later: a deferred local, a value out of
                                        control flow, a slot, or an `is ref` name's hold
            | store Place = Operand     assign: store the new value, then release the old
            | do Rvalue                 evaluate for effect
            | check Condition @Loc      panic unless (bounds, null, no_match, ...)
            | if Operand Block [else Block]
            | loop L Block [continue Block]  with break L / continue L inside
            | return Operand?
            | end x                     end a hold, a stack slot, or an in-use guard before its scope does
            | discard x                 end a by-value parameter's lifetime without its destructor's body
            | unreachable @Loc
Rvalue      = Operand | unary/binary (checked ones carry @Loc) | call | new | construct | variant
            | array | default T | closure | lambda | choose | ref Place | addr Place
            | data Place | test (null, tag, class, has_value) | convert | hold Place | inuse Place
Operand     = constant | copy Place | move Place | borrow Place | borrow.element Place
              (move: a temporary, or part of one, that isn't used again)
Argument    = Operand | ref Place | in Place | addr Place      (a call's receiver and arguments)
Place       = Base Projection*
Base        = local | temporary | this | *x      (*x: what the object or reference in x refers to)
Projection  = .field | [i] (unchecked; a check precedes it) | .Variant.field | ! (assumed value of a T?)
Loc         = a source location constant | the function's location parameter
```

Locals have a kind that the emitter maps to a C++ declaration: **value** (`T x`), **reference**
(`T& x` for `ref`/`in`, `T* x` for `ref?`/`in?`), **borrowed** (`const T& x`, an alias of a place
that no one writes during its life), **hold** (`wyl::Ref<T>` from `wyl::pin`, or `wyl::Borrow<T>`
from `wyl::borrow`), **slot** (`wyl::StackSlot<T>`), and **in-use** (`wyl::InUseMark<T>`, which
marks a value in use while it lives and restores the state before it).

Some notes on the choices:

- **Dereference only at the base.** `a.b.x = f()` can't be one place `(*(*a).b).x`, since `f`
  could replace `a.b` between finding the target and storing to it. Lowering must read `a.b` into
  a temporary first. That is exactly the spec's rule that the object holding the target is
  evaluated before the value ([Evaluation order](../spec/13-expressions.md#expressions.order)), and the temporary is exactly
  the pin the emitter adds today. So "the root to hold" isn't computed by a walker. It's the base,
  by construction.
- **Narrowing is a check that never runs.** Where the source uses a nullable local as not null, as
  narrowing allows, `check.narrowed x` (with no location) precedes the use: the flow analysis proves
  `x` isn't null there, reporting the user's error if it may be, and the pipeline erases it once the
  last verification has. `check.may_be_null x` is its opposite, before a `case null`'s test of a
  source local: the flow analysis reports the case as one that can never match if `x` is known
  not to be null.
- **Checks are separate from places.** `arr[i]` lowers to `check.index i, 4 @loc` followed by the
  unchecked place `arr[i]`, as MIR does with `Assert`. Evaluating a place then never panics and
  runs no code, which makes places safe to use anywhere. The emitter fuses the check back into
  `wyl::at` when they're adjacent, to keep the output readable.
- **Temporaries are single-assignment**, and the verifier checks it. Values that come out of
  control flow (`?:`, `??`, switch expressions, `&&` whose right side needs statements) are a
  local assigned in each branch, or in the emitter an immediately invoked lambda where the type
  can't be declared first. This is the one place SSA would be cleaner (block arguments, as in
  SIL). It doesn't seem worth SSA's cost for everything else (§9.2).
- **Generic code stays generic.** Types are `WylType`, and analyses substitute with `TypeMap` at
  calls as they do now.
- **Declarations are statements**, not a list at the top of each block, since C++ declares a
  variable where it is initialized: the IR's order is the C++ order, and so is destruction's.
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
| `let %h = hold P` / `hold.borrow P` | `auto tN = wyl::pin(P);` / `auto tN = wyl::borrow(P);` |
| `store P = v` | `P = v` or `wyl::assign(P, v)`, as `StoreText` decides today |
| `check.index i, n @loc` + `a[i]` | `wyl::at(a, i, loc)` when adjacent, else a check call and `a[i]` |
| `loop` whose head is `if !c break` with `c` nestable | `while (c)`, or `for (;; update)` when the update nests |
| `loop` in general | `while (true) { ... }`. A `continue` that must skip to statements in the update block is `goto` to a label after the body's block, which skips no declaration |
| `end %h` | `wyl::unhold(h)` / `slot.end()` |
| `let %g = inuse P` | `wyl::InUseMark gN(P);` |
| `end %g` on an in-use guard | `g.end()`, which restores the state the value had |
| `discard x` | `x.wyl_discard()`, which keeps its fields for the scope to release |

Destruction order inside a statement becomes a C++ scope's reverse declaration order, which is
fully specified, and is the order the spec now requires ([When values are released](../spec/17-memory.md#memory.release)). A
statement's temporaries are declared in the statement's scope; a temporary of an operand that may
not be evaluated is declared in that operand's block, so it is released when the operand completes.
Today a by-value parameter's destruction point is ABI-dependent, which is why `wyl::keep` exists.
In the IR, lowering passes every by-value argument of a copyable type as `copy %t`, where `%t`
belongs to the statement's scope; an argument of a unique type is `move %local`, which the callee
owns. That is the same guarantee, made explicit; `ReadsInPlace` passes `%t` itself where its
release runs no code.

## 4. Where it sits

The layers and the pipeline are in [architecture.md](architecture.md). Lowering is naive: it
borrows every object that may be `Borrowable` where a place goes through what it hands out, and a
mandatory pass (`Borrows`, in the Lowering project) applies the spec's rule for how long a borrow
lasts. Every copy and hold it makes that nothing needs is removed by an optimization
(`ReadsInPlace`, `HoldElision`). The flow analysis is the verifier's dataflow, on the program the
mandatory passes leave: its violations there are the user's errors, so only a program that binds is
lowered and checked, as Rust checks borrows only once a program type-checks.

## 5. How the existing features lower

The listings use the syntax of §3.1. `%` names temporaries.

### 5.1 Evaluation order, and getting readable C++ back

`print(obj.x + f())`: `obj.x` must be read before `f` runs.

```
let %a = copy (*obj).x
let %b = call f()
let %c = add.checked %a, %b @"t.wyl:3:13"
do call print(move %c)
```

The emitter may **nest** a temporary into its single use, printing its rvalue there instead of
declaring it. The rule nesting must keep:

> Every pair of conflicting instructions runs in the same relative order, under every evaluation
> order C++ allows, as in the IR.

C++ fixes the relative order of two instructions only when one is inside the other's operands
(the inner one runs first), when they are in different C++ statements, or when C++ sequences
them: the left side of `&&`, `||`, and `,` before the right, and the condition of `?:` before its
branches. Everything else, such as two arguments of one call, may run in either order. A call's
receiver counts as an ordinary sibling of its arguments: calls go through the declaring class, and
MSVC's C++17 receiver-first ordering has had bugs, so it isn't relied on.

Two instructions **conflict**, by the effect table (§6.1), if:

1. one writes a place class the other reads or writes;
2. both may panic, since which panic is reported, and where, is observable;
3. one may panic and the other may do I/O or run unknown code. Writes to memory don't count: a
   panic aborts without unwinding, so nothing sees them. The table models this as a pseudo-place,
   the world, which panics, I/O, and unknown code all write;
4. both create a value whose release may be observable, since their creation order decides their
   release order ([When values are released](../spec/17-memory.md#memory.release)). At first that's any value that
   `MayOwnObjects`; destructor reachability (§6.1) narrows it later.

Four structural rules go with it:

- A value whose release may be observable is nested only into its statement's last C++
  statement. In an earlier one, C++ would destroy it at that statement's end, before the Wylow
  statement completes. `wyl::keep` around it is still nesting: its temporary lives to the end of
  the full-expression. A copied argument of such a type is such a value too, even when what it
  copies is a move: nesting prints it as the `wyl::keep` temporary, a new owner of the copied
  value. A condition is not such an end: C++ destroys a condition's
  temporaries before the body runs, while the IR keeps them until its block's, so none nests
  there. A source condition's own temporaries are a block of their own, which leaves only a
  lowered `?:` condition, whose temporaries belong to the enclosing statement.
- A temporary that creates a value is a fresh value in the IR, but C++ may print a copy as the
  place it copied, and `?:` of two places is an lvalue. So a copy, or a choice between copies, is
  not nested into a use that takes a place (a `ref` or `in` argument, an address, or a store's
  target), where the use would reach the original instead of the copy, nor into a call's function
  value, which the call must keep alive while it runs though its body may replace that place.
- `store`s and holds are never nested. Holds are named because they live for an extent, and
  stores have no value. So every write inside a nested expression is inside a call, C++ never
  interleaves two calls, and its rule that unsequenced writes are undefined can't apply.
- A temporary is not nested where a local that outlives the C++ statement may refer into it
  (`IrExtent.RefersInto`, the rule the hold analyses use): C++ destroys it at the statement's end,
  but the IR's lives to its block's. A reference, view, or pointer temporary may still nest, since
  what is made from it points where it does; a function value may not, since it is the lambda.

The algorithm is greedy, and starts strictly inside what the rule allows. It walks each
instruction's operands right to left and nests a temporary if everything defined between it and
its use is already nested into that use, so nothing moves past a statement that stays separate, and
if it conflicts with no nested sibling C++ may evaluate before it. If `tests/.out` audits show
temporaries that aren't needed, it can grow to move past separate statements it doesn't conflict
with.

In the listing above, `%c` nests into the `print` call and `%b` into `%c`. `%a` is adjacent once
`%b` is nested, but it's `%b`'s sibling, and `f` may write any object:

```cpp
{ auto tmp_0 = l_obj->f_x; wyl::print(wyl::add(tmp_0, fn_f(), "t.wyl:3:13")); }
```

With field-precise summaries (`Writes`), if `f` writes no `x` of `obj`'s class, `%a` nests too:
`wyl::print(wyl::add(l_obj->f_x, fn_f(), "t.wyl:3:13"))`.

`g(k(), h())`, where `k` and `h` conflict:

```
let %a = call k()
let %b = call h()
do call g(move %a, move %b)
```

Only `%b` nests. Nesting `%a` alone would move `k` after `h`, which adjacency already forbids, and
nesting both lets C++ choose:

```cpp
{ auto tmp_0 = fn_k(); fn_g(tmp_0, fn_h()); }
```

`g(a[i], b[j])`: the two bounds checks are siblings that may both panic, so only the second fuses
into `wyl::at`. If both indices are out of range, every compiler reports the first:

```
check.index i, (length a) @"t.wyl:5:7"
let %x = copy a[i]
check.index j, (length b) @"t.wyl:5:13"
let %y = copy b[j]
do call g(move %x, move %y)
```

```cpp
{ auto tmp_0 = wyl::at(l_a, l_i, "t.wyl:5:7"); fn_g(tmp_0, wyl::at(l_b, l_j, "t.wyl:5:13")); }
```

The important property is that nesting is the emitter's only freedom, and declining to nest is always correct.

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

`list[0] = 5` lowers the same way. The mandatory `Borrows` pass then drops the borrow, because
nothing runs between taking `%r` and storing through it. That reproduces the spec's table of borrow
extents ([Borrows](../spec/17-memory.md#memory.borrows)) from one lowering rule and one pass, instead of from the cases in
`Assignment`, `LocatedAssignment`, and `ArgumentOperand`. A borrow is a language rule, so it is
decided the same with or without optimizations (`classes/borrowable_observed.wyl`).

Every pin the emitter takes today is a hold of a counted value: an object (a receiver, the object
holding a place, or an `is ref` binding's holder), a stored closure (a call of a replaceable
function value, or `IsCopiedForReference`), or a string, whose copy shares its byte block. So
`PinValue`, `Copy`, and the receiver pin are one instruction.

### 5.3 `ref?`, and a reference test that lets go early

`if (bag.get(2) is ref int slot) { ... } else { ... }`, where `bag` is a `mut` local:

```
{
  declare %h                              // the name's hold, declared with it
  declare slot: ref? int
  declare %c
  {                                       // the condition, a statement of its own
    let %h = hold bag                     // the function itself may reassign bag meanwhile
    let %r = call Bag.get(borrow %h, 2)   // ref? int, rooted at %h
    store slot = copy %r
    let %found = test.not_null copy slot
    if !%found { end %h }                 // let go at once where the test fails
    store %c = move %found
  }
  if %c { ... } else { ... }
}
```

The hold outlives the condition's other temporaries, since the name needs it, so it is declared
with the name and made where the test is (a `while` makes it again each time, letting go of the
last). The verifier allows `end %h` while `slot` refers into it only where `slot` is known to be
null: in the branch where its `test.not_null` failed.

A `ref?` is a reference local whose value may be null. `r!` is `check.not_null r @loc` followed
by the place `*r`. A narrowed `r` is just `*r`, and `r?.m()` is an `if`. A conversion from a place
to `ref?` is `addr P`. None of these introduce anything an analysis has to learn about.

### 5.4 Nullable values

A narrowed `v` of type `Vec3?` is the place `v!` (`wyl::assume_value`). `v!` checks first. `v?.m()`
copies `v` into a temporary and branches, as today. The binder's `NullableValueRoot` check of
what a reference or view may refer into stays on the bound tree. The verifier checks the invariant
behind the rest: no reference whose place goes through `v!` is used after an instruction that may write `v`. That is the same check as for
holds (§6.4), and a future, more permissive rule (§9.6) would be stated in exactly those terms.

### 5.5 Spans and views

A view is a value whose root is a place. `data P` gives the pointer to a fixed array's first
element, rooted at `P`'s base, and a span built from it, or returned by a method of a receiver,
has that root. A span variable's hold is a hold local declared just before it. For-each is
already lowered by the binder to a span local and a counting loop, so it needs nothing new:

```
{
  let %h = hold.borrow accounts
  let span = call List.asReadOnlySpan(borrow %h)     // rooted at %h
  let i = 0
  loop L {
    let %n = call ReadOnlySpan.length(in span)
    let %more = lt i, %n
    if %more { } else { break L }
    {
      let %p = call ReadOnlySpan.index(in span, i) @loc
      let e = copy *%p             // InPlaceElements may make this `borrow.element *%p`
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
`check.no_match`. Switch expressions become a local assigned in each case.

`CheckCaseCaptures` stays. A switch expression that chooses a non-escaping function makes the
lambdas it chooses from outside its cases, so they can't use names a case binds. `choose` has the
same structure, so lifting the rule would need case bindings declared in the enclosing block: a
design change of its own, not a side effect of the IR.

### 5.8 Lambdas and closures

Each lambda is an `IrFunction`.

- A **non-escaping** lambda's body refers to its creator's locals as places directly. `let %f =
  lambda λ3` has no effect, and the emitter nests it into its use, so it's still a C++ `[&]`
  lambda passed to a template. A call that receives it, or calls a function value that may be it,
  may run its body. Its writes to captured locals are ordinary `store`s in that body, so what it
  changes comes from the body's effects (§6.1), from where it is made on.
- An **escaping** lambda is `closure λ4 [copy n, copy this] in heap` (or `in slot %s`). Its
  captures are explicit operands, and in the lambda's function they are parameters. That's what
  escape analysis and `CyclicClasses` need to see: a closure holds its captures as an object holds
  its fields.
- A **choice** of non-escaping functions is `choose %index [λ1, λ2, borrow g]`. The index is
  computed by ordinary control flow, and an escaping option is evaluated only on its branch, into
  a held local, as `wyl::choose_held` does. A `!` option's check runs on its branch too, which is
  why its option is a `borrow` of the nullable itself: the emitter passes it as
  `wyl::narrowed`, which unwraps the option the choice selected when it is called. A choice owns
  the lambdas made into it and the copies it takes, and refers to its `borrow` options.
- A **non-escaping local** owns what it is made with
  ([Escaping and non-escaping](../spec/12-functions.md#functions.lambdas.escaping)): a lambda or a
  choice is its value, which the emitter declares `auto` in every mode. Made from an escaping value, the
  local's type is the escaping form, unconverted, so it holds and releases the closure; a value of
  the non-escaping form never owns a closure.

### 5.9 Stack slots and the cycle collector

`new C(args) in slot %s` and `closure ... in slot %s` put the object in slot `%s`, a slot local
of some enclosing block. `end %s` ends it early where a reassignment releases it. Ending an empty
or ended slot does nothing, since an assignment in a loop ends its local's slot each time it runs. The escape
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

A class constructor's IR is the base constructor call (explicit, even when implicit), then the field
initializers, then the body. One that chains (`this(...)`) has the call of its class's other
constructor in the same place (`IrFunction.ConstructorCall`), which C++ prints as a delegating
constructor, and no field initializers. A struct constructor that chains makes `self` from the
constructor it calls instead of the default value. A struct constructor builds a value local `self` (the `this` of a
struct constructor is that local's place) from the struct's default value, which runs the field
initializers, and returns it. The base constructor call is the C++ mem-initializer, where nothing
but expressions can run, so each argument of `super(...)` or `this(...)` is a statement of its own
([Temporaries](../spec/17-memory.md#memory.release.temporaries)): one that needs statements is declared, then
computed by a block of its own, which the emitter prints inside the mem-initializer, nested or as an
immediately invoked lambda. MSVC doesn't always evaluate a braced mem-initializer left to right (it
reversed arguments that leave a destructor to run, in a temporary or a parameter), so when more than
one argument needs statements, the constructor delegates to one of its class that takes them as a
`wyl::Arguments`, which a lambda computes in order. That keeps the arguments until the base
constructor returns and destroys them the last first. The `wyl::Arguments` holds a copy of every
argument, including one passed by `in` or `ref`: a value, or the address of the place, both of
which the delegating constructor receives as it would have directly.

## 6. The analyses on the IR

### 6.1 Facts: one effect table

One function gives each instruction's effects:

- the places it reads and writes, each an `IrAccess`: local `x`; field `f` of objects of class
  `C`; or what a reference or pointer leads to, of type `T`; then the path inside it. An access
  carries the type of its storage, and storage of one type is never another's (spec
  `unsafe.aliasing`), so a store through a reference of type `T` may only touch storage whose
  type may contain a `T` inline, or lie inside one; a type parameter may be anything. "An
  element of type `T`", which a loop borrows in place, is an access too (`IrAccess.Element`), and
  is changed by a write to such an element or to a part of one;
- what it may run: callees (through `Callees`), lambdas (passed, or the
  `FunctionValues` of a called function value), and destructors (the types it releases);
- whether it may panic, and what it allocates;
- whether it writes the world (panics, I/O, and unknown code do), and whether it creates a value
  whose release may be observable, for the nesting rule (§5.1).

A call's reads and writes come from its callees' summaries, computed with the existing
`Summaries<T>` worklist. A summary is the union of its instructions' effects, so it only grows,
and is the same `IrAccess`es with the callee's locals erased and its type parameters the
caller's (`IrAccess.Summarized`): there is one storage model, per function and whole-program.
It is the only place that knows what each instruction does, and there are about twenty instruction kinds
rather than about forty bound node kinds.

Releases being explicit made one imprecision cheap to fix: one destructor could slow down unrelated
loops. The destructors that may run at a release of a `T` are
those of the classes reachable from `T` through strong fields, which is the graph `CyclicClasses`
already builds. A loop that releases no `Group` is not slowed by `~Group`.

### 6.2 Rule: hold elision

> `let %h = hold P` over extent E can be `borrow P` if no instruction in E may write `P` or a prefix of it.

Only the function itself can assign a local that no lambda assigns, `this`, or an immutable
field, and a `mut` field `f` needs no pin if nothing in E may write `f` of that class (`Writes`).
A hold that still borrows stays: which holds borrow is the language's rule, which the mandatory
`Borrows` pass has decided by then, from the same extent (`IrExtent`).

### 6.3 Rule: borrowed locals

> `let x = copy P`, where x is never written or moved, can be `let x = borrow P` if no instruction
> in x's live range may write P or a prefix of it, and P's root is held over that range.

A loop element is the case where `P` is an element of the loop's held span: the writes that
matter are those that overlap an element of `x`'s type (a write into a field of a struct element
counts), and destructors, from the whole-program write summaries (`Writes`). Both rules apply to
every type that borrowing saves something for (`TypeStorage.BorrowSavesCopy`): counted types, and
structs, whose copies a borrow skips. Tests keep their `// in-place:` markers.

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

Check 3 re-checks elisions with the same effect table that made them, so it catches mistakes in
the passes but not in the table: a table that under-reports a write makes a wrong elision and
accepts it. The table is kept honest separately (§9.9).

Failures are compiler bugs, reported with the IR dump (`wyl ir file.wyl`, a new CLI command that
prints the IR, which is also the way to debug lowering).

### 6.5 Escape analysis

Values are allocation sites, and each instruction kind has one flow rule:

- `copy`, `move`, and `borrow` pass values along;
- a `store` to a local declared in the analyzed scope flows into it, and any other `store` escapes;
- a `return` flows to the result;
- a call passes values to its callees' parameter summaries;
- `closure` flows its captures into the closure.

A summary has the escaping parameters, `this`, the returned parameters, and the captures. Tests
mark stack objects with `// stack:`.

### 6.6 Dataflow, and whether to use Datalog

Everything above needs three generic pieces:

- the effect table;
- region walking with a "may write P in extent E" query (an extent is a range of instructions in
  one block's tree, and loops count as whole);
- liveness, a backward pass over structured regions, for live ranges and last uses.

Interprocedural facts use `Summaries<T>`. No general fixpoint engine is needed. Structured
regions are reducible by construction, and every intraprocedural question here is answered by one
walk, treating a loop body as a whole, as the flow analysis does.

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

## 7. Migration from the bound-tree emitter

Done. Its steps are in git history (up to commit 62f975b, which trimmed them from this document).
The migration now under way is [architecture.md](architecture.md)'s.

## 9. Decisions

1. **Structured IR or a CFG?** A CFG (MIR, SIL) is the standard for analyses, but emitting C++
   from one means `goto` everywhere and locals declared at the top with manual destruction. That
   gives up C++ scopes doing the releasing, which is exactly where undefined behavior would creep
   in. MLIR's `scf` dialect and WebAssembly show structured control works as an IR. The analyses
   here don't need arbitrary control flow. **Decided: structured**, with a CFG view derived if an
   analysis ever wants one. A `continue` that jumps to a label is checked by the verifier to skip
   no declaration.
2. **SSA or places with single-assignment temporaries?** SSA (SIL's OSSA) helps scalar
   optimization, which the C++ compiler already does. The questions Wylow asks are about places,
   writes, and extents, as MIR's are. **Decided: places, with ownership explicit on operands**,
   which captures what OSSA gives SIL for reference counting. A value coming out of control flow
   (`?:`, `??`, a switch expression) is its own local kind, assigned exactly once on every path,
   which the verifier checks, rather than an exception to single assignment.
3. **How much the emitter nests temporaries back.** Not at all is simplest, but the C++ would be
   much harder to read and audit. **Decided: nest from the start**, since `tests/.out` audits
   depend on readable output, with `--no-nest` from the start too. The whole suite must pass with
   it, which tests that declining to nest is always correct, and it separates lowering bugs from
   nesting bugs. The rule is §5.1's: conflicting instructions keep their IR order under every
   order C++ allows, with four kinds of conflict, a value whose release may be observable nested
   only into its statement's last C++ statement, no `store` or hold nested, and no reliance on
   C++17's receiver-first ordering. The first algorithm nests only adjacent temporaries.
4. **Flow analysis: stay or move?** First decided as a split, with assignment, returns,
   reachability, constructor fields, and narrowing staying on the bound tree. **Superseded
   (architecture.md):** every flow check moves onto the IR's one flow analysis and `FlowAnalyzer`
   is deleted, since two flow analyses gave two answers to what a lambda may change. Typing doesn't
   depend on flow, because the binder narrows from what the context needs (`AssumesNonNull`) and
   only the check asks whether it was checked.
5. **Bounds checks as separate instructions?** **Decided: yes** (pure places), with the emitter
   fusing them back into `wyl::at`.
6. **Accept more programs once the IR can check them?** For example, allow a `ref` into a `mut`
   nullable local as long as nothing in its extent may write the local. This is a language change,
   for the spec. **Decided: defer** until the flow analysis is on the IR (architecture.md).
7. **Lift the restrictions that exist only because of how C++ is emitted?** **Decided: yes for
   `try`** (only as a statement's whole value, not in a `for` update), since tests using it mid-expression prove the lowering general. **`CheckCaseCaptures`
   stays** (§5.7): it isn't caused by the emitter.
8. **Datalog?** **Decided: not now** (§6.6). Revisit with lifetime analysis.
9. **Keeping the effect table honest.** The verifier can't catch an effect table that under-reports
   a write (§6.4). **Decided:** the table is one visitor over instruction kinds (C# doesn't check a
   `switch` over classes for exhaustiveness), so a new kind can't compile without an entry, and `--no-opt` turns every optimization off (every `copy` stays a copy, every hold a hold, every
   object on the heap), so the suite can compare outputs.
10. **Destruction order inside a statement.** **Decided: specify it now**, before step 3, so that
   step 3 targets the spec rather than whatever today's emitter does ([When values are released](../spec/17-memory.md#memory.release)). Temporaries are released when their statement completes, not at last use, in
   reverse order of creation, after an assignment's old value; a temporary of an operand that may
   not be evaluated is released when that operand completes; each evaluation of a condition or
   `for` update is its own statement. Releasing at last use (as Swift does) would make
   destructor timing depend on the optimizer. Earlier releases are allowed only where nothing can
   tell (no destructor can run, nothing refers to the object weakly), which leaves last-use moves
   for most game data.

11. **Optimizations apart from lowering.** **Decided (step 5):** lowering emits only the program's
   meaning (order, the holds and borrows the spec makes observable, releases, checks) and reads no
   analysis results. Every optimization (§6.2, §6.3, §6.5, and later last-use moves) is a pass of
   its own over the lowered IR (`src/Wyl.Compiler.Optimize`), using facts from `Analysis`, verified
   after it runs, and off under `--no-opt`. What lowering can't decide alone, such as which borrows
   a statement makes, is a mandatory pass, which runs under `--no-opt` too. The IR after the
   mandatory passes is then the reference behavior: the interpreter (`src/Wyl.Compiler.Interpret`)
   executes it as a second, independent executor for the C++ build, and the runner, the fuzzer, and the matrices compare
   the two through one rule (`RunComparison` in `Wyl.Harness`). A disagreement is a bug in one of
   the two, or in the IR both read; the interpreter is not presumed right. The spec states only what makes an
   optimization legal (that it can't be observed), not how it is done.

## 10. Prior art for reference-count optimization

Where to look when writing reference-count rules (§6.2, §6.3, `Writes`). Each of these releases at last
use or doesn't specify release timing, so a rule taken from one must still pass §9.10: it may
release earlier only where nothing can tell.

- **Swift, Ownership SSA.** [SIL Ownership](https://github.com/swiftlang/swift/blob/main/docs/SIL/Ownership.md)
  (owned and guaranteed values, borrow scopes, lifetimes, and the verifier that checks them; the
  closest model to this IR's explicit ownership) and the
  [Ownership Manifesto](https://github.com/swiftlang/swift/blob/main/docs/OwnershipManifesto.md).
  [ARC Optimization](https://github.com/swiftlang/swift/blob/main/docs/SIL/ARCOptimization.md)
  covers RC identity: which values share a count, so that operations on them can be paired.
  [`OSSACanonicalizeOwned.cpp`](https://github.com/swiftlang/swift/blob/main/lib/SILOptimizer/Utils/OSSACanonicalizeOwned.cpp)
  recomputes an owned value's liveness, ignoring its copies and destroys, then places destroys at
  the boundary and drops the copies it no longer needs: last-use moves (§9.10) as one pass.
- **Koka, Perceus.** [Perceus: Garbage Free Reference Counting with Reuse](https://www.microsoft.com/en-us/research/publication/perceus-garbage-free-reference-counting-with-reuse/)
  (PLDI 2021) emits precise `dup`/`drop` instructions and specializes them, and reuses a unique
  object's memory for a new one of the same size. [Reference Counting with Frame Limited Reuse](https://www.microsoft.com/en-us/research/publication/reference-counting-with-frame-limited-reuse/)
  (ICFP 2022) makes reuse robust, bounding how long memory may be kept. Reuse is a candidate for a
  `new` right after the last reference to an object of the same class is released.
- **Lean 4.** [Counting Immutable Beans](https://arxiv.org/abs/1908.05647) (IFL 2019): borrowed
  parameters that skip the count, and a heuristic to infer them, which is the question of passing
  a `Ref` parameter as a reference (§6.3's rule applied to parameters).
- **Nim, ARC/ORC.** [Destructors and Move Semantics](https://nim-lang.org/docs/destructors.html):
  "Cursor inference / copy elision" (a local copied from a place nothing mutates while it lives
  becomes a non-owning cursor, which is §6.3), sink parameter inference, and destructor removal.
  ORC is a cycle collector over ARC, as `memory::collectCycles` is.
- **Lobster.** [Memory management](https://aardappel.github.io/lobster/memory_management.html):
  one owner per allocation inferred at compile time and every other use a borrow, with functions
  specialized by their arguments' ownership; the author reports most count operations removed.
- **Objective-C ARC in LLVM.** [`ObjCARCOpts.cpp`](https://llvm.org/doxygen/ObjCARCOpts_8cpp_source.html):
  a dataflow pass removing redundant and partially redundant retain/release pairs at the LLVM IR
  level, where ownership is no longer visible: what this IR avoids needing by keeping it explicit.
