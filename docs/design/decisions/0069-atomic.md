# 0069. `Atomic<T>` for integers, `AtomicBool`, and one memory order

- Status: accepted (2026-11-09)
- Spec: `concurrency.atomic`, `library.atomic`, `library.names`

## Decision

```java unchecked
pub shared class Atomic<T> {
    pub Atomic(T value);
    pub T get();
    pub void set(T value);
    pub T exchange(T value);
    pub T compareExchange(T expected, T value);
    pub T add(T delta);
    pub T subtract(T delta);
}
pub shared class AtomicBool { ... }    // the same, without add and subtract
```

- **The memory order.** Every operation is sequentially consistent, and no operation takes an
  order as an argument. The runtime uses `std::atomic`'s default, `memory_order_seq_cst`.
- **Which `T`.** `Atomic`'s type argument is one of the ten integer types. `StdlibSymbols.Register`
  marks the class (`TypeSymbol.IsAtomic`), and the binder reports `WYL0479` for any other type
  argument, a type parameter included, as it checks `Weak`'s type argument. A `bool` has a type of its
  own, `AtomicBool`, which has no arithmetic. A `char`, a `float`, and a `double` have no atomic.
- **`compareExchange`** returns the value the atomic held, and stores only if that value equals
  `expected`.
- **`add` and `subtract`** return the value before the change. They follow the language's
  arithmetic: a debug build panics on overflow and leaves the value unchanged, and a release build
  wraps. The runtime's debug build loops on `compare_exchange_weak` around `wyl::Add`, and its
  release build calls `fetch_add`.
- **The module.** Both types are declared in `std::threading` ([0066](0066-threading-module.md)), in
  `atomic.wyl`.
- **The storage.** Both types are shared classes whose one field is a `package extern struct
  AtomicState<T>`, which `StdlibSymbols.Register` marks as a sendable handle, as it marks
  `MutexState` ([0067](0067-mutex.md)). The C++ `wyl::AtomicState<T>` holds a pointer to a
  `wyl::threading::AtomicData<T>`, which holds the `std::atomic<T>`. The atomic object owns the data,
  and its destructor frees it. The interpreter changes the value under the data object's lock.
- **The in-use bit** ([0049](0049-getting-a-unique-value-out.md)) does not reach an atomic. An atomic
  holds an integer or a `bool`, which never carries the bit, and a shared class has no `mut` field.

## Alternatives

- **An order on each operation**, as Rust's atomics take an
  [`Ordering`](https://doc.rust-lang.org/std/sync/atomic/enum.Ordering.html). Weaker orders are
  faster on processors with weak memory models, but the spec would then have to say which operations
  pair with which, and a program that pairs them wrongly behaves differently from run to run. Go's
  atomics are all sequentially consistent ([The Go Memory Model](https://go.dev/ref/mem#atomic)), and
  so are Java's `get` and `set`, which have the memory effects of a `volatile` field
  ([`java.util.concurrent.atomic`](https://docs.oracle.com/en/java/javase/21/docs/api/java.base/java/util/concurrent/atomic/package-summary.html),
  [JLS 17.4.4](https://docs.oracle.com/javase/specs/jls/se21/html/jls-17.html#jls-17.4.4)). A weaker
  order can be added later as a method of its own.
- **Any sendable, copyable `T`.** A struct with a `String` field, or a reference to a shared object,
  holds a count, and swapping a counted value atomically needs a lock. `Mutex<T>` already provides
  one.
- **`float` and `double`.** `std::atomic`'s `compare_exchange_strong` compares the bits
  ([cppreference](https://en.cppreference.com/w/cpp/atomic/atomic/compare_exchange)), so `-0.0` and
  `0.0` would differ, and a NaN would equal itself. `compareExchange` would then disagree with `==`.
  Go's [`sync/atomic`](https://pkg.go.dev/sync/atomic) has no floating-point type either.
- **`Atomic<bool>`, with arithmetic limited by a `where` clause.** No bound tells an integer type from
  `bool`, so the clause would need a new interface, `Integer`, before the standard library's numeric
  interfaces are designed. A separate `AtomicBool` follows Java's
  [`AtomicBoolean`](https://docs.oracle.com/en/java/javase/21/docs/api/java.base/java/util/concurrent/atomic/AtomicBoolean.html)
  and Go's `atomic.Bool`.
- **An `add` that always wraps**, as Rust's
  [`fetch_add`](https://doc.rust-lang.org/std/sync/atomic/struct.AtomicI32.html#method.fetch_add)
  does. It would be the one `add` in the language that never panics on overflow in a debug build.
- **`compareExchange` returning a `bool`**, as Java's `compareAndSet` does. The value it found is what
  a retry loop needs next, and comparing it with `expected` gives the `bool`.
- **The `std::atomic` inside the object instead of behind a pointer.** The handle is passed by value
  to every runtime function, and a `std::atomic` cannot be copied. The pointer keeps the shape
  `MutexState` has, and moving the value into the object is an optimization no program can observe.

## Why

An atomic is the cheapest way for threads to share a counter or a flag, and its whole contract is
the order its operations take. One order for every operation lets the spec state that contract in
one sentence, and a program written against it gives the same answers on every processor. Limiting
`T` to the integer types keeps every operation one instruction on the hardware, and keeps
`compareExchange` equal to `==`.
