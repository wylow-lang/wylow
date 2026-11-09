# Runtime and generated code

How the current implementation runs a Wylow program: what the C++ it generates looks like, and what
the runtime (`runtime/wyl/`) does. None of this is part of the language ([spec](../spec/README.md));
changing it changes no program's observable behavior. How the compiler decides which objects go
on the stack is in [stack-objects.md](stack-objects.md).

## Undefined behavior in C++

**Code generated from safe Wylow never contains undefined behavior.** The compiler performs all
safety checking in its own semantic analysis; the generated C++ is never "trusted" to be correct
on its own. Operations that are UB in C++ are emitted through small inline helpers with defined
behavior, which compile down to a compare and a predictable branch (or nothing).

```java
// Wylow
int x = arr[i] + y;
```
```cpp
// generated C++ (illustrative)
int32_t x = wyl::Add(wyl::At(arr, i), y);   // checked index, defined overflow
```

| C++ UB | Wylow behavior |
|---|---|
| Out-of-bounds index | Checked; panics |
| Signed integer overflow | Panics in debug builds, wraps in release builds (defined either way) |
| Division by zero, `INT_MIN / -1` | Checked; panics |
| Shift by ≥ bit width | Shift amount is masked (`x << (n & 31)` for 32-bit) |
| Null dereference | Types are non-null by default; nullable types must be checked. `x!` panics on null |
| Uninitialized variables | Compile error to read before assignment; fields are default-initialized |
| Use-after-free / dangling pointer | Prevented by the memory model ([Memory](../spec/17-memory.md#memory)) |
| Iterator / reference invalidation | Prevented by borrow counters ([Borrows](../spec/17-memory.md#memory.borrows)) |
| Float → int out of range | Saturates; NaN → 0 |
| Unsequenced evaluation | Evaluation order is always left to right; codegen emits temporaries as needed |
| Strict aliasing / type punning | Only via `bitcast<T>(x)` (emitted as `std::bit_cast`/`memcpy`). Storage of one type is never read or written as another, which the compiler relies on ([spec](../spec/20-unsafe.md#unsafe.aliasing)) |
| Data races | Prevented by the concurrency model ([19-concurrency.md](../spec/19-concurrency.md#concurrency)) |
| Stack overflow | OS guard page; process aborts |

## Values

A nullable value type `T?` is a `wyl::Optional<T>`: the value and a `bool`.

Enums compile to a C++ struct containing a tag and a union (not `std::variant`). The tag is the
smallest unsigned integer that numbers the variants, or the `@Repr` type when one is given. When
no variant can hold an object reference, the enum is trivially copyable; otherwise copying and
destroying it copy and destroy only the active variant's fields, and assigning it stores the new
value before releasing the old one, as for structs ([Order of destruction](../spec/17-memory.md#memory.release.destruction)).

A fixed array `T[N]` is a `std::array`.

A `T[]` is an object holding its length and a pointer to its elements, which are allocated
separately (two allocations; the elements are stored inline, not as references to boxes).

Generics are monomorphized: each instantiation is its own C++ template instantiation, so a
generic method can't be virtual (a C++ virtual function can't be a template).

`String`'s methods are declared in `string.wyl` and implemented by the runtime, `wyl::String`.

A `ref?` or `in?` reference is a C++ pointer, null when there is no place.

## Objects

Objects are counted with an intrusive (in-object) count, non-atomic for normal classes and atomic for
`shared class`.

Every class object starts with a 24-byte header (on 64-bit targets): the vtable pointer, a word
holding the non-atomic reference count with the cycle collector's flags in its low bits, and a word
holding the object's weak block (below) once it is referred to weakly, or else its place in the
collector's buffer while it is buffered (an object with both keeps that place in the weak block).
In C++, interfaces derive virtually from one `wyl::Object`, and so does the root of any class
hierarchy in which some class implements an interface, so an object has one count however many
interfaces it implements. Other classes derive from it directly, which saves each object a pointer
and each count change a lookup of the base's offset. References are
`wyl::Ref<T>`, an intrusive smart pointer; non-null is enforced by Wylow, not by `Ref`. The count
starts at 1, owned by the `new` expression, so a constructor that hands out and drops references to
`this` cannot destroy the object early.

An object of a shared class derives from `wyl::SharedObject`, which holds an atomic weak block pointer
and allocates from the global heap. The object's state word holds only the `SHARED` flag, which the
`SharedObject` constructor sets and nothing changes afterward, so any thread may read the word. The
object's other header word holds its atomic count. `Retain` and `Release` are not virtual: each tests
`SHARED` on an unlikely branch, so a reference to an interface implemented by a shared class counts
the object the shared way, and a normal object's count stays non-atomic. The emitter does not call
`SetCyclic` in a shared class's constructor, because a base constructor may already have handed `this`
to another thread. A shared object has no place in the collector's buffer and no collector flags (spec
`memory.cycles`).

## Allocation

Objects and closures up to 512 bytes come from the runtime's **pools** rather than the
C++ global heap; larger ones, and over-aligned ones, use the global heap. A pool is a size class:
slots of one size, in multiples of 16 bytes, carved from 64 KiB chunks aligned to their size.

- **Allocating** takes a slot from the size class's current chunk: its free list, or else its
  uncarved end. When the current chunk is full, the fullest chunk of the class that has room
  becomes current, so that new objects fill few chunks while the others drain. Partial chunks are
  kept in 8 bins by how full they are, which makes the choice constant-time.
- **Freeing** pushes the slot onto its own chunk's free list, found by masking its address, so a
  slot needs no header. The size comes from C++'s sized delete through the virtual destructor.
- **Memory goes back to the OS** only beyond 64 empty chunks per pool. A chunk left with no live slots
  goes on the pool's stack of empty chunks, and any size class reuses a chunk from that stack, so a
  program whose count of objects swings makes no system call to free and allocate them. So a thread may
  hold up to 4 MiB that it isn't using. A chunk with any live object stays mapped: objects never move, so a
  few long-lived objects scattered over many chunks keep all of those chunks. Choosing the fullest
  chunk only reduces this.
- **Pools are per thread, with no synchronization on the usual path.** Most objects are freed on
  the thread that made them. An object can move to another thread inside a unique value
  ([Thread confinement](../spec/19-concurrency.md#concurrency.confinement)) and be freed there: freeing a slot
  of a chunk another thread owns pushes it, atomically, onto that chunk's remote free list. The owner
  takes the list back when the chunk is current and its own free list runs out. Before the owner maps
  a new chunk, it also checks the remote lists of the next four of its other chunks, because a full
  chunk is in no bin and nothing else would find it. The owner keeps its chunks in a linked list and
  remembers where it stopped, so no allocation pauses for longer as the heap grows. This is the separate list for concurrent
  frees that [mimalloc](https://github.com/microsoft/mimalloc) keeps per page ("free list
  multi-sharding"; [technical report](https://www.microsoft.com/en-us/research/publication/mimalloc-free-list-sharding-in-action/)).
  Every free compares the chunk's owner with the current thread. A thread that ends takes back its
  chunks' remote lists, unmaps each chunk with no live object, and marks every other chunk's remote
  list as orphaned. The next thread to free a slot of an orphaned chunk finds the mark in place of a
  list and adopts the chunk. One atomic word holds both the list and the mark, so a free cannot leave
  its slot on the list of a chunk that no longer has an owner.
  `shared class` objects, which may be freed on any thread at any time, use the global heap.
- Under AddressSanitizer every object uses the global heap, so that ASan can still catch a use
  after free.

## Stack allocation

The compiler may put an object made by `new` (or the closure an escaping lambda makes) in the
stack frame of the scope making it instead of on the heap, saving the allocation and the free,
when no reference to it can outlive that scope:

```java
var hit = new RayHit(origin, dir);      // only used here: may be on the stack
if (raycast(world, hit)) { std::print(hit.distance); }
std::print(new Path(a, b).length());    // a temporary: may be on the stack
```

No program can tell the difference, which is what allows it. An object is destroyed when its last
reference is released (see [When values are released](../spec/17-memory.md#memory.release)), and the
compiler does this only where the stack object is destroyed at exactly that point, in the same order
relative to everything else. Panics abort without unwinding, so there is no other way out of the
scope. The only exception is diagnostics: `memory::cycleStats()` counts what the cycle collector
buffered, and it never buffers an object on the stack.

Which objects qualify is the compiler's business and may change between versions; an object
qualifies only if it doesn't **escape**: stored in a field, an element, or a static, returned,
captured by a closure that escapes, referred to by a `Weak`, or given to a function that may do
any of these. How the current compiler decides is in
[docs/design/stack-objects.md](stack-objects.md).

## Weak blocks

The first time an object is referred to weakly, it allocates a small weak
block `{ target, count, buffer slot }` and points its header at it. A `Weak<T>` is a pointer to the block and
one to the target (C++ can't cast the block's `wyl::Object*` back down to `T`), and copying one adds
to the block's count. `Weak<T>` is an `extern struct`
([Extern structs](../spec/21-interop.md#interop.extern.extern-structs)) implemented
by `wyl::Weak` in the runtime. A weak block's count is atomic and the block comes from the global
heap, since a shared object's weak references may live on several threads. A shared object's block is
made with an atomic exchange, so two threads referring to it weakly at once agree on one. The block
of a shared object has a small guard, a spin lock. `get()` reads the target and adds to the count under
the guard. A release that finds itself the last one takes the guard, checks the count again, and clears
the target. If `get()` added to the count first, that release is no longer the last one and only drops
its reference. So `get()` returns a live object or null, and it never revives an object that another
thread is destroying. When an object is destroyed, the block's target is set to null. The block itself
is freed when the target and the last `Weak` to it are gone.

## Cycle collector

Instantiations of generic classes that only C++ sees, through a generic function's type arguments,
are treated as cyclic, and a field whose type could refer to one as reaching every class; neither
affects correctness, only how much is buffered. `cycleStats().visited` counts the objects of the
groups that fit in the budget. Per-class counts and visit timing aren't reported yet.

## Holds

The current rule: the compiler pins (holds a strong reference until the statement completes, as it
would a temporary) the receiver of a method call, and the object owning the place passed as a
`ref`/`in` argument or used as a struct method's receiver, unless that object is `this` or a local
or parameter, or is held by a field that isn't `mut` of such an object. Locals are safe without a pin because nothing but the
function itself can assign them, and a class-typed local cannot even be passed by `ref`. `this` is
safe because whoever called the method has pinned it. A field that isn't `mut` ([Fields](../spec/08-classes.md#classes.fields)) is never
reassigned once its object is constructed, so it holds its object as long as the object holding it
lives; inside its own class's constructors, which may still assign it, it is pinned like a `mut`
field. Nor does it pin an object held by a `mut` field of such an object when nothing that runs
while the pin would last may assign that field: the compiler knows which fields each function may
assign, including through the functions, overrides, lambdas, and destructors it may run, and
through references, pointers, and struct methods' `this` into a value of the type the field
contains.

## In-place loop elements

In `for (Node e : list)`, `e` holds its own reference to each element's object, like any local,
unless nothing the loop can run may store over an element of that type: assign to an element
(`list[i] = x`, `array[i] = x`, or through a `ref` in generic code), or to a value containing a fixed
array of them. Then `e` refers to the element in place, saving a count increment and decrement per
element. What the loop iterates is held and borrowed until it ends, so storing over the element is
the only way its object could be destroyed. The same goes for an element of any type whose copy
costs something, such as a string or a struct, which saves the copy; for a struct, assigning a
field of an element (`list[i].x = 1`) counts as well, as does a write through a `ref` to a type
the struct contains. Storage of one type is never another's ([spec](../spec/20-unsafe.md#unsafe.aliasing)), so a write of an
unrelated type never counts.

The compiler checks everything the loop's body may run: the functions it calls and the functions
they call, every override of a virtual or interface method it calls, every lambda of the type of
a function value it calls (a lambda in generic code counts for every type it could be), and the
destructors of every object it may release. Assigning fields of objects, including fields of
the element's class, never counts, so getters, setters, and most methods don't prevent it. Reassigning `e` itself
does. The program behaves the same either way; only the cost differs.

Releasing a value may destroy the objects it refers to, and through their fields (including
structs, fixed arrays, container elements, and what closures capture), the objects those refer to,
so the destructors that count are those of every class an object reachable that way may have. A
value whose type involves a type parameter, or a class only generic code instantiates, may
destroy anything. So a destructor that stores over an element of a type (`~Squad() { members[0] =
new Node(...); }`) keeps a loop over elements of that type, such as `for (Node e : nodes) {
e.hp -= 1; }`, holding its own reference to each element only if the loop may release a `Squad`,
or something that may hold one; `memory::collectCycles()` may release anything. Destructors that
only assign fields, or store elements of other types, never affect it.

## Moves

The compiler also moves copyable values where no program can tell, at the last use of a local
(`LastUseMoves`); for a unique value, the move is what the program means.

## Function values

A function-typed parameter compiles to a C++ template parameter, so each lambda passed to it is
inlined into the function. Where C++ can't use a template, it is instead a non-owning reference
to the lambda, called indirectly: in an overridden virtual method (one that nothing overrides is
an ordinary method), an abstract or interface method, a class's implementation of an interface
method, a recursive function that passes itself a new lambda (or a choice) on some cycle of calls,
which would otherwise instantiate itself without end, a constructor that shares its parameter
count with another of its type, or a function type's own parameters. A recursive function that
only passes its function parameters on, or escaping values, is still a template:
`walk(child, visit)` instantiates the same one again. Overloads that differ only in function types
are told apart by the C++ name. A function chosen by `?:` or a switch expression holds every
function it could be and the index of the chosen one, so calling it branches to a direct call,
and each lambda can still be inlined.

An escaping function is a `wyl::Function`, two words: a pointer to the code that runs the lambda's
body, and a reference to a closure object, with the same header and reference count as a class
object, that holds copies of what its lambda captured. A lambda that captures nothing has no
closure, so making one allocates nothing, and copying it only tests for null; a function reference
captures nothing. Calling a function value is one indirect call. Copying it copies the reference. A
nullable function needs no flag: its null is a null code pointer. The cycle collector
visits the class references a closure holds. A call of a function value stored where the call
could replace it (a field, a list element, a local that a lambda assigns) holds a reference to the
closure until the call returns. A closure that provably can't outlive the scope making it may be
on the stack, as a class object can be ([Stack allocation](#stack-allocation)).

A nullable non-escaping parameter is a `wyl::Optional` of a `wyl::FunctionRef`, made where the call
is, so that what it refers to (a lambda, or a copy of a stored function) lives until the call
returns. A `wyl::FunctionRef` made from a stored function refers to its closure and calls its code,
so the call is no more indirect than the stored function's.

Planned: each function-typed field of a `ref struct` is a C++ template parameter of the struct, so
the optimizer can inline the lambda into the loop that calls it: `Where<T>` in Wylow is `Where<T, F>`
in C++, one type per lambda. A `ref struct` can't be reassigned or chosen by `?:`, so a variable
never needs to hold two of them; a function taking one as a parameter is compiled once per lambda,
as a generic function is once per type argument.

## Threads

Thread confinement is what lets reference counts and borrow counters be non-atomic, and the pools
and the cycle collector's buffer belong to one thread with no synchronization.

A `shared class` has an atomic count, and its objects use the global heap rather than a
thread's pools.

- Each thread has its own pools and its own cycle collector buffer; a thread a foreign library
  created gets them the first time it runs Wylow code ([Callbacks](../spec/21-interop.md#interop.callbacks)).
  An object freed on another thread than the one whose pool it came from goes back through that
  chunk's remote free list ([Allocation](#allocation)).
- `String`'s count is always atomic. A program that never starts a thread could use non-atomic
  counts instead, which no program can observe; that is an optimization for later.
- The threads themselves are the operating system's.

A thread's pools and buffer are two members of one `wyl::ThreadState`, which the thread reaches
through a `thread_local` pointer. The main thread's state is a function-local static that `wyl::Init`
makes current, and it lives until exit. A spawned thread makes its state as its first act and ends
it as its last, before the thread function returns. Ending a state runs the collector's `EndThread`
first and then frees the state, which runs the pools' destructor. A thread that a foreign library
created makes its state on first use, and a `thread_local` guard ends that state when the thread
ends. The state is not itself a `thread_local` object, because MinGW's emulated thread-local storage
frees every `thread_local` before it runs their destructors. If the state were one, the collection
at a thread's end would make a second state for the destructors it runs, and it would read the
creation counter after that counter had been reset.

- A thread other than the main one collects its buffer when it ends, as `EndThread` does. The
  thread's chunks with live slots are made ownerless, and the next thread that frees into one adopts
  it: that thread adds the chunk to its own size class and then frees the slot. The main thread
  reports the cycles it never freed instead.
- `wyl::Init` marks the main thread's state, and `wyl::BeginMain` marks that `main` has begun after
  every static is initialized. `spawn` panics before that mark.
- `std::threading`'s `Thread<T>` holds a `byte*` to a `wyl::threading::HandleOf<T>`, which the global heap
  holds: the operating-system thread, and an `Optional<T>` for its result. `spawnThread` moves the
  body, a `wyl::Function<T()>`, into a new thread, which makes the thread's state first and ends it last.
  `joinThread` joins and moves the result out, and panics on a null handle, which a thread that was
  never started has. `releaseThread` joins and destroys the handle, and does nothing for a null
  handle.
- `std::threading`'s `SyncState<T>` is a `std::shared_ptr` to a `wyl::threading::ChannelState<T>` on the global
  heap: a mutex, a condition variable, a `std::deque<T>`, and counts of the senders and receivers
  (`runtime/wyl/channels.h`). A copy of the handle shares the state, and the last handle frees it.
  `send` moves the value into the queue under the mutex and notifies one waiter. `receive` waits on
  the condition variable until the queue is not empty or the channel closed, and moves a value out.
  Releasing the last sender closes the channel and notifies every waiter, and the close destroys the
  queued values after the mutex is released, since a destructor may run any code. The close destroys
  the queue only when no receiver is left: a receiver that lives still takes the queued values and
  then reads null, and the last receiver leaving after the close destroys what is left.
- A unique value now crosses threads through a channel as well as through a thread's result, so the
  remote free list and the adoption of an ownerless chunk are reachable from a program:
  `channels/unique_value` sends an object made in one thread's pool and frees it in another's.
- `std::threading`'s `Isolated<T>` is a unique struct holding the value and a `wyl::IsolatedState`,
  which owns a `wyl::CycleBuffer` (`runtime/wyl/isolated.h`). `CycleCollector::SwapBuffer` makes a
  graph's buffer the thread's while the standard library runs the constructor's or `open`'s lambda,
  and `MergeBuffer` adds a graph's objects to the thread's buffer when the box is destroyed, so a
  box dropped or taken returns the graph's objects to the thread that released it. The value moves
  out through `takeValue`, whose C++ body returns `std::move(value)`.
- `std::threading`'s `Mutex<T>` holds a `wyl::MutexState<T>`, a pointer to a
  `wyl::threading::MutexData<T>` on the global heap: a `std::mutex`, the value, and the graph's
  `IsolatedState` (`runtime/wyl/mutex.h`). The `Mutex` object owns the data, and its destructor
  calls `releaseMutex`, which deletes it. The graph's state is the last member, so it is destroyed
  first, and its buffer joins the releasing thread's before the value is released. `lockMutex` takes
  the `std::mutex`, sets the thread's `HoldsLock` flag, and swaps the graph's buffer in while the
  lambda runs.
- `std::threading`'s `Atomic<T>` and `AtomicBool` hold a `wyl::AtomicState<T>`, a pointer to a
  `wyl::threading::AtomicData<T>` on the global heap, which holds a `std::atomic<T>`
  (`runtime/wyl/atomic.h`). The atomic object owns the data, and its destructor calls
  `releaseAtomic`, which deletes it. Every operation uses `std::atomic`'s default order,
  `memory_order_seq_cst`. A debug build's `atomicAdd` and `atomicSubtract` loop on
  `compare_exchange_weak` around the checked `wyl::Add` and `wyl::Sub`, so an overflow panics before
  anything is stored. In a release build, they call `fetch_add` and `fetch_sub`.
- `std::threading`'s `Frozen<T>` has no part in the runtime. It holds the pointer and the length of a
  buffer that `memory::allocate` took from the global heap, and its members are `unsafe` Wylow, as
  `List`'s are. `List.freeze` hands the list's buffer over, so any thread may release the last
  reference and free it.
- `wyl::ThreadState` holds `HoldsLock`, which says whether the thread holds a mutex's lock. `lockMutex`
  panics when the flag is set, and `receiveValue`, `joinThread`, and `releaseThread` call
  `RefuseWaitWhileLocked`, which panics when it is set. A thread holds at most one lock and waits for
  nothing while it holds it, so one flag is the whole state of the rule.

The interpreter runs each logical Wylow thread on an operating-system thread, with no schedule of
its own (decision 0057). Each thread has its own cycle buffer, collector figures, destroying
counter, and call depth (`Interpreter.ThreadState`). Reference counts, the weak block, the creation
counter, and the output are the parts several threads reach. The interpreter counts references
atomically, changes the weak block under a lock, and writes the output under a lock. A wait in
`join`, in a thread's release, or in `receive` ends when the target returns, when a value arrives,
when the channel closes, or when the run ends. A panic on any thread wakes every wait.

## Interop

So far only the standard library declares extern functions, one at a time: `static extern void
check(int index, int length);` in a struct, or a free `extern` function. The C++ runtime
(`runtime/wyl/intrinsics.h`) implements them, as `wyl::name`, or `wyl::type::name` for a method of a
type (the type's name in lower camel case). An `extern struct` of the standard library is the C++
class `wyl::Name` (`wyl::Weak`, `wyl::String`), except one named for a primitive type
(`Stdlib/primitives.wyl`): a C++ arithmetic type has no members, so its extern methods are members
of `wyl::Builtin<T>` (`compare.h`), which wraps the value. `wyl::Bound<I>` gives a primitive or a
`String` as its `Builtin`, whose `m_`-named members are what a call through a generic bound names.

For each extern struct declared with fields, the compiler emits size and offset checks into the
generated C++:

```cpp
static_assert(sizeof(SDL_Rect) == 16);
static_assert(offsetof(SDL_Rect, x) == 0);
static_assert(offsetof(SDL_Rect, w) == 8);
// ...
```

Extern calls are emitted through `noexcept` wrappers, so a C++ exception that would propagate into
Wylow code calls `std::terminate`.

## Panics

A panic prints its message and a stack trace where it happens, and a debugger stops there (`wyl::RaisePanic` in `runtime/wyl/core.h`, which ends with `std::abort`).

## Known limits

**Long chains are destroyed recursively.** Releasing the head of a long linked list destroys each
object inside the release of the one before it, and a long enough chain exhausts the stack. A type
that forms long chains releases them in a loop in its destructor (decision 0048).

## Costs to remove later

What the implementation above pays that it needn't. None of these changes behavior, so each can
be done when profiling shows it matters.

- **Indirect calls where templates can't be used.** A `wyl::FunctionRef` costs an indirect call
  and keeps the lambda from being inlined. It could be avoided by devirtualizing calls whose
  receiver type is known, and for constructors sharing a parameter count, by telling them apart
  with a tag argument.
- **Code size.** Each distinct lambda passed to a template instantiates the callee again, as
  generics do per type. A performance warning could point out large instantiation counts.
- **Pins for locals a lambda assigns.** Such a local is held while in use as a receiver or
  argument (a reference count increment and decrement) even when no lambda that assigns it can
  run during that call. Only calls passed such a lambda, directly or by forwarding, can change it.
- **Evaluation order.** Any call given a function value, and any call of one, is treated as
  writing every local that a non-escaping lambda captures and that is changed anywhere, so operands
  reading one are hoisted into an ordering lambda, though the lambda the call can run may not change
  it. Tracking which locals each lambda changes, and which lambdas each call can run, would avoid
  that. Each hoisting nests a C++ block, so about 25 levels of nested lambdas exceed MSVC's limit on
  block nesting. Real code doesn't nest that deep, so this is a known limit, not a planned fix.
- **Element aliasing and cycles.** A call of a function value counts as writing what every lambda
  of its type writes, so one `fn(Mob) -> void` lambda storing into elements of type `X` anywhere
  makes every loop over `X` that calls a `fn(Mob) -> void` count references per element
  ([In-place loop elements](#in-place-loop-elements)); likewise, a function field may hold
  any lambda of its type for the cycle analysis. Both could count only the lambdas that can reach
  that call or field.
- **Narrowing.** A null check outside a lambda doesn't carry into it (except a `ref?` variable's,
  which nothing can make refer elsewhere), and a local declared outside any lambda that assigns it
  is never narrowed, which can force a `!` (and its check).

- **Which functions take a `wyl::FunctionRef`**: the rules are simple and conservative, and could
  be wrong in the slow direction often enough to matter.
