<a id="unsafe"></a>
# 20. Unsafe code

<a id="unsafe.blocks"></a>
## 20.1 `unsafe` blocks and functions

A block preceded by `unsafe` is an **unsafe block**. A function, method, or constructor declared
`unsafe` is an **unsafe function**. The code inside either is **unsafe code**; all other code is
safe code.

Unsafe code may use the operations this chapter describes. Code that would also be valid outside
unsafe code means the same inside it: indexing is checked, integer overflow is checked as the build
mode says, and borrows are counted.

- It is a compile-time error if safe code dereferences or indexes a pointer, does arithmetic on
  one, takes an address, casts a pointer, or calls an unsafe function.
- It is a compile-time error if a method declared `unsafe` overrides or implements a method that
  isn't. A method that overrides or implements an unsafe method may be safe.

A pointer type may be written wherever a type may, such as the type of a struct's field; only the
operations above need unsafe code.

**Example:**

```java
unsafe {
    Point* p = points.data();
    for (mut int i = 0; i < count; i++) {
        p[i].x = 0;
    }
}
```

<a id="unsafe.pointers"></a>
## 20.2 Pointers

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
[Concurrency](19-concurrency.md#concurrency). Two threads accessing the same storage at the same
time, at least one of them writing, without a synchronization type ordering the accesses, is
undefined behavior.

<a id="unsafe.undefined"></a>
## 20.6 Undefined behavior

Unsafe code has undefined behavior ([Undefined behavior](01-introduction.md#introduction.undefined))
if it breaks a rule this chapter states as undefined behavior, or the conditions the `# Safety`
section of an unsafe function's documentation gives for calling it.
