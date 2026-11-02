# 0022. Construction: `new` only for the heap, one way to build a struct

- Status: accepted (2026-10-19)
- Spec: `expressions.construction`, `structs.constructors`

## Decision

`new` appears only where memory is allocated: classes and heap arrays. A struct is built one way,
by calling a constructor; the memberwise constructor with named arguments sets fields by name.

## Alternatives

- **`new` for every construction** (Java, C#).
- **Initializer syntax** (C#'s `T { field = value }`), beside constructors.

## Why

Every allocation is visible: scanning for `new` finds them all. With one way to build a struct,
default values, positional construction, and setting fields by name are all the same call.
