# 0010. While a base class constructs, the object is that class

- Status: accepted (2026-10-20)
- Spec: `classes.construction`

## Decision

While a base class's constructor or destructor runs, virtual calls dispatch to the base class's
implementations, as in C++. Until every field that needs a value is assigned, a constructor can use
`this` only to assign and read its fields.

## Alternatives

- **Dispatch to the derived class during construction** (Java, Kotlin): a virtual call can observe
  the derived class's fields before they are assigned.
- **Two-phase initialization** (Swift): a derived class assigns its fields *before* calling `super`.

## Why

It is the simplest sound rule: each class can only observe its own fields (fully assigned before
`this` escapes) and its base's (completely constructed).
