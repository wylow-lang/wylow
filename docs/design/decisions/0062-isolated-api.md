# 0062. The `Isolated<T>` API: one `open`, `Unit` for nothing, and a by-value `take`

- Status: accepted (2026-11-06)
- Spec: `concurrency.isolated`, `library.isolated`, `generics.arguments.inference`

## Decision

```java unchecked
pub unique struct Isolated<T> {
    pub Isolated(shared fn() -> T build);
    pub mut R open<R extends sendable>(shared fn(ref T) -> R body);
    pub T take(this Isolated<T> box);
}
```

- The constructor's lambda is non-escaping and shared, so its captures are sendable and copyable and
  nothing of the thread's comes in. It runs immediately, on the current thread.
- `open` is a `mut` method, since it changes the value the box holds, and it takes a non-escaping
  shared lambda whose one parameter is a reference to that value.
- `R` must be sendable. A lambda that only changes the graph returns `Unit`
  ([0042](0042-unit.md)), which is sendable. A lambda that returns nothing cannot give `R` a type,
  because a lambda that returns no value infers nothing
  ([Inferring type arguments](../../spec/11-generics.md#generics.arguments.inference)).
- `take` takes its receiver by value, so a second `take` uses a moved value, which the flow analysis
  rejects ([0050](0050-by-value-receivers.md)).

## Alternatives

- **A second `open` for `void`.** The binder types a lambda once, for the candidate left after the
  other arguments are checked, and it refuses a call that more than one overload still fits. A
  pending lambda fits any function type with the same number of parameters, whatever its return
  type, so two `open`s that differ only in the lambda's return type leave two candidates and no call
  (`WYL0206`, "other overloads also take these arguments").
- **`R` inferred as `void`, with `open` returning nothing.** The language refuses a type argument a
  lambda returning no value would give, and `errors/lambda_inference.wyl` pins that rule for a
  function whose type parameter is its return type.
- **A default type argument, `open<R = Unit>`.** The language has no default type arguments, and
  inference reports a lambda that returns nothing rather than leaving `R` to a default.
- **`take` on a `mut` receiver.** A by-value receiver cannot be `mut`, and a receiver by reference
  would let a second `take` reach an ended box.
- **A non-`mut` `open`.** The lambda changes the value, and the language demands a mutable place for
  that.

## Why

One `open` is the only form the language can resolve, and `Unit` is the language's type for an
operation whose success has nothing to return, so a lambda that only changes the graph returns it.
The by-value `take` makes "the box is ended at most once" a compile-time property with the language's
existing move rules, as `join` and `Result`'s methods do.
