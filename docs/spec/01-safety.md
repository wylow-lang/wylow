# 1. Safety

## 1.1 The rule

**Code generated from safe VPL never contains undefined behavior.** The compiler performs all
safety checking in its own semantic analysis; the generated C++ is never "trusted" to be correct
on its own. Operations that are UB in C++ are emitted through small inline helpers with defined
behavior, which compile down to a compare and a predictable branch (or nothing).

```java
// VPL
int x = arr[i] + y;
```
```cpp
// generated C++ (illustrative)
int32_t x = vpl::add(vpl::at(arr, i), y);   // checked index, defined overflow
```

## 1.2 How each source of C++ UB is handled

| C++ UB | VPL behavior |
|---|---|
| Out-of-bounds index | Checked; panics |
| Signed integer overflow | Panics in debug builds, wraps in release builds (defined either way) |
| Division by zero, `INT_MIN / -1` | Checked; panics |
| Shift by ≥ bit width | Shift amount is masked (`x << (n & 31)` for 32-bit) |
| Null dereference | Types are non-null by default; nullable types must be checked. `x!` panics on null |
| Uninitialized variables | Compile error to read before assignment; fields are default-initialized |
| Use-after-free / dangling pointer | Prevented by the memory model ([03-memory-model.md](03-memory-model.md)) |
| Iterator / reference invalidation | Prevented by borrow counters ([03-memory-model.md](03-memory-model.md#34-references)) |
| Float → int out of range | Saturates; NaN → 0 |
| Unsequenced evaluation | Evaluation order is always left to right; codegen emits temporaries as needed |
| Strict aliasing / type punning | Only via `bitcast<T>(x)` (emitted as `std::bit_cast`/`memcpy`) or `unsafe` |
| Data races | Prevented by the concurrency model ([05-concurrency.md](05-concurrency.md)) |
| Stack overflow | OS guard page; process aborts (same as a panic) |

## 1.3 Panics

A **panic** is a failed safety check or an explicit `panic("message")` call.

- A panic **always aborts the process**, in every build configuration, on every thread.
- Before aborting, the runtime prints the message and a stack trace.
- Panics cannot be caught or recovered from. Recoverable failures use `Result<T, E>`
  ([04-errors.md](04-errors.md)).

## 1.4 Opting out

### `@Unchecked`

Removes **runtime checks** from a function while keeping the language's type rules. Inside an
`@Unchecked` function:

- Array/span indexing is not bounds-checked.
- Integer overflow is not checked in debug builds (it still wraps).
- Division is not checked for zero.
- Borrow counters and class pins are not maintained.

```java
@Unchecked
void skinMesh(ref Mesh m, Span<Mat4> bones) { ... }
```

Pools additionally offer unchecked accessors, e.g. `pool.getUnchecked(handle)`, which skip the
generation check.

### `unsafe`

An `unsafe` block (or `unsafe` method) enables everything `@Unchecked` does, plus:

- Raw pointers `T*`, pointer arithmetic, address-of.
- Arbitrary pointer casts and reinterpretation.
- Sharing data across threads without `Sendable` checks.

```java
unsafe {
    Vertex* v = vertices.data();
    for (int i = 0; i < count; i++) {
        v[i].position *= scale;
    }
}
```

Inside `unsafe` and `@Unchecked` code, violating the rules is undefined behavior and is the
programmer's responsibility, as in C#.

An `unsafe` method or constructor may only be called from unsafe code. Pointer types may be written
anywhere (a struct may have a `T*` field); only using a pointer needs unsafe code.

> Implemented so far: `unsafe` blocks, methods, and constructors; pointer types `T*`, which default
> to `null` and compare with `==`; `p[i]`, an unchecked place; and `p + n` and `p - n`. There is no
> address-of and no pointer casts yet, and unsafe code still has its runtime checks (`@Unchecked` is
> not implemented). Memory comes from the standard library's `Memory`: `allocate<T>(count)`
> (uninitialized), `free`, `initialize(place, value)`, `initializeDefault(data, count)`,
> `drop(place)`, and `moveTo(from, to, count)`, which `Array` and `List` are written with.

## 1.5 Large stack objects

Very large locals (e.g. `float[1_000_000]`) can overflow the stack. The compiler **warns** when a
single local exceeds a threshold:

- Default threshold: 64 KB.
- Configurable with a compiler flag (exact flag name *open*).
- Suppressible per declaration with `@AllowLargeStack`.

Large data should go on the heap: in a class field, a `T[]`, or a `Box<T>`
([02-types.md](02-types.md#211-arrays-and-spans)).
