# 0111. A by-value parameter that may own an object is passed borrowed

- Status: accepted (2026-11-15)
- Spec: none; the decision changes how the compiler passes arguments, which no program can observe
  (`memory.release.temporaries`)

## Decision

- The caller holds every by-value argument as a temporary of its statement, and releases the
  temporary when the statement completes, as before (`memory.release.temporaries`). The callee's own
  copy of the argument is therefore never the value's last reference.
- `ParameterPassing.Borrows` decides which parameters are passed borrowed: a by-value parameter
  whose type is copyable, may own an object, and neither is nor contains a type parameter, of a
  function that is not extern, not a lambda, and not a function value.
- Lowering gives a borrowed parameter the `Borrowed` local kind, and passes the caller's temporary
  to it with a `borrow` read instead of a copy. The callee never releases a borrowed parameter. The
  verifier rejects a move of it and a store to it.
- The emitter declares a borrowed parameter as a `const` reference. `ReadsInPlace` passes the
  caller's place itself instead of the temporary when nothing changes the place until the temporary
  would be released, so a call such as `update(game)` changes no reference count.
- A borrowed parameter of a class reference type is a `const` reference to a `wyl::BorrowedRef`. A
  `BorrowedRef` derives from `wyl::Ref` and never changes the object's count. A caller that passes
  `this` makes one without a count change.
- An argument that converts a reference to one of its supertypes refers to the same object. For a
  parameter passed borrowed, lowering passes the reference before its conversion, and the verifier
  accepts a `borrow` argument whose type converts to the parameter's (`Conversions.IsReferenceUpcast`).
  The caller then makes the `BorrowedRef` of the parameter's type from the subclass's reference
  without a count change. C++ rejects a copy of a `BorrowedRef`, so a copy that
  could outlive the call fails to compile, and a copy into a `Ref` increments the count as usual.
- A `mut` parameter is passed borrowed too. The callee copies it into a variable of its own when it
  starts, and releases that variable when it returns.
- A lambda and a function value take their parameters by value, as before.

## Alternatives

- **The callee copies every by-value argument, as before.** Each call that passed a class reference
  incremented its count, and the callee decremented it again when it returned. The count changes
  were invisible, because the caller's temporary outlived the callee's copy, but they cost time on
  every call.
- **A borrowed parameter only where releasing its type runs no destructor.** That rule rested on a
  wrong premise: that the callee's copy could be the last reference. The caller's temporary always
  outlives it, so the rule would have slowed every class with a destructor for nothing.
- **A spec rule that the callee never releases a by-value parameter.** No program can tell whether
  the callee copies its argument, so the rule would describe an implementation, and the spec states
  only what a program can observe (decision 0001).
- **Function values with borrowed parameters.** A function type has one C++ form wherever it is
  written. The form of `fn(T)` in a generic function, instantiated with a class for `T`, must equal
  the form of `fn(Order)`, so the convention of a function type could not depend on whether a
  parameter type is a type parameter. Calls through function values keep their copies instead.
- **A `const` reference to a `wyl::Ref` for a class reference too.** A call that passed `this` or a
  subclass had to build a temporary `Ref` of the parameter's type. Building the temporary incremented the object's count, and destroying it
  decremented the count again.
- **Casting `this` to a `const wyl::Ref&`.** A `Ref` holds only a pointer, but reading a `T*` through
  a `Ref<T>` breaks the C++ aliasing rules ([basic.lval]/11), and the emitter never produces C++
  with undefined behavior.
- **A `BorrowedRef` that is not a `Ref`.** Such a type could have a trivial destructor, so a caller's
  temporary would need no cleanup when an exception unwinds the call. The runtime has many templates
  that take a `const Ref<T>&`, and C++ deduces `T` from an argument of a class derived from `Ref<T>`
  ([temp.deduct.call]/4.3), but not through a conversion. A separate type would need an overload of
  each of those templates.
- **Borrowed parameters whose type is a type parameter.** Lowering compiles a generic body once,
  and the body must work when the type argument is a unique struct, which it may move into storage.

## Why

The language already made a by-value argument a temporary of the caller's statement, so the callee's
copy of a counted value was a count increment and decrement with no effect. A borrowed parameter
removes that pair from every direct call, whatever the type's destructor does. When the argument is
a place that the call cannot change, such as a parameter of the caller or a local that no lambda
assigns, the caller's temporary goes too, and the call passes the place with no count change.
