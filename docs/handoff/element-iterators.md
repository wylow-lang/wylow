# Element iterators

Implement decision 0076. Read the decision record `docs/design/decisions/0076-element-iterators.md`,
then the spec sections it names: `functions.ref-returns`, `memory.holds`,
`statements.for-each.iterators`, and `library.iterator`. The first step of iterators (decision
0075) is done, so `std::Iterator<T>` and for-each over it already exist.

## The work

1. **Returned references reached through the receiver.** The binder accepts `return ref` of a place
   in an object that the receiver refers to, and of a place that a method of such an object
   returned. `WYL0241` then reports only places in locals and parameters. The caller must receive
   what the reference holds or borrows. The lowering of a call that returns such a reference
   therefore also returns the object to hold or the container to borrow, and the caller releases it
   where the hold of a reference variable would end. Decide the representation with the IR in mind
   (`docs/design/ir.md`), and describe any new instruction in `IrEffects` and `IrAccess`.
2. **`ref` and `in` returns from virtual, abstract, and interface methods.** Retire `WYL0245`.
   Decision 0003 keeps a retired code in the catalog, marked retired, and the code's page says that
   the compiler no longer emits it.
3. **`RefIterator<T>` and `InIterator<T>`** in `std`, next to `Iterator<T>` in
   `src/Wyl.Compiler.Semantics/Stdlib/iterator.wyl`, and for-each binding `ref`, `in`, or a copy from
   them. Add a catalog error for a `ref` loop variable over an `InIterator`. Extend `WYL0484` to a
   type that implements more than one of the three interfaces, and update its page.

## Where the first step left things

- `Binder.BindIteratorLoop` (`Binder.ForEach.cs`) builds the loop: a temporary holds the iterator,
  and a `BoundFor` calls `moveNext` as its condition and `current` in its body. `IteratorTypes`
  finds the `Iterator<T>` types that a type is or implements, and `StdlibSymbols.Iterator` is the
  interface's symbol. `DeclareElement` declares the loop variable with a given `RefKind`. The
  iterator loop passes `RefKind.None` and `loopElement: false` today. A `ref` or `in` loop over an
  element iterator would pass the variable's own `RefKind`.
- `WYL0483` reports a `ref` or `in` loop variable over an `Iterator<T>`. That rule stays for
  `Iterator<T>`, and the new rule for `InIterator<T>` reports only `ref`.
- A method that implements an interface method cannot be `@CallerLocation` (`WYL0434`), because a
  call through a bound passes no location. A `current` that panics therefore reports a location
  inside the file that declares it.
- In generic code, a hold of a value whose type parameter is a struct refers to the struct's place,
  in the runtime (`wyl::Pin`, `wyl::MakeBorrow`) and in the interpreter alike. A returned reference
  that carries a hold has to keep that behavior.
- A call infers type arguments through bounds (decision 0077), so `<I extends RefIterator<T>, T>`
  needs no explicit type arguments.
- `std::Borrow` is a unique struct that holds a `Borrowable` and is not generic, because a type
  parameter bounded by `Borrowable` is not known to be a class. `ListIterator<T>` holds the list and
  a `Borrow` of it.
- The spec example at `docs/spec/12-functions.md:288` fails with `WYL0241`. The example returns a
  reference reached through the receiver, so it passes once item 1 is done. Until then, the whole
  suite reports that one failure.

## Tests

Cover a tree iterator, written in a test file, that changes values in place. Cover a node unlinked
from its tree while a loop variable refers into it: the node stays alive. Cover replacing a unique
element that a loop variable refers to, which panics. Cover a map-like class whose keys come through
an `InIterator`, and a virtual method that returns a reference. Each new error gets a code, a spec
anchor, a test, and a page. Add rows to the values matrix next to the iterator rows of
`MatrixValues.cs`. Add a fuzzer mutation if one can cross the new rules: the `foreach-iterator`
mutation in `tests/Wyl.Fuzz/Mutations.cs` is the model. Update `docs/design/architecture.md` for
the hold that a returned reference carries, and tick the 0076 item in issue #6 when the work is done.

## After this

Step 2 of decision 0075 needs extension methods: the adapters (`filter`, `map`, `take`, `skip`,
`zip`) and the terminal operations on iterators and spans, as the roadmap lists them.
