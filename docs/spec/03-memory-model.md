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
element, a pool slot). References avoid copying and allow mutation in place.

References are **second-class**:

| Allowed | Not allowed |
|---|---|
| Method parameters: `void damage(ref Enemy e)` | Struct or class fields |
| Local variables: `ref Transform t = player.transform;` | Return values (see note) |
| Loop variables: `for (ref Enemy e : enemies)` | Capture by a closure that may outlive the call |
| Pattern bindings: `if (pool.get(h) is ref Enemy e)` | Storing in any container |

> Note: a function may return a `ref` derived from one of its `ref` parameters (this is how
> `Pool.get` works). The exact rule is *open*.

Because a reference cannot outlive the function that created it, the only remaining danger is the
storage it points into being freed or moved while the function runs. Two runtime mechanisms
prevent that.

### Pinning class objects

Taking a reference into a field of a class object holds a strong reference to that object for the
reference's scope, so the object cannot be destroyed while referenced. The compiler omits this
when it can prove it unnecessary (for example, the object is held by a local that is never
reassigned).

### Borrow counters on containers

Containers that can move or free their elements keep a **borrow count**. Taking a `ref` or `Span`
into the container increments it; the end of the reference's scope decrements it.

| Container | Operations that panic while borrowed |
|---|---|
| `List<T>` | `add`, `insert`, `remove`, `clear`, anything that may reallocate; destruction |
| `Pool<T>` | Destruction only (`remove` is deferred, `add` never moves elements) |
| `T[]` | None (never reallocates; kept alive by pinning) |

```java
for (ref Bullet b : bullets) {
    if (b.hit) bullets.add(Bullet(...));       // panic: List modified while borrowed
}
```

The usual fix is to collect changes in a second list and apply them after the loop, or to use a
`Pool`, which supports adding and removing while iterating.

### Spans

`Span<T>` and `ReadOnlySpan<T>` follow exactly the same rules as `ref`: second-class, with the
same borrowing and pinning behavior.

## 3.5 What the checks cost

| Operation | Checked | `@Unchecked` |
|---|---|---|
| Array/span index | 1 compare | 0 |
| `pool.get(h)` | 1 compare | 0 (`getUnchecked`) |
| Copy a class reference | refcount inc/dec | same (pass by `ref` to avoid) |
| Borrow from class field | refcount inc/dec, often elided | 0 |
| Borrow from container | counter inc/dec | 0 |
| Pool iteration | skip dead slots | same |
