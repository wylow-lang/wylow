# 0022. Construction: `new` only for the heap, one way to build a struct

- Status: accepted (2026-10-19)
- Spec: `expressions.construction`, `structs.constructors`

## Decision

`new` appears only where memory is allocated: classes and heap arrays. A struct is built one way,
by calling a constructor; the memberwise constructor with named arguments sets fields by name.

## Alternatives

- **`new` for every construction**, as in Java ([JLS 15.9](https://docs.oracle.com/javase/specs/jls/se27/html/jls-15.html#jls-15.9)) and
  C# ([`new` operator](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/new-operator)),
  which uses it for structs too.
- **Initializer syntax** beside constructors, like C#'s
  [object initializers](https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/classes-and-structs/object-and-collection-initializers)
  (`new Point { X = 1, Y = 2 }`).

## Why

Every allocation is visible: scanning for `new` finds them all. With one way to build a struct,
default values, positional construction, and setting fields by name are all the same call.
