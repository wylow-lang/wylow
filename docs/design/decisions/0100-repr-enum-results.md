# 0100. An extern function may return a `@Repr` enum, and the call panics on an unknown value

- Status: accepted (2026-11-12)
- Spec: `interop.signatures`, `interop.blittable`

## Decision

- An extern function may have a `@Repr` enum as its return type. C++ returns the enum's integer
  type, or a C++ enumeration of the same size and signedness.
- The call converts the returned integer to the enum with the check of a cast. If no variant of the
  enum has the integer's value, the call panics at the call site.
- A `@Repr` enum is still not the type of an extern constant, of a field of a struct or union in an
  extern block, or of a parameter or the result of a `@CCallback` function. Decision
  [0085](0085-c-structs-unions-and-enums.md) excluded the constant and the field, and this decision
  keeps them excluded.

## Alternatives

- **Parameters only.** Decision 0085 allowed a `@Repr` enum only as a parameter. A binding then
  declares the integer type for every function that returns a code, and every caller writes the
  cast. The SQLite binding in `examples/sqlite` declared its result codes as `int` constants for
  this reason.
- **A nullable result that is null on an unknown value.** If the return type were `E?`, a call would
  give null where `E.tryFrom` gives null. Every caller would then handle null, even for a function
  whose set of results is closed. A binding that expects values the enum does not list can still
  declare the integer type and convert the value itself.
- **Undefined behavior on an unknown value.** An extern call is unsafe, so the program could promise
  that C++ returns only listed values. A library that adds a result code in a newer version would
  then break a correct binding silently. The check costs one comparison per variant at a call, and
  decision 0085 already judged a check on a returned value to be cheap.
- **Checked fields and constants as well.** A field of a struct in an extern block is memory that
  C++ code writes at any time. A check would run on every read of the field, and a pointer to the
  field would bypass it. Decision 0085 gives the reasons in full.

## Why

A returned value crosses the boundary at one point, the call, so the compiler can check it there
once. The check is the check of a cast, so a program that receives an unknown value panics with the
message that `(E)i` gives. Lowering places the check after the call, and the emitter only
translates it.
