# 0084. Extern blocks are the only extern declaration, and the standard library uses them too

- Status: accepted (2026-11-10)
- Spec: `interop.extern`, `interop.extern.constants`, `interop.extern.extern-structs`,
  `interop.extern.names`, `interop.extern.std`, `interop.safety`, `interop.signatures`,
  `functions.declarations`, `functions.methods`, `structs.declarations`, `annotations.list`

## Decision

Every extern declaration is a member of an `@Include("path") extern { ... }` block, in the standard
library and in every other package. The `extern` modifier on a single function, method, or struct is
removed. Inside a block, a function or method without a body is provided by C++, and a method with a
body is Wylow code.

- `@CppNamespace("name")` on a block puts its members' C++ names in that namespace. A member's C++
  name is otherwise its own name, unless `@CppName` gives another. `@CppName` also applies to a field
  of a struct or union in a block.
- `@Safe` and `@Pure` apply to any function or method without a body in a block. `@Pure` is the
  programmer's promise that the function neither panics nor changes anything. Breaking that promise
  is undefined behavior, as breaking the promise of `@Safe` is.
- Every package may declare extern blocks. The standard library's blocks may also declare generic
  members, use types that are not blittable in signatures, declare structs backed by a C++ class or
  named by a primitive type's keyword, and mark a member without a body `@CallerLocation`. The
  compiler decides whether a block may do these things in one place, from whether the block is in
  `std`.
- The compiler compares each extern function's signature with the C++ function's signature. Two
  integer types match when they have the same size and signedness. Two pointers match when they
  differ only in `const` or in which one-byte type they point to. An integer type matches a C++
  enumeration of its size and signedness. The compiler converts an argument or a result between two
  matching types that are not the same type.
- A function that takes a variable number of arguments cannot be declared. A native shim wraps one.

## Alternatives

- **Keep the standard library's `extern` modifier beside the blocks.** The language would have two
  forms of one thing. The binder would also keep deriving C++ names such as `wyl::memory::allocate`
  from a module path (`Binder.ExternName`), and that rule exists only for the standard library.
- **Open generic extern declarations to every package.** A generic extern would instantiate a C++
  template with the compiler's own names for Wylow types, such as `s_Point`, and a C++ library cannot
  use those names. A native shim binds a template instead.
- **Add `const` pointer types to the language.** Pointers are already unsafe, so `const` would add
  types without adding a check. The signature check accepts a difference in `const` instead, and the
  spec makes writing through the pointer that `cstr()` returns undefined behavior.
- **Trust each declaration without comparing it with the C++ function.** C++ converts between
  integer types implicitly. If a program declared `int` for a parameter that C++ declares as a
  64-bit integer, the call would compile and silently truncate the value. The layout check catches
  the same kind of mistake in a struct, and the signature check is its counterpart for a function.
- **Keep `@Pure` internal to the standard library.** The promise is of the same kind as the promise
  of `@Safe`, and a binding gains the same freedom of evaluation order from it as the standard
  library does.

## Why

The standard library is a package like any other, except where it implements the language itself:
generics over the runtime's templates, `String`, `ref` parameters, and the primitive types. If the
standard library writes its declarations in the same blocks as every other package, that exception
is the only difference, and the compiler decides it once. The C++ names come from the declarations,
so the runtime's names and the binder's rules no longer have to agree on a derivation.

A signature check needs the C++ declaration, and the compiler never parses a header. The generated
C++ includes the header, so the C++ compiler can compare the types with `static_assert`, as the
layout checks already do. The check compares integers by size and signedness, not by C++ type. One
declaration then serves every platform where a C++ type such as `long` has the declared size.
