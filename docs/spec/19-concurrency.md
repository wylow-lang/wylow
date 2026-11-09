<a id="concurrency"></a>
# 19. Concurrency

A program's threads share its code and its statics, and each thread has its own call stack. This
chapter gives what a thread may reach, which values may reach another thread, what a shared class
and a shared function are, how an isolated object graph moves, and how a mutex guards one.

<a id="concurrency.confinement"></a>
## 19.1 Thread confinement

A normal class object belongs to one thread at a time, and no other thread can reach it.

- An object changes threads only inside a unique value that moves: a whole isolated object graph
  ([Isolated object graphs](#concurrency.isolated)), or what a unique value of the standard library
  owns.
- A value reaches another thread only by being captured by a function that thread runs, by being
  read from a static ([Statics](06-variables.md#variables.statics)), or by moving through a
  standard library type that carries a value to another thread, such as a channel, a mutex, or a
  thread's argument and result. Each accepts only sendable values ([Sendable types](#concurrency.sendable)).
  A mutex takes its value as an `Isolated<T>`, which is sendable for any `T`
  ([Mutexes and the lock rule](#concurrency.mutex)).
- Code itself is not confined: a function running on another thread may call any function, create
  and release objects, and use any type. The objects it makes stay on its thread.

<a id="concurrency.sendable"></a>
## 19.2 Sendable types

A type is **sendable** if no part of a value of it belongs to one thread, so the value may be used
on another. Whether a type is sendable follows from its declaration and from its fields, elements,
and type arguments; no declaration states it. A bound asks for it with `sendable`, as in
`Channel<T extends sendable>` ([Bounds](11-generics.md#generics.bounds)).

| Type | Sendable |
|---|---|
| The primitive types | yes |
| A type parameter whose bounds name `sendable` | yes |
| A struct, enum, or fixed array whose fields or elements are all sendable, a unique one included | yes |
| `String` | yes |
| A reference to a `shared class`, including the standard library's synchronization types | yes |
| An escaping `shared fn` ([Shared functions](#concurrency.shared-functions)) | yes |
| `Weak<T>` where `T` is a `shared class` | yes |
| `Isolated<T>`, for any `T` | yes |
| A unique struct of the standard library that owns class objects, when the values it owns are sendable | yes |
| A reference to a normal class, an array, a `List`, or a class of the program | no |
| `Weak<T>` where `T` is a normal class | no |
| A function that is not `shared` | no |
| A non-escaping `shared fn` | no |
| A struct, enum, or fixed array with any field or element that is not sendable | no |
| A type parameter whose bounds do not name `sendable` | no |

Wherever a sendable type is required, using another is a compile-time error, which names the field
that is not sendable.

<a id="concurrency.sendable.moving"></a>
### 19.2.1 Moving and sharing

A sendable value reaches another thread in one of two ways, which follows from whether its type is
copyable ([Copyable types](04-types.md#types.copyable)).

- A **copyable** sendable value is **shared**: its copies may be used by several threads at once.
  Nothing reachable through it can change except through a synchronization type: a copy of a struct
  is a separate value, a `String` never changes, and a shared object's fields are not `mut`.
  Statics, `shared class` fields, and what a `shared fn` captures are read by several threads, so
  their type must be sendable and copyable ([Statics](06-variables.md#variables.statics)).
- A **unique** sendable value is **moved**: it has one owner, so after it moves only the receiving
  thread has it, and its destructor, if it has one, runs on the thread that releases it. A unique
  value may change even when it is only read, as a borrow counter in what it owns does. A channel, a
  mutex, and a thread's argument and result move values, so they accept unique values too.
- Moving a value to another thread panics if anything it owns is borrowed
  ([Borrows](17-memory.md#memory.borrows)): sending it over a channel, storing it in a mutex, or
  giving it to a new thread. A loop or a reference on this thread could still be using that storage,
  as when a loop's body replaces the field it loops over and sends the old value. The check gives
  the same answer in every build mode. Moving a value within a thread needs no check: what the loop
  refers into stays alive, and the new owner cannot change its length while it is borrowed.

<a id="concurrency.shared-classes"></a>
## 19.3 Shared classes

A `shared class` may be referenced from several threads:

```java unchecked
shared class Settings {
    pub String name;
    pub int limit;
    pub virtual int limitFor(int kind) { return limit; }
}
```

- The last reference to one of its objects may be released on any thread, which then runs the
  destructor.
- It is a compile-time error if a field is `mut`, or if a field's type is not sendable and copyable.
- Its base class, if any, and every class extending it are shared too.
- Its objects are never cycle-collected ([Cycles](17-memory.md#memory.cycles)), since other threads
  may refer to them. Their fields cannot change, so a cycle among them can only pass through a
  synchronization type, such as a mutex that holds a reference back. Such a cycle is broken with a
  `Weak<T>`, which is sendable when `T` is a `shared class`; otherwise it leaks.

```java unchecked
shared class Node {
    pub Mutex<Weak<Node>> parent = new Mutex<Weak<Node>>(null);
    pub Mutex<Node?> next = new Mutex<Node?>(null);
}
```

<a id="concurrency.shared-functions"></a>
## 19.4 Shared functions

`shared` on a function type means its lambda may run on another thread. It is part of the type, as
`escaping` is ([Escaping and non-escaping](12-functions.md#functions.lambdas.escaping)), and is
written wherever a function type is: `shared fn(Span<float>) -> void`,
`escaping shared fn() -> R`, `Channel<shared fn() -> void>`.

A function type has four forms: `fn`, `escaping fn`, `shared fn`, and `escaping shared fn`.

| | Escaping `shared fn` | Non-escaping `shared fn` |
|---|---|---|
| Runs | On another thread, possibly after the call that made it returns | On other threads while the function it was given to waits for them |
| Captures | Copies, which must be sendable and copyable | Read-only sendable, copyable locals and parameters, and `in` references and `ReadOnlySpan`s of such data |
| Is itself | A shared object, so its value is sendable | A second-class value, like any non-escaping function |
| Is used for | A thread's body, a task in a queue | A body that a parallel loop splits across threads |

- A `shared` lambda cannot assign what it captures, call a `mut` method on it, or pass it by `ref`,
  even when it is non-escaping: other threads may be reading it. It may capture `this` only in a
  `shared class`'s method.
- Data that a non-escaping shared function may change is passed to it as arguments: the function
  that calls it hands each thread its own part. That function, not the language, keeps the parts
  apart.
- A non-escaping `shared fn` handed to another thread must have finished before the function it was
  given to returns, since what it captures lives in the caller's frame. Only `unsafe` code can hand
  one to a thread, and it takes on that duty.
- A `shared fn` converts to the function type without `shared`; nothing converts the other way
  ([Function conversions](05-conversions.md#conversions.functions)).
- What a shared function returns is isolated when its arguments are sendable. It can reach no normal
  object that existed before the call: what it captures is sendable, so are its arguments, and no
  static refers to a normal object. So every normal object reachable from its result was made during
  the call, and once the call returns nothing but the result refers to them. `Isolated<T>` and a
  thread's result rely on this.

<a id="concurrency.isolated"></a>
## 19.5 Isolated object graphs

`Isolated<T>` holds a `T` and everything reachable from it, its **isolated graph**, such that
nothing outside refers to any normal object in it. It is a unique struct
([Unique structs](07-structs.md#structs.unique)), so it cannot be copied, and it is sendable for any
`T`: moving it moves the whole graph to whoever receives it, and leaves nothing behind that could
reach those objects.

- Making one runs a non-escaping `shared fn() -> T` immediately, on the current thread. Its
  result is isolated. A sendable value is isolated already, and converts to `Isolated<T>` by being
  moved in.
- `open` runs a non-escaping `shared fn(ref T) -> R` on the value, where `R` is sendable, and
  returns its result. The lambda's captures are sendable and copyable, so nothing of the thread's
  can get in, and its result is sendable, so nothing inside can get out; the graph is still
  isolated afterwards. `open` needs a mutable place, since it changes what the box holds. A lambda
  that only changes the graph returns `Unit`: a lambda that returns nothing cannot give `R` a type
  ([Inferring type arguments](11-generics.md#generics.arguments.inference)).
- `take` moves the value out and ends the box: the graph joins the current thread's objects.
- A mutex holds one, and its `lock` opens the graph while the thread holds the mutex's lock
  ([Mutexes and the lock rule](#concurrency.mutex)).

```java unchecked
Isolated<Index> built = Isolated<Index>(() -> Index.build(entries));  // 'entries' is sendable
channel.send(built);                                     // moves the box and the graph

mut Isolated<Index> index = fromLoader.receive()!;
index.open(i -> i.addEntry(7, "alpha"));                 // 'addEntry' returns 'Unit'; still isolated
int count = index.open(i -> i.count());
Index graph = index.take();                               // ends the box; the graph is this thread's now
```

An isolated graph has a cycle buffer of its own. While the lambda of the constructor or of `open`,
or a mutex's `lock`, runs, the thread's buffer is the graph's: that code can reach nothing of the
thread's, so everything it buffers belongs to the graph or was made during the call.
`memory::collectCycles()` inside it collects the graph only, and the buffer moves with the graph.
`take` adds the graph's buffer to the thread's.

Code inside `open` cannot read the thread's own objects. Data built from the thread's objects is
assembled in ordinary code and moved in as a sendable value.

<a id="concurrency.mutex"></a>
## 19.6 Mutexes and the lock rule

`Mutex<T>` ([`Mutex<T>`](a-library.md#library.mutex)) is a shared class that holds an isolated
graph and a lock. Any thread that refers to the mutex may reach the graph, and only while that
thread holds the lock.

- The constructor takes an `Isolated<T>`, so the mutex starts with an isolated graph. A sendable
  value converts to one ([Isolated object graphs](#concurrency.isolated)).
- `lock` takes the lock, runs a non-escaping `shared fn(ref T) -> R` on the value, releases the
  lock, and returns the lambda's result. `R` is sendable, so nothing inside the graph gets out, and
  the lambda's captures are sendable and copyable, so nothing of the thread's gets in. A lambda that
  only changes the value returns `Unit`.
- One thread at a time holds a mutex's lock. A `lock` called while another thread holds the lock
  waits until that thread releases it.
- Everything one thread's lambda did to the graph is visible to the lambda of the next thread that
  takes the lock.
- While a thread holds the lock, the graph's cycle buffer is the thread's
  ([Isolated object graphs](#concurrency.isolated)), so `memory::collectCycles()` inside the lambda
  collects the graph only.
- Releasing the last reference to a mutex releases the graph on that thread. The graph's cycle
  buffer joins the thread's first, and then the value is released.

```java unchecked
var index = new Mutex<Index>(Isolated<Index>(() -> new Index()));
var worker = spawn(() -> index.lock(i -> i.addEntry(7, "alpha")));  // 'addEntry' returns 'Unit'
worker.join();
int count = index.lock(i -> i.count());
```

The **lock rule**: a thread holds at most one lock, and waits for nothing while it holds one.

- Calling `lock` while the thread holds a lock panics, whether the mutex is the same one or
  another.
- While the thread holds a lock, a `receive` panics, a `join` panics, and releasing a `Thread`
  panics, since each of them waits for another thread.
- The checks give the same answer in every build mode.

<a id="concurrency.panics"></a>
## 19.7 Panics on threads

A panic on any thread ends the program ([Panics](18-errors.md#errors.panics)). No thread continues
after another panics, so nothing observes state that a panicking thread left half-changed, and a
lock needs no poisoning.

<a id="concurrency.interleaving"></a>
## 19.8 Interleaving

A program with threads may write its output in a different order from run to run, as threads are
scheduled. That is the only way threads make a program's behavior vary between runs: everything a
thread does by itself is as deterministic as single-threaded code
([Observable behavior](01-introduction.md#introduction.observable)).
