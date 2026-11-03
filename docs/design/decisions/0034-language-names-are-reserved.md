# 0034. The names every file sees mean only their declarations

- Status: accepted (2026-11-02)
- Spec: `programs.builtin-names`

## Decision

Outside `std`, no declaration of any kind has one of the names every file sees (`String`, `Array`,
`Span`, `ReadOnlySpan`, `Result`, `Spannable`, `ReadOnlySpannable`, `Borrowable`, `Printable`,
`Comparable`, `Copyable`): not a type or function (`WYL0057`, as before), and not a method, field,
variant, type parameter, parameter, local, or pattern binding (`WYL0057`, new). No import brings in
anything else under one of them (`WYL0437`); importing the declaration itself, `import std::Result;`,
is valid.

## Alternatives

- **Only top-level types and functions** (the rule until now). An import (`import std::List as
  Result;`) or a local named `Result` then hid the language's `Result` in its file or scope, so
  `Result<int, String>.Ok(1)` meant something else there, or nothing.
- **Letting inner scopes shadow them**, as any other name may be shadowed. These names are what the
  syntax is built on (`T[]` is `Array<T>`, `try` is `Result`), so a reader should never have to
  check which `Result` a file means.

## Why

A name the language itself depends on has one meaning everywhere, as a keyword does, while staying
an ordinary type that generic code and imports can name. The cost is eleven names a program can't
use for its own declarations, all `PascalCase` type names that a field or local, by the naming
conventions, wouldn't take anyway.
