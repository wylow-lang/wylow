# 0051. Visibility is one word: `module`, `package`, or `pub`

- Status: accepted (2026-11-04)
- Spec: `programs.visibility`, `lexical.keywords`

## Decision

A declaration's visibility is one of three words — `module`, `package`, or `pub` — or no word at
all, which makes it visible only in its file. `pub(module)` and `pub(package)` are removed, and
writing either is a compile-time error (`WYL0052`). `module` and `package` are keywords; `package`
is no longer a reserved word.

The levels do not change: `module` reaches every file of its module (its directory, not the
directories below it), `package` reaches its package, `pub` reaches every package, and no word at
all means the declaring file.

## Alternatives

- **Keep `pub(module)` / `pub(package)`.** It is Rust's spelling
  ([Visibility and privacy](https://doc.rust-lang.org/reference/visibility-and-privacy.html)), and
  it keeps `module` an ordinary identifier. Rust's parentheses hold an arbitrary path,
  `pub(in SimplePath)`; here the two scopes are fixed, so the parentheses carry no information,
  and this is the only modifier written with parentheses.
- **`pub module` / `pub package`.** `pub` stays the one visibility root. It reads like `pub import`,
  which re-exports, and a public field of a type named `module` (`pub module x;`) would need
  lookahead to tell from the visibility.
- **`mod` / `pkg`.** The same length as `pub`: three letters. `mod` is Rust's keyword for declaring
  a module ([Modules](https://doc.rust-lang.org/reference/items/modules.html)), which is a
  different meaning, and it still reads as declaring a module rather than removing that reading.
  `pkg` is not an access word in the languages checked (Swift's is `package`). Both also separate
  the keyword from the spec's term for the scope.
- **Make `module` the default and write file privacy as `file`.** It removes the module word
  entirely, as Swift's `internal` default and `fileprivate` do
  ([Access Control](https://docs.swift.org/swift-book/documentation/the-swift-programming-language/accesscontrol/)),
  but it flips the default and marks every file-private declaration. The file boundary is what
  keeps an abstraction over `unsafe` sound, so keeping it the default is the point.

## Why

Every other modifier is one word, and the visibility levels are exactly the words `module`,
`package`, and `pub`; writing them directly removes the only parenthesized modifier without adding
a scope grammar. Java's, Swift's, and C#'s access modifiers are bare words, and Swift's `package`
level ([SE-0386](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0386-package-access-modifier.md),
"Implemented (Swift 5.9)") is a package-scoped level too.

The cost is that `module` becomes a keyword: a program can no longer name a type, field, local, or
directory `module`. The language already reserves `package` and many structural words, and no
program in `std`, the tests, or `examples` names anything `module`.
