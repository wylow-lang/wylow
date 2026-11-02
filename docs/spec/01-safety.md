# 1. Safety

## 1.1 The rule

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
int32_t x = wyl::add(wyl::at(arr, i), y);   // checked index, defined overflow
```

## 1.2 How each source of C++ UB is handled

| C++ UB | Wylow behavior |
|---|---|
| Out-of-bounds index | Checked; panics |
| Signed integer overflow | Panics in debug builds, wraps in release builds (defined either way) |
| Division by zero, `INT_MIN / -1` | Checked; panics |
| Shift by ≥ bit width | Shift amount is masked (`x << (n & 31)` for 32-bit) |
| Null dereference | Types are non-null by default; nullable types must be checked. `x!` panics on null |
| Uninitialized variables | Compile error to read before assignment; fields are default-initialized |
| Use-after-free / dangling pointer | Prevented by the memory model ([03-memory-model.md](03-memory-model.md)) |
| Iterator / reference invalidation | Prevented by borrow counters ([03-memory-model.md](03-memory-model.md#33-references)) |
| Float → int out of range | Saturates; NaN → 0 |
| Unsequenced evaluation | Evaluation order is always left to right; codegen emits temporaries as needed |
| Strict aliasing / type punning | Only via `bitcast<T>(x)` (emitted as `std::bit_cast`/`memcpy`). Storage of one type is never read or written as another, which the compiler relies on (§1.4, `unsafe`) |
| Data races | Prevented by the concurrency model ([05-concurrency.md](05-concurrency.md)) |
| Stack overflow | OS guard page; process aborts |

## 1.3 Panics

A **panic** is a failed safety check or an explicit `panic("message")` call.

A panic means the code has a bug, so the state it was changing can't be trusted. It therefore
**aborts the process**, on any thread ([05-concurrency.md](05-concurrency.md#57-panics)):

- The runtime prints the message and a stack trace where the panic happens, and a debugger stops
  there.
- Nothing unwinds: no destructor runs and no thread continues, so no code can see the state the
  panicking code left half-changed. There is no way to catch a panic, by type or otherwise.
- Stack overflow and running out of memory abort the process the same way.

> *Open:* recovering from a panic in code that is kept apart from the rest (a mod, a script, an
> editor's play mode) could later be an opt-in that changes no program that doesn't use it. Until
> then, such code can run in a separate process.

Panics are not exceptions. Expected failures (a missing file, bad input, a lookup that may find
nothing) are values: `T?` or `Result<T, E>`
([04-errors.md](04-errors.md#45-panicking-and-non-panicking-versions)), and the standard library has a
non-panicking version of every operation whose failure the caller may not control.

## 1.4 Opting out

### `unsafe`

An `unsafe` block (or `unsafe` method) *permits* operations that safe code can't use. It never
changes what code that would also compile outside it means: `a[i]` is bounds-checked, overflow
panics in debug builds, and borrows are counted, inside `unsafe` as anywhere else (as in Rust and
C#). Unsafe code may use:

- Raw pointers `T*`, pointer arithmetic, address-of.
- Pointer casts. Reading or writing storage through a pointer of another type than the storage's is
  still undefined behavior, as in C++ (strict aliasing): reinterpreting a value is `bitcast<T>(x)`'s
  job. The compiler relies on this rule: a write through a reference or pointer of type `T` changes
  only storage that holds a `T`, directly or inside a struct, enum, or fixed array. So does the C++
  compiler that builds the output.
- Sharing data across threads through raw pointers, without the checks that make other values
  sendable ([05-concurrency.md](05-concurrency.md#58-unsafe)).
- Unchecked operations, which are named methods: `getUnchecked(i)` on arrays, spans, and `List`
  returns the element without the bounds check. Arithmetic that may overflow uses the `wrapping`
  operations ([02-types.md](02-types.md)), which are safe.

```java
unsafe {
    Vertex* v = vertices.data();
    for (mut int i = 0; i < count; i++) {
        v[i].position = v[i].position.scale(scale);
    }
}
```

Inside `unsafe` code, violating the rules is undefined behavior and is the
programmer's responsibility, as in C#.

An `unsafe` method or constructor may only be called from unsafe code. Since a call through a method
that isn't `unsafe` needs no unsafe code, an override or interface implementation can be `unsafe`
only if what it overrides is (as in Rust); a safe override of an `unsafe` method is allowed. Pointer types may be written
anywhere (a struct may have a `T*` field); only using a pointer needs unsafe code.

> Implemented so far: `unsafe` blocks, methods, and constructors; pointer types `T*`, which default
> to `null` and compare with `==`; `p[i]`, an unchecked place; and `p + n` and `p - n`. There is no
> address-of, no pointer casts, and no `getUnchecked` yet. Memory comes from the standard library's `std::memory`: `allocate<T>(count)`
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

> Not implemented yet: the warning, its flag, and `@AllowLargeStack`.
