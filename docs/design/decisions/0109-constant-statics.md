# 0109. A static whose value the compiler can compute is a constant

- Status: accepted (2026-11-15); amends [0053](0053-statics-initialization-and-storage.md) for
  constant statics
- Spec: `variables.statics`

## Decision

- Lowering decides which statics are **constant statics**. A constant static is one whose value
  the compiler computes without running any of the program's code.
- A literal of a scalar type is a constant. The scalar types are the integer types, `float`,
  `double`, `bool`, and `char`.
- A read of a constant static is a constant of the static's type.
- An integer sum, difference, product, negation, or conversion of integer constants is a constant
  if its exact result is in the range of its type on every target.
- An array literal of a fixed-array type is a constant if every element is a scalar constant.
- A static without an initializer is constant if its type is scalar or a fixed array of a scalar
  type.
- If an integer result is outside the range of its type, the static is not constant. Its
  initializer then computes the result at run time, so the result panics in a debug build and
  wraps in a release build, as `expressions.arithmetic.overflow` says.
- `IrStatic.Constant` holds the value of a constant static. A constant static has no initializer
  function and no ensure. Lowering puts no ensure call before a read of one, so the IR is the same
  with `--no-opt`.
- The emitter declares a constant static as a `static constexpr` variable of its C++ type,
  initialized with its value. The emitter writes no `wyl::StaticStorage`, initializer, or ensure for
  it, and `wyl_init_statics` does not mention it.
- The interpreter gives every constant static its value before it runs any initializer.
- The runtime keeps `wyl::StaticBegin` and `wyl::StaticEnd` out of line, so that the ensure of each
  static that is not constant stays small.

## Alternatives

- **Every static in `wyl::StaticStorage`, as before.** An ensure stores the value at run time, so
  the C++ compiler cannot fold a read of the static. A read of a static inside a loop stays a load
  from memory, and every static costs an initializer and an ensure.
- **Literals and reads of constant statics only, with no arithmetic.** That rule is simpler, but a
  static such as `COLUMNS = PAGES * 16` would keep its storage and its ensure. The compiler folds
  only integer `+`, `-`, `*`, negation, and conversion, whose exact result has one definition. If
  the exact result does not fit, the compiler leaves the operation to run time, where the build mode
  decides the result.
- **The C++ compiler evaluating the arithmetic in a `constexpr` initializer.** If a product
  overflowed, the C++ compile would fail instead of the program panicking. A C++ compile failure is
  a compiler bug (`CLAUDE.md`).
- **The emitter deciding which statics are constant.** The interpreter would then run an ensure that
  the emitted C++ does not run, and the two executors would read different IR. Lowering decides
  once, and both executors read `IrStatic.Constant`.
- **Lowering a read of a scalar constant static to its literal.** A reference such as
  `in int columns = Limits.COLUMNS` needs a place, so the static keeps its storage in either case.
  The C++ compiler folds a read of a `constexpr` variable without that help.
- **Folding floating-point arithmetic and division.** If the compiler folded a floating-point
  operation, the result would have to round exactly as the C++ build rounds it. If it folded a
  division, it would have to reproduce the panic on a zero divisor. The compiler leaves both
  operations to the initializer, which computes them as it computes any other expression.

## Why

The initializer of a constant static runs no code, cannot panic, and cannot be part of a cycle, so
no program can observe when the static gets its value. The order of initialization in
`variables.statics` therefore holds for every constant static, whatever moment an executor gives
the static its value. A `constexpr` variable lets the C++ compiler fold each read into the code that
uses it and put a table in read-only data. In one measured program of about 8,000 lines, every one of
its 245 statics was constant, and their storage, initializers, and ensures made up about a third of
the generated C++.
