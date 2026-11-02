# What operations cost

What operations cost in the current implementation. This is not part of the language: an
optimization may make any of it cheaper, and none of it is promised ([decision 0001](decisions/0001-document-set.md)).

## Checks and ownership

| Operation | Cost |
|---|---|
| Array/span index | 1 compare; none with `getUnchecked(i)` in `unsafe` code |
| Copy a class reference | refcount inc/dec (pass by `ref` to avoid) |
| Move a unique value | a copy of its bytes; no refcount changes |
| Free a pooled object | one compare of its chunk's owning thread; an atomic push if another thread owns it |
| For-each over class elements | refcount inc/dec per element only if the loop may store over one |
| Release a reference to a cyclic class, count stays above 0 | 1 branch; a buffer append the first time |
| Copy a `Weak<T>` | weak block count inc/dec |
| Cycle collection | only where `memory::collectCycles` is called, proportional to objects visited |
| Borrow from class field | refcount inc/dec, often elided |
| Borrow from container | counter inc/dec |
| Pass a lambda to a function parameter | nothing: inlined, like a hand-written loop |
| Pass a lambda to an overridden or interface method, a recursive function passing itself new lambdas, or an overloaded constructor | an indirect call per call of it |
| Choose a function with `?:` | a branch on the choice per call; every lambda it could be is still inlined |
| Use a local that a lambda assigns, as a receiver or argument | refcount inc/dec while in use, if it holds objects |
| Make an escaping lambda | a heap allocation holding copies of what it captures; nothing if it captures nothing |
| Call an escaping function | an indirect call; refcount inc/dec on the closure if it is stored where the call could replace it (a field, a container element, a local a lambda assigns) |
| Copy an escaping function | refcount inc/dec, as a class reference |
| Pass an escaping function to a non-escaping parameter | refcount inc/dec for the call, unless it comes from a local that can't change meanwhile |

## Other operations

- A nullable value `T?`: the `T` and a `bool` (`int?` is an `int` and a `bool`).
- `new T[n]`: two allocations, the object and its elements.
- Generic code: one copy per instantiation, with no indirection.
- Moving a unique value: a copy of its bytes; no reference count changes, since the references it
  holds change owner rather than number.
- Freeing a pooled object: one compare of its chunk's owning thread; an atomic push if another thread owns it.
- Referring to an object weakly: one small allocation the first time; nothing for an object never
  referred to weakly. Copying a `Weak<T>`: a count change on its block.
- Releasing a reference to an object of a cyclic class whose count stays above 0: one branch, and a
  buffer append the first time. Acyclic classes: nothing.
- A hold (pin) of an object: a count increment and decrement, often left out.
- `ref?` over `ref`: nothing.
- Copying a reference to a `shared class` object: an atomic count change.
- Reading a static initialized with a constant expression: nothing; it is constant data. Any other
  static: a check that it is initialized.
- Opening an `Isolated<T>`: two pointer writes to switch the cycle buffer.
- Passing a lambda to a function parameter: nothing; it is inlined like a hand-written loop.
- Making a non-escaping lambda: nothing. Making an escaping lambda: a heap allocation holding copies
  of what it captures, or nothing if it captures nothing.
