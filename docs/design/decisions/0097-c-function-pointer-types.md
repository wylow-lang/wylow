# 0097. A C function pointer is an `extern fn` type, and only a `@CCallback` function converts to it

- Status: accepted (2026-11-12)
- Spec: `types.functions`, `conversions.functions`, `interop.blittable`, `interop.signatures`,
  `interop.callbacks`

## Decision

A C function pointer has its own function type, written with `extern` before `fn`:
`extern fn(void*, int) -> void`. The type is written wherever a type is written: in an extern
function's signature, in a local, in a field of a struct of an extern block, and in an extern
constant. It is blittable, and so is its nullable form `(extern fn(void*, int) -> void)?`, which
crosses as a function pointer that may be null. The non-null form holds no null.

- Only a reference to a static or top-level function marked `@CCallback` converts to an `extern fn`
  type, and only when its parameter types and return type are the type's. A lambda, a function
  value, and an instance method do not convert. An `extern fn` converts to no other type.
- The parameter and return types of an `extern fn` must be blittable, and its parameters have no
  `ref` or `in` mode. The parameters and the return type of a `@CCallback` function follow the same
  rules.
- An `extern fn` value captures nothing, so it has one form. It is never written `escaping`, it may
  be stored anywhere, and it is sendable.
- Calling an `extern fn` value is unsafe.
- An `extern fn` in an extern signature matches a C++ function pointer whose return type and
  parameter types match by the rules of the signature check.

The C++ type of `extern fn(P) -> R` is the function pointer `R (*)(P)`. The emitter calls each
`@CCallback` function through a `noexcept` function of that type, whose address is the pointer.
If an extern function's C++ parameter has a type that matches the `extern fn` type without being
it, such as `void (*)(void*, const char*)` for `extern fn(void*, byte*) -> void`, the call gives C++
a function of the parameter's own type, which converts the arguments and calls the callback.

## Alternatives

- **A named callback type, declared in an extern block, as a C `typedef` is.** A name would read
  well in a long signature, and it would match how C headers name their callback types. It would
  add a kind of declaration that exists only to name a type, and every other type in an extern
  block is written in place. A program that wants a short name can still declare a function that
  takes the type once.
- **An ordinary `fn` type means a C function pointer inside an extern block.** No new syntax is
  needed, but `fn(int) -> void` would then be a closure in one place and a code pointer in another,
  with different conversions, sizes, and rules for calls. A reader could not tell the type's meaning
  from the type.
- **The type always admits null, as `T*` does.** Calling one is already unsafe, so null would add
  no new undefined behavior at the call. But a C library that requires a non-null callback would
  then accept `null` from safe code with no sign in the type, and a function type in Wylow is
  non-null unless it is written with `?`.
- **A lambda that captures nothing converts too.** A lambda without captures could become a plain
  function, but whether a lambda captures is easy to change by accident, and the change would turn
  a working call into an error at a distance. A named function marks the boundary in one place.
- **C++ receives the callback's own function, cast to the parameter's type.** The two types have
  the same representation on every target the compiler supports, but C++ makes a call through a
  pointer of another function type undefined ([\[expr.call\]/6](https://eel.is/c++draft/expr.call#6)),
  and Clang's `-fsanitize=function` reports it. SQLite's `sqlite3_exec` takes a callback whose
  parameters are `char**`, which Wylow writes `byte**`, so the case is common.

## Why

C libraries take a function pointer and a `void*` context, and call the function later, possibly on
a thread the library made. A Wylow `fn` value is a closure: it carries a captured object, a count,
and code that expects them, so it cannot be the pointer. A separate type keeps that difference in
the type, where the binder checks it: the conversion accepts only functions that need no closure,
and the blittable rule keeps every value that crosses in the representation C expects. The
`noexcept` function between C and the callback keeps the Wylow function's own C++ signature free to
pass parameters as the emitter chooses.
