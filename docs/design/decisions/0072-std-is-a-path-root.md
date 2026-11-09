# 0072. Every file sees `std` as a path root, and code calls functions through their module

- Status: accepted (2026-11-09)
- Spec: `programs.paths`

## Decision

- Every file sees the package `std` as the first name of a path without an import, so
  `std::print(x)` and `std::memory::collectCycles()` need no import line. A submodule of the file's
  module, a module that the file's module re-exports, and a module that the file imports all come
  before `std`, so a file that imports another module under the name `std` reaches that module.
- `import std::print;` and the other imports of `std` names stay valid.
- The project's own code imports types by name and calls functions through their module:
  `import std::List;` and `new List<int>()`, but `std::print(x)`, and `threading::spawn(...)` after
  `import std::threading;`. The tests, the examples, the spec's examples, and the diagnostic pages
  follow this convention. Decision 0073 adds the warning that enforces it.

## Alternatives

- **No change**: `import std;` already let a file write `std::print(x)`. A file needed an import
  line for either form, and the bare form was shorter at every call, so code used the bare form.
- **Only modules may be imported**, so that every name is qualified. Go works this way: a name of
  another package is always written with its package name, as in `fmt.Println`
  ([Qualified identifiers](https://go.dev/ref/spec#Qualified_identifiers)). In Wylow this would also
  qualify every type, as in `std::List<int>`, and a type name says enough by itself.
- **Making it an error to import a function by name.** The standard library re-exports
  `panic::panic` with `pub import`, which such a rule forbids. The rule also needs answers for `*`
  imports and for a name that is both a module and a function. Decision 0073 chooses a warning
  instead.

## Why

A function's name says what the function does but not where it comes from. `std::replace(a, b)`
tells the reader that the call is the standard library's, and `replace(a, b)` does not. A type name
reads well without a module, because the type is a noun that the reader looks up once. The Rust
book recommends the same split: bring a function's parent module into scope, and bring a struct or
enum in by its own name
([Creating Idiomatic `use` Paths](https://doc.rust-lang.org/book/ch07-04-bringing-paths-into-scope-with-the-use-keyword.html#creating-idiomatic-use-paths)).
Rust makes every crate that a package depends on, `std` included, a path root in every module
([Extern prelude](https://doc.rust-lang.org/reference/names/preludes.html#extern-prelude)), so
`std::mem::replace(...)` needs no `use`. Wylow does the same for `std`.

No program can declare a module that hides `std` by accident. A module directly in `src/` cannot be
named `std` (`programs.modules`), and only a submodule of the file's own module or a deliberate
`import ... as std` can take the name.
