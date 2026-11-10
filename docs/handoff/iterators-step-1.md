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

## After this step

Step 2 needs extension methods: the adapters (`filter`, `map`, `take`, `skip`, `zip`) and the
terminal operations on iterators and spans, as the roadmap lists them.
