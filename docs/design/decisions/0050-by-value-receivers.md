# 0050. By-value receivers replace `move` methods

- Status: accepted (2026-11-04); replaces the `move` methods of
  [0049](0049-getting-a-unique-value-out.md)
- Spec: `structs.methods.by-value`, `structs.unique.destructors`, `enums.methods`,
  `interfaces.implementing`, `functions.overloading`, `functions.methods`, `functions.extensions`,
  `expressions.null`, `patterns.bindings.moves`, `flow.moves`, `memory.moves`, `errors.result`,
  `lexical.keywords`

## Decision

A struct or enum method takes its receiver by value by writing it as its first parameter, in the form
an extension method already uses: `pub T orElse(this Result<T, E> result, T fallback)`. The word `move`
and `discard this` are removed.

- The type written is the declaring type, with its own type parameters as type arguments.
- The method has no `this`. The receiver is an ordinary by-value parameter that is not `mut`, and
  members are reached through its name.
- It is an instance method: it is called on a value and not through its type.
- The receiver is not one of the method's parameter types, so `orElse(this R r, int x)` and
  `orElse(int x)` conflict.
- It cannot implement an interface method, and classes, interfaces, constructors, and indexers cannot
  declare one. `this ref` and `this in` are not allowed on a method.

What `Result`'s `orElse`, `map`, `mapErr`, and `orPanic` do is unchanged from 0049; only the way they
are declared changes.

## Alternatives

- **Keep the `move` modifier**, the form of Swift's `consuming func`
  ([SE-0377](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0377-parameter-ownership-modifiers.md)).
  It is shorter and names what happens. It is also a second spelling of what `this T` already means
  (`functions.extensions`), and it needs `this` to be a by-value parameter in a few places and a
  reference in the rest: moving bindings, use after move, and `discard` each carried a clause for it.
- **A bare `this` in the parameter list**, as Rust's `self` shorthand stands for `self: Self`
  ([methods](https://doc.rust-lang.org/reference/items/associated-items.html#methods)). It repeats no
  type, but it is new grammar beside the extension form, and it keeps the case of a `this` that is by
  value. It can be added later.
- **Extensions only.** No new form at all, but a caller of `Result.orElse` would import `orElse`. 0049
  rejected the exception that would avoid the import, and a member needs no exception: what a type
  declares comes with it.
- **Static methods only.** They already take a by-value parameter, but `Result.orElse(r, 0)` loses
  `parse().map(f).orElse(0)` and `?.`.
- **Inferring it from the body.** Editing a body would change what compiles in its callers.
- **`this ref` and `this in` on methods too**, as C++23's explicit object parameter allows
  ([explicit object member functions](https://en.cppreference.com/w/cpp/language/member_functions.html)).
  They would be second spellings of `mut` and plain methods, and `this ref` would get around enum
  methods not being `mut`.

## Why

The language has one way to say "this parameter is passed by value", and this reuses it, with no new
word and no new grammar: `Parameters` already accepts a receiver parameter. The form and its limits
match C++23's explicit object parameter: `void bar(this X self, int i)` passes by value, the function
cannot be `static` or `virtual`, and its body has no `this` and reaches members through the parameter
(same page). The special cases for a by-value `this` go away with it.

The cost is a longer declaration, which repeats the type and names the receiver, and a signature that
implies the move through the by-value rule instead of stating it. Few methods need this form, and the
call is unchanged.

The receiver is left out of the parameter types so that a by-value method and an in-place one can't
share a name and be told apart only by how the receiver is passed. That would otherwise let such a
method match an interface method's parameter types, so implementing one is an error: interface calls
use the value in place.
