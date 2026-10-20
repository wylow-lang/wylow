# 3. Memory model

VPL has no garbage collector and no borrow checker. Memory safety comes from four kinds of
storage, each with one simple rule.

| Kind | Lives | Freed by | Safety mechanism |
|---|---|---|---|
| **Values** (primitives, `struct`, `T[N]`) | Inline | Whatever contains them | None needed |
| **Objects** (`class`) | Heap | Reference counting | Refcount; `weak` for cycles |
| **Pool slots** (`Pool<T>` + `Handle<T>`) | Stable chunks owned by the pool | The pool | Generation check |
| **References** (`ref T`, `Span<T>`) | Local only | — (a view) | Cannot escape; runtime borrow tracking |

## 3.1 Values

Structs, primitives, and fixed arrays are stored inline and copied on assignment
(see [02-types.md](02-types.md#29-structs)). They have no destructors. Destroying a struct means
releasing any class references it contains.

## 3.2 Objects (classes)

- Allocated with `new`, always on the heap.
- **Reference counted** with an intrusive (in-object) count. Normal classes use a non-atomic count;
  `shared class` uses an atomic one ([05-concurrency.md](05-concurrency.md)).
- The destructor runs immediately when the last reference is released. This makes classes the
  right place for owning resources (GPU buffers, files, sockets).

### When references are released

- A local or parameter releases its reference when it goes out of scope; fields when their object
  is destroyed, after its destructor body has run (fields first, then the base class's destructor).
- A temporary, such as an object returned by a call and not stored, is released by the end of the
  statement that created it. The exact point within the statement may vary by statement but is the
  same on every platform and C++ compiler.
- **Assignment stores the new value before releasing the old one**, so a destructor triggered by an
  assignment sees the new value in the assigned place. Assigning a struct stores every field before
  releasing any old field.
- A destructor may use `this` freely, but must not keep a reference to its object: if a reference
  to it still exists when the destructor finishes, the program panics.

> ⚠️ **Long chains are destroyed recursively.** Releasing the head of a long linked list destroys
> each node from inside the previous node's destruction, which can overflow the stack (an abort, see
> [01-safety.md](01-safety.md#12-how-each-source-of-c-ub-is-handled)). Break up very long chains
> manually for now.

### Implementation

Every class object starts with a header holding a non-atomic reference count and the head of a
list of weak references. In C++, classes and interfaces derive virtually from one `vpl::Object`, so
an object has one count however many interfaces it implements. References are `vpl::Ref<T>`, an
intrusive smart pointer; non-null is enforced by VPL, not by `Ref`. The count starts at 1, owned by
the `new` expression, so a constructor that hands out and drops references to `this` cannot destroy
the object early.

### Cycles and `weak`

Reference cycles are never freed. Break them with `weak` references:

```java
class Node {
    List<Node> children;
    weak Node? parent;     // does not keep the parent alive
}
```

A `weak` reference must be nullable. It becomes `null` automatically when its target is
destroyed. To use it, test it: `if (parent is Node p) { ... }` (this produces a strong reference
for the block's duration).

- `weak` is allowed on class fields only (not struct fields, locals, or parameters), and a weak
  field cannot be passed by `ref`. Reading it gives a strong `T?`.
- A weak reference reads as `null` from the moment its target's count reaches zero, before the
  target's destructor runs.
- Implementation: each weak reference is linked into a list kept by its target, and the target
  clears the list when it dies. Memory is freed immediately; there is no separate weak count or
  control block. A weak field costs three pointers.

## 3.3 Pools and handles

`Pool<T>` stores many values of a **struct** type `T` and hands out `Handle<T>` values to refer to
them. Pools are the intended way to store entities and anything numerous that refers to other
things. **Classes cannot be stored in pools.**

```java
Pool<Enemy> enemies = new Pool<>();

Handle<Enemy> h = enemies.add(Enemy(...));
// a Handle is a plain value: store it in structs, lists, classes, save files

enemies.remove(h);
```

### Handles

A handle is two integers: `{ uint index; uint generation; }`. It is a value type, 8 bytes, and
freely copyable. `Handle.NONE` is a handle that never refers to anything.

Each pool slot stores its own `generation`. Removing an element increments the slot's
generation, so every existing handle to it becomes **stale**. A stale handle never refers to a
different element, even if the slot is later reused.

### Access

`get` returns a nullable reference, which must be checked:

```java
if (enemies.get(target) is ref Enemy e) {
    e.health -= 10;
} else {
    target = Handle.NONE;    // target is gone
}
```

| Method | Behavior |
|---|---|
| `get(h)` | `ref T?`; null if `h` is stale |
| `contains(h)` | `true` if `h` is live |
| `getUnchecked(h)` | No generation check (`@Unchecked` / `unsafe` only) |

Iterating a pool (`for (ref Enemy e : enemies)`) visits live elements only.

### Stable storage

A pool allocates storage in fixed-size chunks. Growing adds a new chunk; **existing elements never
move**. Adding to a pool therefore never invalidates references into it, even while it is being
iterated.

### Deferred reclamation

Removal happens in two steps:

1. **Logical removal (immediate):** the slot's generation is incremented and it is marked dead.
   All handles to it are stale right away: `get` returns null, `contains` returns false, iteration
   skips it.
2. **Physical removal (deferred):** if the pool is currently **borrowed** (some `ref` or `Span`
   into it is live), the slot is put on a pending list. Its value is not destroyed and the slot
   cannot be reused. When the pool's borrow count returns to zero, pending slots are destroyed
   and become reusable. If the pool is not borrowed, this happens immediately.

The result: **a reference always sees exactly the element it was taken from**, unchanged,
for as long as the reference exists. It never observes a destroyed or reset value, and never a
different element that reused the slot.

```java
for (Handle<Enemy> h, ref Enemy e : enemies) {
    if (e.health <= 0) {
        enemies.remove(h);          // handle is dead now
        e.dropLoot();               // still the same enemy, still valid
    }
}                                   // borrow ends: destroyed here
```

> ⚠️ **Destruction may be later than `remove()`.** When removing during a borrow (such as while
> iterating), the element is destroyed when the outermost borrow of the pool ends, not at the
> `remove` call. Memory for removed elements is held until then. Remove outside the borrow if
> immediate destruction matters.

### Destroying a pool

Destroying a pool while it is borrowed panics.

## 3.4 References

`ref T` is a reference to a value that lives somewhere else (a local, a struct field, an array
element, a pool slot). References avoid copying and allow mutation in place. An `in T` reference
is the same but read-only.

References are **second-class**:

| Allowed | Not allowed |
|---|---|
| Method parameters: `void damage(ref Enemy e)` | Struct or class fields |
| Local variables: `ref Transform t = player.transform;` | Return values, except from a method into its receiver (below) |
| Loop variables: `for (ref Enemy e : enemies)` | Capture by a closure that may outlive the call |
| Pattern bindings: `if (pool.get(h) is ref Enemy e)` | Storing in any container |

A `ref` or `in` variable must be initialized with the place it refers to: a variable, field, or
element, never a temporary value. It cannot refer to a class object itself (objects are already
references) or to a `weak` field. Assigning to it assigns to the place.

### Returning references

A method may return `ref T` or `in T`, using `return ref place;`. The returned reference
**borrows the method's receiver**, so it must point into the receiver's own storage: `this`, a
field or element of `this` (but not of another object a field refers to), a reference returned
by a method of `this`, or, in unsafe code, the target of a pointer. Indexers are the usual case:

```java
struct Mat4 {
    float[16] m;
    ref float this[int row, int col] { return ref m[row * 4 + col]; }
}
```

- Only instance methods return references, and for now they cannot be virtual or abstract.
- A `ref` return must point into mutable storage, so a `readonly` method returns `in` (or a
  pointer's target). Calling a method that returns `in` gives a read-only place.
- The result is a place for as long as the receiver is: on a temporary struct, such as
  `makeMatrix()[0, 0]`, it can be read but not assigned or referred to.
- Free functions and static methods cannot return references yet. (`Pool.get`, which returns a
  `ref T?`, will be a method.)

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

The current rule: the compiler pins (holds a strong reference for the duration of the call) the
receiver of a method call, and the object owning the place passed as a `ref`/`in` argument or used
as a struct method's receiver, unless that object is `this` or a local or parameter. Locals are
safe without a pin because nothing but the function itself can assign them, and a class-typed local
cannot even be passed by `ref`. `this` is safe because whoever called the method has pinned it. A
pin costs one count increment and decrement.

A `ref` or `in` variable, and a span variable, holds the object for its whole scope, even when a
local holds it: the function itself may reassign that local while the reference lives.

### Evaluation order and the heap

Operands are evaluated left to right ([06-syntax.md](06-syntax.md#evaluation-order)). Since any call
can modify any object, reading a field of an object counts as conflicting with every call in the
same expression: in `obj.x + f()`, `obj.x` is read before `f` runs. The same holds for reads
through `ref`/`in` parameters and a struct method's `this`, which may point into an object. In an
assignment, the object holding the target is evaluated before the value: in
`a.b.x = f()`, `a.b` is read before `f` runs. An element target is found, including its index and
bounds check, before the value: `list[i()] = f()` calls `i` before `f`, and `a[f()] += 1` calls
`f` once.

### Borrow counters on containers

Containers that can move or free their elements keep a **borrow count**. Taking a `ref` or `Span`
into the container increments it; the end of the reference's scope decrements it.

| Container | Operations that panic while borrowed |
|---|---|
| `List<T>` | `add`, `insert`, `removeAt`, `clear`, `reserve`: anything that changes its length or may reallocate |
| `Pool<T>` | Destruction only (`remove` is deferred, `add` never moves elements) |
| `T[]` | None (never reallocates; kept alive by pinning) |

```java
for (ref Bullet b : bullets) {
    if (b.hit) bullets.add(Bullet(...));       // panic: List modified while borrowed
}
```

The usual fix is to collect changes in a second list and apply them after the loop, or to use a
`Pool`, which supports adding and removing while iterating.

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

## 3.5 What the checks cost

| Operation | Checked | `@Unchecked` |
|---|---|---|
| Array/span index | 1 compare | 0 |
| `pool.get(h)` | 1 compare | 0 (`getUnchecked`) |
| Copy a class reference | refcount inc/dec | same (pass by `ref` to avoid) |
| Borrow from class field | refcount inc/dec, often elided | 0 |
| Borrow from container | counter inc/dec | 0 |
| Pool iteration | skip dead slots | same |
