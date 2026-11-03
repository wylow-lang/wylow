# 0032. Generic types have no static fields

- Status: accepted (2026-11-02); answers the first open question of [0030](0030-statics-are-initialized-before-main.md)
- Spec: `variables.statics`

## Decision

A generic type may not declare a static field; doing so is a compile-time error. Its static
methods are allowed.

## Alternatives

- **A static per instantiation**, so `Box<int>` and `Box<String>` each have their own. Every static
  is initialized before `main` (0030), so the set of instantiations would have to be listed and
  ordered. Which instantiations a program has is decided by what the compiler instantiates, which
  inlining or removing unused code can change, so an initializer that prints or panics would behave
  differently under `--no-opt`; defining the set by a language rule needs the whole program. No
  order for them can be read from the source either.
- **One static shared by every instantiation**, which may not mention the type parameters. This is
  Java's rule: every parameterization of a generic class shares one class at run time
  ([JLS 8.1.2](https://docs.oracle.com/javase/specs/jls/se21/html/jls-8.html#jls-8.1.2)), a static
  field has exactly one incarnation (8.3.1.1), and a static context may not refer to the class's
  type parameters (8.1.3). It is sound, but it is the same as a static of a non-generic type, in a
  place that suggests otherwise: two ways of writing one thing.

## Why

Both ways of allowing them are worse than not having them: one makes a language rule depend on the
optimizer or on the whole program, the other duplicates a non-generic static. A value computed per
type argument can be passed in, or computed by a function. Allowing them later breaks no program;
forbidding them after allowing them would.

Static fields are not implemented (#6); the error and its catalog code come with them.
