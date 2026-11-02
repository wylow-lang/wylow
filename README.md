# Wylow

A personal, general-purpose programming language: Java-style syntax, C++-level control, no undefined
behavior by default. Wylow compiles to C++.

The language is described in [docs/spec](docs/spec/README.md).

## Status

- **Milestone 1:** functions, `int`/`long`/`float`/`double`/`bool`, string literals, locals and
  `var`, arithmetic, casts, `if`/`while`/`for`/`break`/`continue`, `?:`, overloading with default
  and named arguments, `print`, `panic`. Checked arithmetic, definite assignment, and left-to-right
  evaluation are enforced.
- **Milestone 2:** structs: fields with initializers, constructors, memberwise constructors,
  instance/static/`mut` methods, `private`, memberwise `==`, and `ref`/`in` parameters.
- **Milestone 3:** generics: generic structs, functions, and methods, type argument inference,
  default-value rules for type parameters, and recursive-struct detection through type arguments.
- **Milestone 4:** classes: `new`, reference counting, destructors, single inheritance with
  `virtual`/`override`/`abstract`/`final`, `super`, interfaces (dynamic dispatch for classes, generic
  bounds for structs and classes), nullable references with `?.`, `??`, `!`, `is` patterns and flow
  narrowing, `weak` fields, and pinning of receivers and `ref` arguments that live inside objects.
- **Milestone 5:** enums with and without data, exhaustive `switch` with patterns, and `Result`.
- **Milestone 6:** containers: fixed arrays `T[N]`, `Span`/`ReadOnlySpan`, `Array` (`T[]`), `List`,
  and for-each, written in a Wylow standard library on `ref` locals and returns, indexers, `unsafe`
  pointers, `@CallerLocation`, and borrow counting.
- **Since then:** lambdas and function types (escaping and not), `String` with interpolation,
  `char`, `Weak<T>` and the cycle collector (`memory::collectCycles`), annotations, a mid-level IR
  with a verifier that is the flow analysis, optimization passes (stack objects, borrowed and
  moved locals), and an IR interpreter (`wyl interpret`) that runs programs without a C++
  toolchain.

What's next is tracked in the roadmap issue (#6).

## Requirements

- .NET 10 SDK
- Visual Studio with the "Desktop development with C++" workload (MSVC), or `g++` on `PATH`

## Usage

```
dotnet build
dotnet src/Wyl.Cli/bin/Debug/net10.0/wyl.dll run examples/hello.wyl
```

| Command | |
|---|---|
| `wyl run <file>` | build and run |
| `wyl build <file> [-o out.exe]` | build an executable |
| `wyl check <file>` | report errors only |
| `wyl emit <file>` | print the generated C++ |
| `wyl ir <file>` | print the lowered IR |
| `wyl interpret <file>` | run with the IR interpreter, no C++ toolchain needed |

Options: `--release` (optimized; integer overflow wraps instead of panicking), `--cc msvc|gcc|asan`.

## Layout

| Path | |
|---|---|
| `src/Wyl.Compiler.*` | One project per compiler layer: syntax, semantics, IR, lowering, analysis, optimize, codegen, interpret (`docs/design/architecture.md`) |
| `src/Wyl.Compiler` | Runs the layers in order, plus the native build driver |
| `src/Wyl.Cli` | The `wyl` command |
| `runtime/wyl.h` | C++ support code included by every generated program, split by topic into `runtime/wyl/` |
| `tests/cases` | Test programs; expectations are `// output:`, `// error:`, `// panic:` comments |

## Tests

```
dotnet build
dotnet tests/Wyl.Tests/bin/Debug/net10.0/Wyl.Tests.dll [--cc gcc|asan] [--interpret] [filter]
```

The fuzzer, the program generator, and mutation testing are in `tests/`; `CLAUDE.md` lists their
commands and `docs/design/testing.md` the testing strategy.
