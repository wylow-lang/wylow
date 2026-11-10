# 0083. A member that a call cannot see does not hide an extension

- Status: accepted (2026-11-10)
- Spec: `functions.extensions`

## Decision

A call `x.name(...)` considers the extensions named `name` when the receiver's type has no member
named `name` that is visible where the call is. A member that the call cannot see, such as a private
field of a type in another module, no longer hides an extension of the same name.

A visible member still wins over an extension, whatever the call's arguments are. `List<T>` has a
visible `count()`, so `list.count(test)` reports that `count()` takes no argument, and a program
reaches the span's `count(test)` through `list.asReadOnlySpan().count(test)`.

## Alternatives

- **Every member hides an extension, as before.** Whether a program could call an extension would
  depend on the private fields of a type in another module. A private field named `count` in
  `Span<T>` hid `std::iteration`'s `count` extension on spans.
- **Extensions join overload resolution with the members**, so that `list.count(test)` reaches the
  extension when no member accepts the arguments. C# considers extension methods only when no
  instance method is applicable to the arguments
  ([extension method invocations](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/expressions#128103-extension-method-invocations)).
  The rule is larger, and a member added later with a matching signature would silently take over
  calls that reached an extension.

## Why

A file cannot name a member that it cannot see, so that member cannot be the method that a call
means. Letting it hide an extension makes a type's private layout part of what every other module
can call.
