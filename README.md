# VPL

A personal programming language for 3D games: Java-style syntax, C++-level control, no undefined
behavior by default. VPL compiles to C++.

The language is described in [docs/spec](docs/spec/README.md).

## Status

- **Milestone 1:** functions, `int`/`long`/`float`/`double`/`bool`, string literals, locals and
  `var`, arithmetic, casts, `if`/`while`/`for`/`break`/`continue`, `?:`, overloading, `print`,
  `panic`. Checked arithmetic, definite assignment, and left-to-right evaluation are enforced.
- **Milestone 2:** structs: fields with initializers, constructors, `{ field = value }` initializers,
  instance/static/`readonly` methods, `private`, memberwise `==`, and `ref`/`in` parameters.

## Requirements

- .NET 10 SDK
- Visual Studio with the "Desktop development with C++" workload (MSVC), or `g++` on `PATH`

## Usage

```
dotnet build
dotnet src/Vpl.Cli/bin/Debug/net10.0/vpl.dll run examples/hello.vpl
```

| Command | |
|---|---|
| `vpl run <file>` | build and run |
| `vpl build <file> [-o out.exe]` | build an executable |
| `vpl check <file>` | report errors only |
| `vpl emit <file>` | print the generated C++ |

Options: `--release` (optimized; integer overflow wraps instead of panicking), `--cc msvc|gcc`.

## Layout

| Path | |
|---|---|
| `src/Vpl.Compiler` | Lexer → parser → binder → flow analysis → C++ emitter, plus the native build driver |
| `src/Vpl.Cli` | The `vpl` command |
| `runtime/vpl.h` | C++ support code included by every generated program |
| `tests/cases` | Test programs; expectations are `// output:`, `// error:`, `// panic:` comments |

## Tests

```
dotnet build
dotnet tests/Vpl.Tests/bin/Debug/net10.0/Vpl.Tests.dll [--cc gcc] [filter]
```
