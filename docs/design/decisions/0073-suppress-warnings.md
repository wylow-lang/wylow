# 0073. A warning for a bare call of an imported function, and `@SuppressWarnings`

- Status: accepted (2026-11-09)
- Spec: `annotations.suppress-warnings`, `annotations.list`, `programs.imports`, `introduction.errors`

## Decision

- The compiler warns of a call that names a function by a bare name, where the function was brought
  in by one of the file's imports: `WYL0480`, `function 'replace' is called without its module`, with
  `help: write 'std::replace'`. The warning is named `unqualified-call`.
- The warning is reported where the call is, not at the import. It is not given for a function of
  the file's own module, including one that the module re-exports, for a method, for the
  construction of an imported struct, or for a function of a package's root module other than
  `std`'s, because no path can name that function.
- `@SuppressWarnings("name", ...)` replaces `@AllowLargeStack`. Each argument is a string literal that
  names a warning. On a type, function, method, constructor, field, or local variable, the annotation
  turns the named warnings off inside the declaration's text. On an import, it turns them off
  wherever the file uses a name that the import brings in. An import may have no other annotation,
  so error `WYL0035` (`imports cannot have annotations`) is retired.
- An argument that names no warning is warning `WYL0481`, named `unknown-warning`. An annotation with
  no arguments, or with an argument that is not a string literal, is error `WYL0482`.
- Each warning's name is part of its catalog entry (`[Warning(code, anchor, name)]`). The spec does
  not list warnings or their names.
- The test runner fails a test on any warning that its `// warning[WYLnnnn]: <message>` markers do not
  list. The catalog test does the same for a diagnostic page's example, and the spec-example test for
  a spec block. Programs that self-tests and matrix cells build through `TestPrograms.WithStd` turn
  the warning off on their `*` imports.

## Alternatives

- **A convention enforced only by review.** Decision 0072 states the convention. Code that people
  and models write keeps the habit of bare calls, and review misses some of them, so the convention
  drifts.
- **An error for importing a function by name.** The standard library's `pub import panic::panic;`
  would be an error, and so would `import std::panic;`, which brings in both a module and a function.
  The rule would need answers for `*` imports and for mixed lists such as
  `import std::{List, replace};`. A warning at the call needs none of these answers, because a call
  either names its module or does not.
- **A warning at the import.** An import that is never called bare, such as `import std::panic;`
  used only for `panic::checkIndex`, would warn for nothing.
- **Warning codes as `@SuppressWarnings`'s arguments**, as C# does with
  [`#pragma warning disable CS0168`](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/preprocessor-directives#pragma-warning).
  A code such as `WYL0480` does not say which warning the annotation turns off.
- **`@Allow(unqualifiedCall)`**, after Rust's
  [`#[allow(lint)]`](https://doc.rust-lang.org/reference/attributes/diagnostics.html#lint-check-attributes).
  It works equally well, and Java's conventions win toss-ups in Wylow's surface syntax
  (`docs/design/working.md`).
- **Keeping `@AllowLargeStack` beside `@SuppressWarnings`.** The compiler does not report the
  large-local warning yet, and one annotation per warning would add a name to the language for each
  warning. A large-local warning, once the compiler has one, takes a name that `@SuppressWarnings`
  accepts.

## Why

Decision 0072 asks code to call functions through their module. A convention that only reviewers
check does not hold in code that models write, so the compiler says where the convention is broken,
and the test suite treats an unlisted warning as a failure. A warning rather than an error keeps the
cost low where a bare call is the point, as in a test of what an import brings in.

`@SuppressWarnings` follows Java's
[`@SuppressWarnings`](https://docs.oracle.com/javase/specs/jls/se21/html/jls-9.html#jls-9.6.4.5)
(JLS §9.6.4.5): string arguments that name warnings, on a declaration, turning the warnings off inside
it. Wylow also allows it on an import, because Wylow has no annotation for a whole file. Without it, a
file that calls an imported function bare in many places would annotate each declaration. The
compiler checks the names against its own list and warns of a name that it does not know, so a
misspelled name does not silently turn nothing off.
