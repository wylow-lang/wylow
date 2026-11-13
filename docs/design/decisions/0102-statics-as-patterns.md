# 0102. A static whose initializer is a literal is a pattern, and an extern static may give its value

- Status: accepted (2026-11-12)
- Spec: `patterns.kinds`, `variables.statics`, `interop.extern.constants`

## Decision

- A `case` or an `is` pattern may name a static. The static must have an integer type, `char`, or
  `bool`, and its initializer must be a literal: an integer literal, which may be negated, a `char`
  literal, `true`, or `false`. The pattern matches a value equal to the static's value, as the
  literal itself would.
- A lone name in a pattern is a variant if the matched value is an enum. Otherwise the name is a
  static if a static of that name is visible as a value, and otherwise a type. `Type.NAME` names a
  static of `Type` when the matched value is not an enum.
- A static of an extern block may give its value, as in `static int SQLITE_ROW = 100;`. The value
  must be a literal of the static's type, and the static must have an integer type, `char`, or
  `bool`. The emitter checks with a `static_assert` that the C++ constant equals the value, and the
  build reports a failed assertion at the declaration, as it reports a signature that does not
  match C++.
- It is a compile-time error for a pattern to name a static whose initializer is not a literal, a
  static of another type, or an extern static that does not give its value. The note says which
  of the three applies.

## Alternatives

- **Any static as a pattern, compared at run time.** The compiler could not report two cases of one
  value, and a `case` would no longer name a value known at compile time. The user and the
  assistant rejected this alternative when they agreed on the design.
- **A constant expression as the initializer.** The spec's constant expressions include operators,
  so `static int READ_ERROR = 10 | (1 << 8);` would be a pattern. The binder would then evaluate
  integer, `char`, and `bool` operators at compile time, and its overflow, shift, and division
  rules would have to agree with the runtime's and the interpreter's. A literal needs no
  evaluation. An enum variant's value is also a literal (spec `enums.repr`), and a later decision
  can widen both rules together without breaking a program.
- **An extern static whose value is any constant expression.** The same evaluator would be needed,
  and C's own headers already state each value as a number or as an expression that a binding can
  copy as the number it gives.
- **A static without an initializer as a pattern of its default value.** The default value of a
  static is known, but a reader of the `case` would have to know that the declaration leaves out
  its value on purpose. The rule asks for the value to be written.

## Why

A pattern names a value that the compiler knows, so that the compiler can report two cases of one
value and the emitter can write a C++ `switch`. A static whose initializer is a literal names such
a value, and it gives the value a name that a binding of a C API wants, such as `SQLITE_ROW`.

An extern static declares a C++ constant, and today the program reads the value only when it runs.
If the declaration also gives the value, the compiler knows it. A read of the static still gets its
value from C++, and a pattern gets its value from the declaration. The `static_assert` keeps the two
values equal, because the build fails at the declaration if the header gives another value.
