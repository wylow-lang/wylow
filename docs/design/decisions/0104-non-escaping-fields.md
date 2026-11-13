# 0104. A `ref struct` may hold a non-escaping function, and a pointer may point to a `ref struct`

- Status: accepted (2026-11-12)
- Spec: `structs.ref`, `functions.lambdas.escaping`, `memory.second-class`, `types.pointers`,
  `unsafe.pointers`

## Decision

- A field of a `ref struct` may have a function type. The field is non-escaping unless it is
  declared `escaping`, as a parameter or a local is.
- A non-escaping field has no initializer. The field is read-only outside the constructors of its
  `ref struct`. In a constructor, the field is assigned only from a parameter of that constructor.
- An argument of a non-escaping function type to a constructor of a `ref struct` must be a
  parameter or a local variable. A lambda or a function name written in place of the argument is an
  error, and the help says to declare it in a local first.
- A place of a `ref struct` type is never assigned after it is made. The rule already held for a
  `ref struct` variable, and it now holds for a place that a pointer reaches.
- A pointer may point to a `ref struct`, and unsafe code may take the address of a `mut` local or
  parameter of a `ref struct` type.
- Span and reference fields stay errors. A later decision may allow them.

## Alternatives

- **An unsafe conversion of a non-escaping function to `void*` and back.** A plain struct would
  hold the `void*`, and the callback would cast it back to a function type. The function's type
  would be lost in C, and a cast back to another function type would be undefined behavior that the
  compiler cannot find. With a `ref struct` field, the function keeps its type through the pointer.
- **Span fields as well.** `ref struct Parser { ReadOnlySpan<byte> input; int position; }` would be
  useful, but the flow analysis would have to treat a `ref struct` as holding and borrowing what
  each of its span fields views. A non-escaping function needs no hold, because the parameter or
  local that it comes from lives for the whole scope of the `ref struct`. The user and the assistant
  chose to allow only functions in this decision.
- **Escaping by default.** A function field is escaping everywhere else. A `ref struct` is
  second-class, as a parameter and a local are, so its fields follow their rule. A function field
  of a `ref struct` was an error before this decision, so no program changes meaning.
- **A lambda written in place of a constructor argument.** The lambda would be a temporary that is
  gone after its statement, while the `ref struct` may live longer. Lowering could keep such a
  lambda alive for the scope of the variable, as a view holds a temporary object. The rule asks for
  a local instead, and a later decision can lift it without breaking a program.

## Why

A C API that calls back during one call, such as `sqlite3_exec` or `qsort_r`, takes a function
pointer and a `void*` context. The context reaches the callback, and the callback calls the
program's function through it. Before this decision, a field held only an escaping function, so a
binding of such an API took an escaping function. An escaping lambda copies the locals that it uses,
and it cannot change them, so a caller that counts rows had to count them in an object.

The pointer to the context already makes the programmer responsible for C not keeping it after the
call (spec `unsafe.pointers`). A non-escaping function inside the context needs no further promise.
Safe code cannot store or return a `ref struct`, so the function cannot outlive the parameter or
local that it came from. The field is read-only, so nothing makes the struct hold another function.
