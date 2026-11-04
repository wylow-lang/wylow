# The Wylow Language Specification

The definition of the Wylow language: its rules only, each stated once
([chapter 1](01-introduction.md) gives the conventions). The reasons for the rules are in
[decision records](../design/decisions/README.md), how the compiler implements them in
[docs/design](../design/), what diagnostics mean in [docs/diagnostics](../diagnostics/README.md),
and what to expect coming from other languages in [the guide](../guide/README.md).

| Chapter | Name | Contents |
|---|---|---|
| [1](01-introduction.md) | `introduction` | Scope, notation, terms, kinds of rules, observable behavior |
| [2](02-lexical.md) | `lexical` | Source text, tokens, comments, identifiers, keywords, literals, naming conventions |
| [3](03-programs.md) | `programs` | Packages, the manifest, modules, imports, paths, visibility, the entry point |
| [4](04-types.md) | `types` | Kinds of types, primitives, nullable types, arrays, spans, function types, pointers, `String`, default values |
| [5](05-conversions.md) | `conversions` | Implicit conversions, integer constants, casts |
| [6](06-variables.md) | `variables` | Locals, mutability, `ref` and `in` variables, constants, statics |
| [7](07-structs.md) | `structs` | Fields, constructors, `mut` methods, copies, unique structs, `ref struct`s, layout |
| [8](08-classes.md) | `classes` | Inheritance, overriding, constructors, order of construction, destructors |
| [9](09-enums.md) | `enums` | Variants, unique enums, integer representations, flags |
| [10](10-interfaces.md) | `interfaces` | Declaring and implementing interfaces |
| [11](11-generics.md) | `generics` | Type parameters, inference, bounds |
| [12](12-functions.md) | `functions` | Parameters, overloading, extension methods, indexers, lambdas |
| [13](13-expressions.md) | `expressions` | Evaluation order, operators, `try`, string interpolation |
| [14](14-statements.md) | `statements` | Blocks, assignment, loops, `switch`, `defer` |
| [15](15-patterns.md) | `patterns` | Patterns, cases, guards, exhaustiveness |
| [16](16-flow.md) | `flow` | Definite assignment, single assignment, narrowing, use after move, fields in constructors, reachability, lambdas |
| [17](17-memory.md) | `memory` | Kinds of storage, moves, objects, releases, weak references, cycles, references, holds, borrows, second-class values |
| [18](18-errors.md) | `errors` | Panics, `Result` |
| [19](19-concurrency.md) | `concurrency` | Thread confinement, sendable types, shared classes and functions, isolated graphs |
| [20](20-unsafe.md) | `unsafe` | Unsafe code, pointers, undefined behavior |
| [21](21-interop.md) | `interop` | Extern declarations, blittable types, callbacks, exports |
| [22](22-annotations.md) | `annotations` | Annotation syntax and the annotations |
| [A](a-library.md) | `library` | The standard library declarations the language names |

A chapter's name starts the name of each of its sections (`memory.moves`), and links use the
names, never the numbers.

## Chapters being rewritten

Chapters 1 to 19 are written. Chapters 20 to 22 and appendix A are being written from these files,
which are deleted as their text moves:

- [02-types.md](02-types.md): strings, arrays and spans, and the bounds and type properties of
  generics
- [06-syntax.md](06-syntax.md): `std::panic` and `std::memory`, annotations, and the advice on
  properties
- [07-interop.md](07-interop.md): `extern`, blittable types, C enums, callbacks
