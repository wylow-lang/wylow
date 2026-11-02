# 0020. How lambdas are typed

- Status: accepted (2026-10-21)
- Spec: `functions.lambdas.typing`

## Decision

A lambda takes its type from where it is used. To revisit once real programs show whether they get
in the way:

- **Untyped lambdas are ambiguous between overloads** that differ only in function parameter
  types, rather than each overload being tried.
- **Parameter modes are part of a function type**, and an untyped lambda takes them from its
  target; function-typed parameters can't be `ref` or `in`.
- **Expression-bodied `void` lambdas** must have a call as their body, as statements must.
- **Inferring from a lambda's body needs one overload left.** Two generic overloads whose lambdas
  take the same number of parameters, or a generic and a non-generic one, make `x.map(m -> m.name)`
  an error asking for type arguments. Binding the body once per distinct parameter-type list, and
  sharing it, would lift that.

## Alternatives

- **Trying each overload** with the lambda's body, and keeping the ones that bind.

## Why

Each rule is simple and conservative: a call either has one meaning a reader can find, or is an error.
