<a id="unsafe"></a>
# 20. Unsafe code

<a id="unsafe.blocks"></a>
## 20.1 `unsafe` blocks and functions

```ebnf
UnsafeBlock = "unsafe" Block .
```

A block preceded by `unsafe` is an **unsafe block**. A function, method, or constructor declared
`unsafe` is an **unsafe function**. The code inside either is **unsafe code**; all other code is
safe code.

Unsafe code may use the operations this chapter describes. Code that would also be valid outside
unsafe code means the same inside it: indexing is checked, integer overflow is checked as the build
mode says, and borrows are counted.

- It is a compile-time error if safe code dereferences or indexes a pointer, does arithmetic on
  one, takes an address, casts a pointer, calls an unsafe function, or calls `beginBorrow` or
  `endBorrow` ([`Borrowable`](17-memory.md#memory.borrows.borrowable)).
- It is a compile-time error if a method declared `unsafe` overrides or implements a method that
  isn't. A method that overrides or implements an unsafe method may be safe.

A pointer type may be written wherever a type may, such as the type of a struct's field; only the
operations above need unsafe code.

**Example:**

```java unchecked
unsafe {
    Span<Point> view = points;
    Point* p = view.data();
    for (mut int i = 0; i < count; i++) {
        p[i].x = 0;
    }
}
```

<a id="unsafe.pointers"></a>
## 20.2 Pointers

A pointer type `T*` points to storage that holds a `T` ([Pointer types](04-types.md#types.pointers)).
Its default value is the null pointer, and `null` converts to every pointer type. A pointer type is
not nullable.

- `p[i]`, for a pointer `p` and an `int` `i`, is the place `i` elements after the one `p` points to;
  `p[0]` is the place `p` points to. The index is not checked, and may be negative.
- `p + n` and `p - n`, for an `int` `n`, are the pointers `n` elements after or before `p`. They are
  not checked.
- `&place` is a pointer to that place: a `T*` when the place holds a `T`.
- `(U*) p` converts a pointer to type `U*`. The pointer's value does not change; reading or writing
  through it is allowed only as [Storage and types](#unsafe.aliasing) says.
- Two pointers of one type compare with `==` and `!=` by address: they are equal when they point to
  the same element of the same storage. A pointer compares with `null`. No other operator applies
  to a pointer, and comparing needs no unsafe code.
- `bitcast<T>(x)` returns a value of type `T` whose bits are those of `x`. It is a compile-time
  error if `T` and the type of `x` have different sizes. The result is a value of `T` only when
  the bits are one of `T`'s valid representations, so `bitcast<Direction>(i)` converts a `@Repr`
  enum's value without the check a cast makes ([Integer representations](09-enums.md#enums.repr)).

It is undefined behavior to read or write through a pointer outside the storage it points into, or
after that storage is gone or has moved.

**Example:**

```java unchecked
unsafe {
    int[4] values = [10, 20, 30, 40];
    int* first = &values[0];
    int* third = first + 2;
    std::print(third[0]);               // 30
    third[0] = 35;
    std::print(values[2]);              // 35
    std::print(first == third);         // false
}
```

<a id="unsafe.aliasing"></a>
## 20.3 Storage and types

Storage holds values of one type. Reading or writing through a pointer of type `T*` is undefined
behavior unless the storage it points to holds a `T`: a variable of type `T`, or a field, variant
field, or element of type `T` inside a larger value. To read a value's bits as another type, use
`bitcast<T>(x)`.

<a id="unsafe.unchecked"></a>
## 20.4 Unchecked operations

The standard library's unchecked operations are unsafe functions, such as `getUnchecked(i)` on
arrays, spans, and lists, which returns an element without checking its index. Calling one with
arguments its documentation forbids is undefined behavior.

<a id="unsafe.threads"></a>
## 20.5 Sharing across threads

Through pointers, unsafe code may give data to another thread without the rules of
[Concurrency](19-concurrency.md#concurrency). It is undefined behavior for two threads to access
the same storage at the same time, with at least one of them writing, unless a synchronization type
orders the accesses.

<a id="unsafe.undefined"></a>
## 20.6 Undefined behavior

Unsafe code has undefined behavior ([Undefined behavior](01-introduction.md#introduction.undefined))
if it breaks a rule this chapter states as undefined behavior, or the conditions the `# Safety`
section of an unsafe function's documentation gives for calling it.
