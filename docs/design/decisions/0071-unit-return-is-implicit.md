# 0071. A function that returns `Unit` may leave out its return

- Status: accepted (2026-11-09); amends [0042](0042-unit.md)
- Spec: `statements.return`, `generics.arguments.inference`, `functions.lambdas.typing`

## Decision

- If a function, method, or lambda returns `std`'s `Unit`, `return;` returns `Unit()`, and reaching
  the end of its body returns `Unit()`.
- A lambda that returns no value infers `Unit` for a type parameter that is its whole result type.
  With `T run<T>(fn() -> T f)`, `run(() -> { })` calls `run<Unit>`.
- A lambda whose function type returns `Unit` may have an expression body with no value, as in
  `fn() -> Unit f = () -> print("a");`.
- The rules apply to `std`'s `Unit` only. They do not apply to `Unit?` or to a program's own type
  named `Unit`. A program does not need to import `Unit` for a lambda to infer it.

## Alternatives

- **`void` as a type argument**: decision 0042 rejected it, because `List<void>` and
  `var x = log();` would become legal and both executors would need an empty value.
- **Inferring `Unit` for any result type that mentions the type parameter**: with
  `fn() -> List<T>`, a lambda that returns nothing would have to return an empty `List<Unit>`,
  which the program never wrote. The compiler keeps error WYL0205 for that case.
- **Leaving the rules out**: every thread body, `lock`, and `open` that only changes state then ends
  in `return Unit();`, which says nothing a reader needs.

## Why

The value of a `Unit` is always `Unit()`, so writing it adds no information. The binder adds the
return, so lowering, the verifier, and both executors see an ordinary `return Unit();`, and no later
layer has a second rule. `Result<Unit, E>` keeps `return Ok(Unit());`: its result type is not
`Unit`, and a missing `Ok` could hide a forgotten error path.
