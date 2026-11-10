# Iterators, step 1

Implement the first step of decision 0075. The step needs no extension methods. Read the decision
record `docs/design/decisions/0075-iterators.md`, then the spec sections it names:
`statements.for-each.iterators`, `statements.for-each.indices`, `library.iterator`,
`memory.borrows.borrowable`, and `unsafe.blocks`.

## The work

1. **`std::Iterator<T>`.** Declare the interface in a new file of `src/Wyl.Compiler.Semantics/Stdlib/`,
   with doc comments, and give it a symbol in `StdlibSymbols` if the binder needs to recognize it.
   The interface is not in the prelude: a program imports it.
2. **For-each over an iterator.** The binder's for-each is in `Binder.ForEach.cs`. A source whose
   type implements `Iterator<T>` loops by calling `moveNext` and then `current`. The loop takes the
   iterator by value: a unique iterator moves into the loop, and the loop releases it at the end.
   Teach the flow analysis the move, so that a use of a moved iterator variable after the loop is
   an error. The index of a two-variable loop counts iterations. Add catalog errors for a `ref` or
   `in` loop variable over an iterator, for a type that implements `Iterator` with two type
   arguments, and for a type that both implements `Iterator` and converts to a span. Each error
   gets a code, a spec anchor, a test, and a page or a line in `docs/diagnostics/without-pages.txt`.
3. **`beginBorrow` and `endBorrow` in unsafe code.** `Binder.Calls.cs` reports `WYL0419` for a
   direct call. Allow the call inside unsafe code, and update the message and `docs/diagnostics/WYL0419.md`.
4. **A safe unique struct that holds a borrow.** The struct holds a `Borrowable` class object,
   calls `beginBorrow` when it is made, and calls `endBorrow` in its destructor. Its name is open;
   propose one to the user.
5. **`List.iterator()` and `Array.iterator()`**, each with `where T extends copyable`, returning a
   unique struct iterator that holds the borrow and a position. Document in the doc comments what
   `current` does before the first `moveNext` and after the last one (a panic is the expected
   answer), and that changing the length while the iterator exists panics.

## Tests

Cover a loop over a list's iterator, `break` and `return` releasing the iterator and ending the
borrow, an `add` to the list during the loop panicking, a program's own struct iterator and class
iterator, a generic function taking `I extends Iterator<T>`, an iterator over nullable elements
(`List<Order?>`), the index, and every new error. Add rows to the interaction matrices where the
for-each source kinds are listed, and a fuzzer mutation if one can cross the new rules. Update
`docs/design/architecture.md` if a rule gains a mechanism, and issue #6 when the step is done.

## Then: element iterators (decision 0076)

Read `docs/design/decisions/0076-element-iterators.md` and `functions.ref-returns`. This work is
separate from the steps above and may follow them in the same session or the next.

1. **Returned references reached through the receiver.** The binder accepts `return ref` of a place
   in an object that the receiver refers to, and of a place that such an object's method returned.
   `WYL0241` then reports only places in locals and parameters. The caller must receive what the
   reference holds or borrows, so the lowering of a call that returns such a reference also returns
   the object to hold or the container to borrow, and the caller releases it where a reference
   variable's hold would end. Decide the representation with the IR in mind (`docs/design/ir.md`),
   and describe any new instruction in `IrEffects` and `IrAccess`.
2. **`ref` and `in` returns from virtual, abstract, and interface methods.** Retire `WYL0245`: decision
   0003 keeps a retired code in the catalog, marked retired, and its page says the compiler no
   longer emits it.
3. **`RefIterator<T>` and `InIterator<T>`** in `std`, and for-each binding `ref`, `in`, or a copy
   from them. Add catalog errors for `ref` over an `InIterator`, and extend the error for a type that
   implements more than one iterator interface.

Test a tree iterator written in a test file that changes values in place, a node unlinked from its
tree while a loop variable refers into it (the node stays alive), replacing a unique element that a
loop variable refers to (a panic), a map-like class whose keys come through an `InIterator`, and a
virtual method that returns a reference.

## After this step

Step 2 needs extension methods: the adapters (`filter`, `map`, `take`, `skip`, `zip`) and the
terminal operations on iterators and spans, as the roadmap lists them.
