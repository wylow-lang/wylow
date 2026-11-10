# 0080. An extension call looks past names that have no extension for its receiver

- Status: accepted (2026-11-10)
- Spec: `functions.extensions`

## Decision

A call `x.name(...)`, where the type of `x` has no member `name`, looks for extensions in the
places that a file looks for a name, in their order (`programs.imports`): the module's names and the
file's own declarations, the names that imports bring in by name, the names that `*` imports bring
in, and the names every file sees. The call takes its candidates from the first place that has an
extension named `name` that accepts the receiver. Overload resolution then chooses among those
candidates as it does for any call. A place whose functions named `name` include no such
extension does not hide the places after it.

An extension accepts the receiver if the receiver converts to the extension's `this` parameter
(`functions.extensions`). The compiler infers the type arguments that the `this` parameter
determines from the receiver alone, and checks them against their bounds.

A call written as a function call, `name(x, ...)`, keeps the ordinary rule: the name means the first
place that has it.

## Alternatives

- **The name's one meaning in the file**, the rule for every other use of a name. A module that
  declares its own function `count` could then never call `it.count()` on an iterator: the module's
  `count` hides every imported `count`, and an import of `std::iteration::count` by name is an error
  because the module declares the name (`programs.imports`). The adapters have short, common names,
  so this conflict would be frequent, and nothing at the call would explain it.
- **Every visible extension of the name, from all places together.** An extension that a program
  declares would then be ambiguous with an imported one of the same receiver type, instead of
  hiding it as a module's names hide imports everywhere else.

## Why

An extension call looks like a method call, so the name after the dot is read as a member of the
receiver's type, not as the file's name `name`. C# searches each enclosing namespace for extension
methods whose receiver the argument converts to, and goes on to the next namespace while it finds
none ([extension method invocations](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/expressions#128103-extension-method-invocations)).
Kotlin resolves a call with an explicit receiver level by level in the same way
([call with an explicit receiver](https://kotlinlang.org/spec/overload-resolution.html#call-with-an-explicit-receiver)).
Wylow keeps the order of its own name lookup and skips only the places that have nothing to offer
the receiver, so a nearer extension for the same receiver still hides a farther one.
