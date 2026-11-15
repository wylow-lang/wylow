# 0110. Floating-point arithmetic of constants makes a constant static

- Status: accepted (2026-11-15); extends [0109](0109-constant-statics.md)
- Spec: `variables.statics`, `expressions.arithmetic.overflow`

## Decision

- A floating-point sum, difference, product, quotient, or negation of constants of one type is a
  constant if its result is finite. The compiler computes the result in the operands' own
  precision, so a `float` operation rounds to `float`.
- If the result is infinite or not a number, the static is not constant. Its initializer then
  computes the result at run time.
- A conversion between a floating-point type and an integer type is never folded.

## Alternatives

- **Folding integer arithmetic only, as 0109 did.** A static such as `THIRD = 1.0 / 3.0` would
  keep its storage and its ensure, while `COLUMNS = PAGES * 16` would not. The rule for which
  statics are constant would then depend on the type of the arithmetic for no reason that a reader
  can see.
- **Folding a result that is infinite or not a number.** The emitter writes a constant as a C++
  literal, and C++ has no literal for infinity or for a NaN. The emitter would need a second form
  for those values.
- **Folding conversions between floating-point and integer types.** A conversion from a
  floating-point value to an integer has range rules of its own, and the folder would have to
  reproduce them. The initializer already applies them at run time.

## Why

IEEE 754 defines each of `+`, `-`, `*`, and `/` as the exactly rounded result of the operation, so
each operation has one correct answer in each precision. C# computes a `float` operation in single
precision and a `double` operation in double precision, and the runtime requires IEEE 754
arithmetic (`runtime/wyl/core.h`). The C++ build turns on no option that relaxes floating-point
arithmetic, such as `-ffast-math` or `/fp:fast`. A folded result therefore has the bits that the
program would compute at run time. A floating-point quotient never panics, so the folder has no
panic to reproduce.
