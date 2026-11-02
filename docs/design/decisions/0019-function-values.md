# 0019. Non-escaping functions are second-class values

- Status: accepted (2026-10-21)
- Spec: `functions.lambdas.escaping`, `memory.second-class`

## Decision

A function-typed parameter or local is non-escaping unless declared `escaping`. A non-escaping
function is bound once, can't be reassigned, and can't be stored. A nullable function type is a
nullable value, and only a parameter can hold a non-escaping one. A null check outside a lambda
doesn't narrow inside it.

To revisit once real programs show whether they get in the way:

- **Bind-once function locals.** A non-escaping local can't be reassigned; `?:` and switch
  expressions cover choosing one.
- **Narrowing.** Carrying a null check on a local that isn't `mut` into a lambda would be sound.
- **`F?` is a nullable value**, not reference-like. Locals, choices, and `??` would need an optional
  that owns a lambda, which non-escaping locals and choices avoid by being bound once or holding
  every option.

## Alternatives

- **Inferring whether a function escapes from how it is used**: the cost of a lambda would depend on
  code far from where it is written.
- **Every function value escaping**, as Java's lambdas are
  ([JLS 15.27.4](https://docs.oracle.com/javase/specs/jls/se27/html/jls-15.html#jls-15.27.4)): a lambda that captures anything needs an
  object to hold its captures.

## Why

The cost of a lambda is visible where it is declared, never inferred from how it's used later: a
non-escaping local stays non-escaping, and storing it is an error. Being bound once, it can't
outlive what its lambda captures, so non-escaping lambdas need no allocation and may change the
locals they capture.
