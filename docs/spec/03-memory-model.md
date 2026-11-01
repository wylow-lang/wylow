# 3. Memory model

Wylow has no tracing garbage collector and no borrow checker. Memory safety comes from three kinds of
storage, each with one simple rule. Objects are freed by reference counting, and cycles of objects
by a collector that runs only when the program calls it.

| Kind | Lives | Freed by | Safety mechanism |
|---|---|---|---|
| **Values** (primitives, `struct`, `T[N]`) | Inline | Whatever contains them | None needed |
| **Objects** (`class`) | Heap | Reference counting | Refcount; `Weak<T>` and `Memory.collectCycles` for cycles |
| **References** (`ref T`, `Span<T>`) | Local only | — (a view) | Cannot escape; runtime borrow tracking |

## 3.1 Values

Structs, primitives, and fixed arrays are stored inline and copied on assignment
(see [02-types.md](02-types.md#29-structs)). They have no destructors. Destroying a struct means
releasing any class references it contains.

## 3.2 Objects (classes)

- Allocated with `new`, on the heap (an object that can't outlive its scope may be on the stack
  instead, which no program can observe; see [Stack allocation](#stack-allocation)).
- **Reference counted** with an intrusive (in-object) count. Normal classes use a non-atomic count;
  `shared class` uses an atomic one ([05-concurrency.md](05-concurrency.md)).
- The destructor runs immediately when the last reference is released. This makes classes the
  right place for owning resources (GPU buffers, files, sockets).

### When references are released

- A local or parameter releases its reference when it goes out of scope; fields when their object
  is destroyed, after its destructor body has run (fields first, then the base class's destructor).
  Destruction starts at the object's own class: each class in turn runs its destructor body and
  then releases the fields it declares, in reverse declaration order, before the base class's
  destructor runs. Its abstract or virtual methods dispatch to the class being destroyed, so they
  never see a derived class's fields.
- A closure releases the copies it captured, and a choice the callables it holds, in the order they
  are held, not in the reverse order a struct's fields take. Either one's last reference releases
  them, like an object's fields.
- A temporary, such as an object returned by a call and not stored, is released when the statement
  that created it completes, not at its last use, so a destructor's timing can be read from the
  source. Within a statement:
  - temporaries are released in the reverse of the order they were created;
  - an assignment releases the old value first, then the statement's temporaries: in `x = f(g())`,
    `x`'s old value is released before `g()`'s result;
  - a temporary created in an operand that may not be evaluated (the right side of `&&`, `||`, and
    `??`, a branch of `?:`, a case of a switch expression, a call after `?.`) is released when that
    operand completes, since what the operand produces is a copy;
  - each evaluation of an `if` or `while` condition, or of a `for` loop's condition or update,
    is a statement of its own, so its temporaries are released before the body or the next step
    runs. A `switch`'s matched value and what an `is ref` binding holds last longer, as described
    where they are defined.
  - in a `return`, the statement's temporaries are released after the value is computed, before
    the function's locals.
  - each argument of `super(...)` is a statement of its own, so its temporaries are released
    before the next argument is evaluated and before the base constructor runs. The values passed
    are released when the base constructor returns, the last first. (C++ can run nothing before a
    base constructor but the expressions computing its arguments.)
- **Assignment stores the new value before releasing the old one**, so a destructor triggered by an
  assignment sees the new value in the assigned place. Assigning a struct stores every field before
  releasing any old field.
- The compiler may release an object earlier than these rules say only where no program can tell:
  its release can run no destructor, and nothing refers to it weakly. The strong references the
  compiler adds to keep a place or a receiver alive ([Pinning](#pinning-class-objects)) are
  released as the statement's temporaries are, in the same reverse order of creation. Leaving one
  out is allowed only where it could never be the last reference, so it never moves a release.

- A destructor may use `this` freely, but must not keep a reference to its object: if a reference
  to it still exists when the destructor finishes, the program panics.
- Objects in a cycle are released only by `Memory.collectCycles` ([below](#cycles-memorycollectcycles)).

> ⚠️ **Long chains are destroyed recursively.** Releasing the head of a long linked list destroys
> each node from inside the previous node's destruction, which can overflow the stack (an abort, see
> [01-safety.md](01-safety.md#12-how-each-source-of-c-ub-is-handled)). Break up very long chains
> manually for now.

### Implementation

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

### Allocation

Objects, closures, and weak blocks up to 512 bytes come from the runtime's **pools** rather than the
C++ global heap; larger ones, and over-aligned ones, use the global heap. A pool is a size class:
slots of one size, in multiples of 16 bytes, carved from 64 KiB chunks aligned to their size.

- **Allocating** takes a slot from the size class's current chunk: its free list, or else its
  uncarved end. When the current chunk is full, the fullest chunk of the class that has room
  becomes current, so that new objects fill few chunks while the others drain. Partial chunks are
  kept in 8 bins by how full they are, which makes the choice constant-time.
- **Freeing** pushes the slot onto its own chunk's free list, found by masking its address, so a
  slot needs no header. The size comes from C++'s sized delete through the virtual destructor.
- **Memory goes back to the OS** when a chunk is left with no live slots, except that one empty
  chunk per size class is kept, so that a count of objects hovering at a chunk's edge doesn't map
  and unmap a chunk each time. A chunk with any live object stays mapped: objects never move, so a
  few long-lived objects scattered over many chunks keep all of those chunks. Choosing the fullest
  chunk only reduces this.
- **Pools are per thread, with no synchronization.** An object is freed on the thread that made
  it, since it never leaves that thread ([05-concurrency.md](05-concurrency.md#51-thread-confinement)).
  `shared class` objects, which may be freed on any thread, cannot use these pools.
- Under AddressSanitizer every object uses the global heap, so that ASan can still catch a use
  after free.

> Implemented: everything above, except that the pools are global, since threads don't exist yet.

### Stack allocation

The compiler may put an object made by `new` (or the closure an escaping lambda makes) in the
stack frame of the scope making it instead of on the heap, saving the allocation and the free,
when no reference to it can outlive that scope:

```java
var hit = new RayHit(origin, dir);      // only used here: may be on the stack
if (raycast(world, hit)) { print(hit.distance); }
print(new Path(a, b).length());         // a temporary: may be on the stack
```

No program can tell the difference, which is what allows it. An object is destroyed when its last
reference is released (see [When references are released](#when-references-are-released)), and the
compiler does this only where the stack object is destroyed at exactly that point, in the same order
relative to everything else. Panics abort without unwinding, so there is no other way out of the
scope. The only exception is diagnostics: `Memory.cycleStats()` counts what the cycle collector
buffered, and it never buffers an object on the stack.

Which objects qualify is the compiler's business and may change between versions; an object
qualifies only if it doesn't **escape**: stored in a field, an element, or a static, returned,
captured by a closure that escapes, referred to by a `Weak`, or given to a function that may do
any of these. How the current compiler decides is in
[docs/design/stack-objects.md](../design/stack-objects.md).

### Weak references: `Weak<T>`

`Weak<T>` refers to an object without keeping it alive. It is an ordinary value type from the
standard library, so it can be a field (of a class or a struct), a local, a parameter, or an
element: `List<Weak<Listener>>` works.

```java
class Node {
    pub List<Node> children;
    pub mut Weak<Node> parent;             // does not keep the parent alive
}

child.parent = node;                       // a T or T? converts implicitly to Weak<T>
if (child.parent.get() is Node p) { ... }  // get() returns T?: null once the target is gone
child.parent = null;                       // refers to nothing
```

- `T` is a class or interface type, written without `?`; it cannot be a type parameter, which may
  stand for a struct. `Weak<T>` is defaultable: its default refers to nothing. Weak references
  don't support `==`; compare what `get()` returns.
- `get()` returns a strong `T?`. Keep it in a local (or a pattern binding, as above) to use the
  object for more than one operation.
- A weak reference reads as null from the moment its target's count reaches zero, before the
  target's destructor runs, whether the target was released or collected as part of a cycle. A
  weak reference to the object taken by its own destructor reads null too.
- `Weak<T>` is not sendable ([05-concurrency.md](05-concurrency.md)), since its target belongs to
  one thread.
- There is no `weak` modifier: `Weak<T>` is the only kind of weak reference.

Use a weak reference when a reference should not keep its target alive: a parent pointer, an
observer list, a cache, an enemy's current target. Cycles of strong references are also freed by the
cycle collector (below), but only when the program asks; a weak reference lets the object be
destroyed as soon as nothing else holds it.

**Implementation.** The first time an object is referred to weakly, it allocates a small weak
block `{ target, count, buffer slot }` and points its header at it. A `Weak<T>` is a pointer to the block and
one to the target (C++ can't cast the block's `wyl::Object*` back down to `T`), and copying one adds
to the block's count. `Weak<T>` is an `extern struct` ([07-interop.md](07-interop.md)) implemented
by `wyl::Weak` in the runtime. When the target's count reaches zero, the block's target is
set to null; the block itself is freed when the target and the last `Weak` to it are gone. An
object that is never referred to weakly costs nothing extra; one that is costs one small
allocation, once.

### Cycles: `Memory.collectCycles`

Reference counting alone never frees a cycle: objects that refer to each other keep each other's
counts above zero. Wylow frees them with a **cycle collector that runs only when the program calls
it**:

```java
void endFrame() {
    Memory.collectCycles(2000);            // visit at most 2000 objects, at this point in the frame
}
```

**It never runs on its own.** No allocation, release, or timer triggers it, and the program does not
collect at exit. A game calls it at a point it chooses, typically once per frame or on a level
transition, so collection shows up in a profiler exactly where it was called. A program that never
calls it simply never frees its cycles, as in the past.

| Call | Does |
|---|---|
| `Memory.collectCycles()` | Processes every buffered object (below) and frees every cycle found |
| `Memory.collectCycles(int maxVisits)` | Stops after visiting about `maxVisits` objects; the rest wait for the next call |
| `Memory.cycleStats()` | Counts since the last call: objects buffered, visited, and freed, as a `CycleStats` struct |

The work done is the same on every run of the same build, on every machine: each thread collects
only its own objects, and a budget counts objects, not time. `Memory.cycleStats()` reports how long
visits take, to choose a budget. A call without a budget frees exactly the cycles that are
unreachable, in any build. Which of them a budgeted call gets to may differ between builds (another
compiler version, debug or release), since references the compiler keeps for itself, such as pins,
buffer objects too.

#### What the collector looks at

The compiler finds the classes that **may be cyclic**: those that can reach an object of their own
class through strong references in fields, including through structs, enums, fixed arrays, and
container elements (`List<Node>`), and counting every class a field's type could refer to
(subclasses, interface implementations). `Node` above is cyclic through `children`; a class holding
`Vec3`s, a `List<int>`, a `String`, and a `Mesh` that holds no references back is not.

An escaping function ([06-syntax.md](06-syntax.md#escaping-and-non-escaping)) holds a closure
object with copies of what its lambda captured, and can only be a lambda of its own type. So a
function field (or a `List<fn() -> void>`) counts as able to reach what any lambda of that type
captures, and each lambda's closures are decided like a class: cyclic only if what it captures can
reach one of them back. A lambda in generic code may capture anything, and counts for every type it
could be: `fn() -> T` for any `fn() -> X`. A class holding `fn() -> void` handlers whose lambdas capture
only values, or objects that can't lead back to it, is not cyclic, and neither are those closures.

Generic classes are decided per instantiation: `List<Node>` is cyclic, `List<int>` is not. Container
elements are those of the standard library's containers, `List` and `Array`, which store them behind
a pointer; a class of the program is looked at only through its fields, even if it is `Spannable`.
A field's references are looked at whatever their class, but objects of acyclic classes are never
visited, so the references they hold count as references from outside.

- **Objects of acyclic classes cost nothing**, and are never buffered or visited. Most objects in a
  game are values or acyclic.
- When a reference to a **cyclic** object is released and its count stays above zero, the object may
  have just become part of an unreachable cycle, so it is added to its thread's buffer (once, until
  the next collection). That is one branch per such release, and an append the first time.
- A collection runs trial deletion from the buffered objects: it subtracts the references the
  objects reachable from them hold to each other, and objects left with no references from outside
  that group are garbage. The work is proportional to what is reachable from the buffered objects,
  never the whole heap. With a budget, a buffered object whose reachable group exceeds what is left
  of the budget is put back for a later call rather than half-visited.
- `@Acyclic` on a class declares that its objects never form cycles, so they are never buffered.
  If that is wrong, the cycle leaks; it is never undefined behavior.
- `shared class` objects ([05-concurrency.md](05-concurrency.md)) are not collected, since other
  threads may refer to them. Their fields can't be `mut`, and only synchronization types let
  them change, so cycles among them are rare; break any with `Weak<T>`.

#### Destroying a cycle

1. Every weak reference to every object in the cycle is cleared.
2. Every object's destructor runs, oldest object first, while all their fields are still intact:
   each destructor sees the rest of the cycle undestroyed. This covers every cycle one call frees,
   so their destructors interleave by age. (Among objects created 2^32 or more objects apart, the
   order is still fixed, but may not be by age.)
3. Their fields are released and the memory is freed. The standard library's containers release their
   elements here, with the fields: their destructors only release storage, so they run after every
   destructor of the cycle, which sees every container of the cycle still full.

As with any destructor, a destructor that stores a reference to its object, or to another object of
the cycle, somewhere outside the cycle panics. Calling `collectCycles` from a destructor panics.

Collection is always safe, wherever it is called. Every reference to an object held by running code
(a local, a pin, the element a loop refers to in place) is counted, so an object still in use always
has a reference from outside any cycle and is never freed.

#### Diagnostics

Debug builds report, as performance warnings:

- a buffer that keeps growing across calls, meaning collections are not keeping up with the budget;
- a class whose objects are freed by the collector again and again, where a `Weak<T>` would let them
  be destroyed immediately instead;
- at exit, the cycles still uncollected, by class (found without running destructors).

They go to stderr as `performance warning: ...`. The buffer warning fires after it grows on 5
consecutive calls, the class warning once a class has been freed by 10 calls, each once per program.
Generic classes are named with their type arguments (`List<Node>`) where the compiler knows the
instantiation.

> Implemented so far: everything above except per-class counts and visit timing in
> `Memory.cycleStats()`, and `shared class` (threads don't exist yet, so the buffer is global).
> Instantiations of generic classes that only C++ sees, through a generic function's type arguments,
> are treated as cyclic, and a field whose type could refer to one as reaching every class; neither
> affects correctness, only how much is buffered. `visited` counts the objects of the groups that
> fit in the budget.

## 3.3 References

`ref T` is a reference to a value that lives somewhere else (a local, a struct field, an array
element). References avoid copying and allow mutation in place. An `in T` reference
is the same but read-only.

References are **second-class**:

| Allowed | Not allowed |
|---|---|
| Method parameters: `void damage(ref Enemy e)` | Struct or class fields |
| Local variables: `ref Transform t = player.transform;` | Return values, except from a method into its receiver (below) |
| Loop variables: `for (ref Enemy e : enemies)` | Capture by a closure that may outlive the call |
| Pattern bindings: `if (list.get(i) is ref Enemy e)` | Storing in any container |

A `ref` or `in` variable must be initialized with the place it refers to: a variable, field, or
element, never a temporary value. It cannot refer to a class object itself (objects are already
references). Assigning to it assigns to the place.

### Returning references

A method may return `ref T` or `in T`, using `return ref place;`. The returned reference
**borrows the method's receiver**, so it must point into the receiver's own storage: `this`, a
field or element of `this` (but not of another object a field refers to), a reference returned
by a method of `this`, or, in unsafe code, the target of a pointer. Indexers are the usual case:

```java
struct Mat4 {
    pub float[16] m;
    pub mut ref float operator [](int row, int col) { return ref m[row * 4 + col]; }
}
```

- Only instance methods return references, and for now they cannot be virtual or abstract.
- A `ref` return must point into mutable storage, so a struct method that isn't `mut` returns `in` (or a
  pointer's target). Calling a method that returns `in` gives a read-only place.
- The result is a place for as long as the receiver is: on a temporary struct, such as
  `makeMatrix()[0, 0]`, it can be read but not assigned or referred to.
- Free functions and static methods cannot return references yet.

### Nullable references: `ref? T`

`ref? T` (and `in? T`) is a reference that may refer to nothing, for lookups that can fail without
copying what they find: `list.get(i)` is null for an index out of bounds.

```java
if (enemies.get(i) is ref Enemy e) {
    e.health -= 10;
}
ref? Enemy found = enemies.get(i);
if (found != null) {
    found.health -= 10;
}
```

- It is not `ref T?`, which is a reference to a variable of type `T?` (`void reset(ref int? v)`).
  The two combine: `ref? int?` may refer to an `int?` field or element. A `ref` or `ref?` variable
  does not refer to a nullable local, which a null check could narrow and which the reference could
  then set to null; pass such a local by `ref` to a function instead. An `in` or `in?` variable is
  read-only, so it may ([02-types.md](02-types.md#nullable-value-types)).
- It is allowed where `ref` is (parameters, locals, pattern bindings, and returns into the
  receiver) and follows the same rules: second-class, and the storage it points into is held or
  borrowed while it is used.
- A method returns one with `return ref place;` or `return null;`.
- It can only be used as a place after a check, as for nullable types (§2.5): `x is ref T name`
  (or `in T name`) binds the place, and `x != null` narrows a `ref?` local or parameter. Narrowing
  is always sound here, since a reference can't be made to refer elsewhere: assigning to it
  assigns to the place. `x!` gives the place or panics.
- It converts implicitly from `ref T` to `ref? T`, and from `ref` to `in`, never the other way.
- It compiles to a C++ pointer that is null when there is no place, so it costs nothing over `ref`.
- It is initialized or passed like a `ref`, or with `null`: `ref? T x = place;`, `ref? T x = null;`,
  and `ref? var x = list.get(i);`, which takes the type of what it is initialized with. A `ref?`
  parameter takes `ref place`, `ref other` for another `ref?`, or `null`, which needs no `ref`, and
  may default to `null`. An `in?` parameter takes whatever an `in` one takes, including a temporary
  value, as well as `null` and any `ref?` or `in?`.
- Where it is narrowed, it is used as the place: `found.health -= 10`, `found = Enemy(...)` (which
  assigns the place), or passed by `ref`. Narrowing it is always sound, so a null check outside a
  lambda narrows it inside too.
- `x?.member` reaches the member in the place referred to, not in a copy as for a nullable value,
  so `list.get(i)?.takeDamage(5)` changes the element; what the place lies in is held or borrowed
  until the member is done.
- It is not a value: `var x = list.get(i);` is an error (write `ref? var`, or copy with `!`), and so
  are `??` on one (whether it would give a place or a copy is unclear), a `?:` or switch expression
  producing one, and using one as a type argument.
- Overloads cannot differ only in `ref` and `ref?` (`f(ref int)` and `f(ref? int)`), since
  `f(ref x)` could call either.
- `x is ref T name` (or `ref var name`) is only an `is` test, not a case of a switch. The name is in
  scope like any pattern's, and while it is, what it refers into is held or borrowed as for a `ref`
  variable; when the test fails, that is let go at once, so an `else` branch can still add to the
  list that `list.get(i)` looked in.

C# has no such type: `CollectionsMarshal.GetValueRefOrNullRef` returns a plain `ref` that may be
null, which the caller must remember to test with `Unsafe.IsNullRef`. Rust's `Option<&mut T>` is
the model.

Because a reference cannot outlive the function that created it, the only remaining danger is the
storage it points into being freed or moved while the function runs. Two runtime mechanisms
prevent that.

### Pinning class objects

Taking a reference into a field of a class object holds a strong reference to that object for the
reference's scope, so the object cannot be destroyed while referenced. The compiler omits this
when it can prove it unnecessary (for example, the object is held by a local that is never
reassigned).

The same applies to the receiver of a method call: in `player.weapon.fire()`, the call holds the
weapon, so `fire` may replace `player.weapon` without destroying the object it is running on.

The current rule: the compiler pins (holds a strong reference until the statement completes, as it
would a temporary) the receiver of a method call, and the object owning the place passed as a
`ref`/`in` argument or used as a struct method's receiver, unless that object is `this` or a local
or parameter, or is held by a field that isn't `mut` of such an object. Locals are safe without a pin because nothing but the
function itself can assign them, and a class-typed local cannot even be passed by `ref`. `this` is
safe because whoever called the method has pinned it. A field that isn't `mut` (§2.7) is never
reassigned once its object is constructed, so it holds its object as long as the object holding it
lives; inside its own class's constructors, which may still assign it, it is pinned like a `mut`
field. Nor does it pin an object held by a `mut` field of such an object when nothing that runs
while the pin would last may assign that field: the compiler knows which fields each function may
assign, including through the functions, overrides, lambdas, and destructors it may run, and
through references, pointers, and struct methods' `this` into a value of the type the field
contains. A pin costs one count increment and decrement.

A `ref` or `in` variable, and a span variable, holds the object for its whole scope, even when a
local holds it: the function itself may reassign that local while the reference lives. An object
held by `this`, or by a field that isn't `mut` of `this` (outside its class's constructors), needs
no hold.

### Loop elements

In `for (Enemy e : list)`, `e` holds its own reference to each element's object, like any local,
unless nothing the loop can run may store over an element of that type: assign to an element
(`list[i] = x`, `array[i] = x`, or through a `ref` in generic code), or to a value containing a fixed
array of them. Then `e` refers to the element in place, saving a count increment and decrement per
element. What the loop iterates is held and borrowed until it ends, so storing over the element is
the only way its object could be destroyed. The same goes for an element of any type whose copy
costs something, such as a string or a struct, which saves the copy; for a struct, assigning a
field of an element (`list[i].x = 1`) counts as well, as does a write through a `ref` to a type
the struct contains. Storage of one type is never another's (01-safety.md §1.2), so a write of an
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
new Enemy(...); }`) keeps a loop over elements of that type, such as `for (Enemy e : enemies) {
e.hp -= 1; }`, holding its own reference to each element only if the loop may release a `Squad`,
or something that may hold one; `Memory.collectCycles()` may release anything. Destructors that
only assign fields, or store elements of other types, never affect it.

### Evaluation order and the heap

Operands are evaluated left to right ([06-syntax.md](06-syntax.md#evaluation-order)). Since any call
can modify any object, reading a field of an object counts as conflicting with every call in the
same expression: in `obj.x + f()`, `obj.x` is read before `f` runs. The same holds for reads
through `ref`/`in` parameters and a struct method's `this`, which may point into an object. In an
assignment, the object holding the target is evaluated before the value: in
`a.b.x = f()`, `a.b` is read before `f` runs. An element target is found, including its index and
bounds check, before the value: `list[i()] = f()` calls `i` before `f`, and `a[f()] += 1` calls
`f` once.

A call that may run a lambda (a call of a function value, or a call given one) counts as
conflicting with reading a local that a lambda captures and that is changed anywhere, since only a
non-escaping lambda can change a local, and only while such a call runs: in `x + run(() -> x = 5)`,
`x` is read before `run`. Other locals are read wherever C++ reads them.

### Borrow counters on containers

Containers that can move or free their elements keep a **borrow count**. Taking a `ref` or `Span`
into the container increments it; the end of the reference's scope decrements it.

| Container | Operations that panic while borrowed |
|---|---|
| `List<T>` | `add`, `insert`, `removeAt`, `clear`, `reserve`, `trim`: anything that changes its length or may reallocate |
| `T[]` | None (never reallocates; kept alive by pinning) |

```java
for (ref Bullet b : bullets) {
    if (b.hit) bullets.add(Bullet(...));       // panic: List modified while borrowed
}
```

The usual fix is to collect changes in a second list and apply them after the loop.

Destroying a borrowed container cannot happen: a borrow also pins the container.

A borrow starts when a reference or span into the container is taken from one of its methods (an
indexer, `asSpan`) and lasts:

| Where the reference or span is | Borrowed until |
|---|---|
| A `ref`, `in`, or span variable | The end of the variable's scope |
| A for-each loop over the container | The end of the loop |
| A `ref`/`in` argument, a span argument, or a struct method's receiver | The end of the statement |
| An assignment's target, when the value runs code: `list[0] = f()` | The end of the assignment |

Reading an element by value, or storing a value that runs no code, needs no borrow: the
reference is used before anything else can happen. In an assignment like `list[0] = f()`, the
element is found first, so `f` must not change the list's length.

#### `Borrowable`

The counting is ordinary standard library code. A class that implements the standard library's
`Borrowable` interface has `beginBorrow()` called when a borrow of it starts and `endBorrow()`
when it ends; `List` counts borrows in a field and checks it in each method that changes its
length. The compiler makes these calls, including when the object is only known through an
interface; code cannot call them directly. Only classes can be `Borrowable`.

### Spans

`Span<T>` and `ReadOnlySpan<T>` follow exactly the same rules as `ref`: second-class, with the
same borrowing and pinning behavior. They are `ref struct`s, and any struct can be declared one:

```java
ref struct Cursor { ... }
```

A `ref struct` may be a local variable or a parameter, but not:

- a field (of any type, including another `ref struct`), a variant's field, an array element, a
  pointer's target, or a type argument;
- assigned after its declaration, so a variable views one thing for its whole scope. It must be
  initialized where it is declared;
- the result of `?:` or a switch expression, or matched against patterns;
- returned, except by an instance method, and then only a view of the receiver: its fields or
  elements, or a span its methods return. A free function or static method cannot return one.

A span variable cannot view a temporary struct, such as `makeSamples().view()`, since the struct
is gone after the statement. A temporary object is fine: the span holds it.

> Planned, for lazy queries ([02-types.md](02-types.md#queries)): a `ref struct` may have fields
> of `ref struct` and function types, a generic `ref struct` may take a `ref struct` type argument,
> and an extension method may return a `ref struct` that views its `this` parameter and holds its
> function-typed parameters.

## 3.4 What the checks cost

| Operation | Checked | `@Unchecked` |
|---|---|---|
| Array/span index | 1 compare | 0 |
| Copy a class reference | refcount inc/dec | same (pass by `ref` to avoid) |
| For-each over class elements | refcount inc/dec per element only if the loop may store over one | same |
| Release a reference to a cyclic class, count stays above 0 | 1 branch; a buffer append the first time | same |
| Copy a `Weak<T>` | weak block count inc/dec | same |
| Cycle collection | only where `Memory.collectCycles` is called, proportional to objects visited | same |
| Borrow from class field | refcount inc/dec, often elided | 0 |
| Borrow from container | counter inc/dec | 0 |
| Pass a lambda to a function parameter | nothing: inlined, like a hand-written loop | same |
| Pass a lambda to an overridden or interface method, a recursive function passing itself new lambdas, or an overloaded constructor | an indirect call per call of it | same |
| Choose a function with `?:` | a branch on the choice per call; every lambda it could be is still inlined | same |
| Use a local that a lambda assigns, as a receiver or argument | refcount inc/dec while in use, if it holds objects | same |
| Make an escaping lambda | a heap allocation holding copies of what it captures; nothing if it captures nothing | same |
| Call an escaping function | an indirect call; refcount inc/dec on the closure if it is stored where the call could replace it (a field, a container element, a local a lambda assigns) | same |
| Copy an escaping function | refcount inc/dec, as a class reference | same |
| Pass an escaping function to a non-escaping parameter | refcount inc/dec for the call, unless it comes from a local that can't change meanwhile | same |
