# Objects on the stack

How the compiler puts objects made by `new`, and closures, in a stack frame instead of on the heap.
The language only allows it because no program can tell
([Observable behavior](../spec/01-introduction.md#introduction.observable)). This
is an optimization pass over the lowered IR (`src/Wyl.Compiler.Optimize/StackSlots.cs`), off under
`--no-opt`, using the escape summaries in `src/Wyl.Compiler.Analysis/Escapes.cs` (ir.md §6.5,
§9.11).

## Why no program can tell

An object is destroyed when its last reference is released. A slot ends where that release would
have happened, so the destructors run at the same point and in the same order. Panics abort without
unwinding, so there is no other way out of a scope. Everything below exists to keep that true.

## Escaping

An object **escapes**, and stays on the heap, if a reference to it may exist after its slot ends:
if it is stored in a field, an element, or a static; returned; captured by a closure that escapes;
referred to by a `Weak` (whose block would point into the stack); or passed (as an argument or as
`this`) to a function that may do any of these with it. That last needs a summary of each lowered
function: which of its parameters, and whether its `this`, may escape or be returned, solved over
the whole program with `Summaries`, counting every override of a dispatched method (`Callees`). A
function that isn't lowered, such as an `extern` one, lets everything it is given escape. An object
storing a reference to itself (`this.self = this`) escapes.

The flow follows values through locals only: a `store` to a whole local flows into it, and any other
store makes the value escape. So a value read from a field or an element is never one of the
tracked values, and neither is what a reference into one, or a hold of what is in one, refers to:
whatever is in a field escaped when it was stored there, and a reference can't be stored. A
function that returns its argument (or its `this`) hands the object back as the call's value,
which escapes only if the caller lets it: `print(pass(new Path(a, b)).length())` can keep the object
on the stack. What a lambda returns escapes, since whatever calls the lambda may keep it.
Destructors aren't summarized: one that keeps a reference to its object panics, on the stack as on
the heap. A `Borrowable` object escapes if its class's `beginBorrow` or `endBorrow`, its own or
inherited, may let its `this` escape, since they run around every borrow.

## Where a slot goes

- **A local's object.** A `new` stored in a local (a source local or a value coming out of control
  flow) declared earlier in the same block, by that block's statement or by a block or branch inside
  it, which runs at most once each time the block does. Its slot is declared right before the local,
  so it ends right after the local is released. A `new` in a loop nested in the block would need two
  objects at once, so it stays on the heap; one in the loop's own body has a slot there instead.
  Locals are released in reverse order, so the object's last reference is released with the first
  local of the block holding it to the end, one it isn't moved out of; nothing declared between that
  local and the slot may run code when released.
- **Reassigned locals.** Each `new` gets its own slot. A store to the local releases the object it
  held, so it ends every slot of the local that may have been filled before it (an `end` of an empty
  or ended slot does nothing), except the one it fills. The object's other references must be gone
  by then: a local it is copied to must not be declared in a statement that stores the local, nor for
  the rest of the block while a store follows, and the store must not give the local the object back
  (`x = pass(x)`). A local that a lambda assigns, that a loop's update stores, or that is passed or
  referred to by `ref`, keeps its objects on the heap, since no slot can be ended there.
- **A temporary's object.** Its slot is declared right before the temporary, so it ends where the
  temporary is released: when its statement completes, or, in an operand that may not be evaluated
  (a branch of `?:`, the right side of `&&`), when that operand completes. A `new` whose value nothing
  keeps (`_ = new Successor(before)`) gets a block of its own around the statement.
- **Not in `super(...)` arguments.** C++ computes them before the constructor's frame exists, in
  code of their own, so a slot there would end before the base constructor runs.

## Closures

An escaping lambda that captures something makes a closure, which is on the stack in the same way
when it provably doesn't escape: an `escaping` local that is only called, or a closure passed to an
`escaping` parameter that the callee never stores. A closure holds what it captures as an object
holds its fields, so a captured object escapes only if the closure does, or if the lambda's body,
whenever it is called, may let it escape or return it, which each lambda's summary records.

## The runtime

- `wyl::StackSlot<T>` holds the object. A closure's slot is a
  `wyl::StackSlot<wyl::Closure<Name, C...>>`, declared with the types of what the lambda captures: the
  closure's type doesn't depend on the lambda's code, which `wyl::closure_in` gives the function value
  where the lambda is.
- **The reference count still works.** The slot holds the reference `new` starts the object with, so
  passing the object to functions that hold and release it never brings the count to 0 early. At the
  end of the slot's scope its destructors run, but nothing is freed. A debug build checks that the
  count is then back to 1, which a correct analysis guarantees.
- **The cycle collector never sees a stack object.** Buffering one would leave the collector's buffer
  pointing into a frame that is gone. It can't be part of an unreachable cycle anyway, since nothing
  outside its scope refers to it. The slot clears the object's cyclic flag after its constructor runs,
  and takes it out of the buffer if the constructor's releases put it there.
- **Size is capped** at 512 bytes, checked by C++, which knows the size: a slot for a larger object
  puts it on the heap but otherwise behaves the same.

This covers the object itself, not storage it holds: a local `List` can be on the stack, but its
elements stay on the heap, since the list can grow. Avoiding that is a separate technique (inline
storage for a few elements, or arenas).

## Tests

Tests mark each line with `// stack: <n>`, the number of objects made there that are on the stack;
the runner skips the markers under `--no-opt`. A test that observes the optimization itself, through
`memory::cycleStats()`, is `// mode: optimized` (`cycles/stack_unbuffered.wyl`).

## Not done

Performance warnings: a `new` that escapes only through one call, or that is just over the size limit.
