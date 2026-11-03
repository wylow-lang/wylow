# 0035. `main` may take the arguments and return the exit status

- Status: accepted (2026-11-02)
- Spec: `programs.entry`, `programs.execution`, `introduction.implementation-defined`

## Decision

- `main` is `void main()`, `int main()`, `void main(String[] args)`, or `int main(String[] args)`.
- `args` holds the arguments without the program's name, each as UTF-8 with every maximal
  ill-formed subsequence replaced by U+FFFD. On Windows they come from the UTF-16 command line, so
  an unpaired surrogate is what is replaced; elsewhere they are the bytes of `argv`.
- An `int main` returns the exit status. How the target reports a value outside 0 to 255 is
  implementation-defined: POSIX keeps its low 8 bits, and Windows keeps all 32.
- The array is built by `std`'s own `programArguments()`, in Wylow, from two runtime functions, so
  the C++ `main` and the interpreter give `main` the same value.

## Alternatives

- **No arguments in `main`** (the compiler until now, though the old spec text allowed
  `int main(String[] args)`). A command-line program could not read its arguments at all.
- **A library function, such as `std::process::arguments()`, instead of a parameter.** Reachable
  from anywhere, so any function could depend on global state; with a parameter, only what `main`
  passes on sees them. A library function can still be added on top if a need appears.
- **Failing on an argument that is not UTF-8.** A program then can't start at all on a file name
  in another encoding, which a user can't fix from inside it. Replacement keeps `String` valid UTF-8
  ([0011](0011-string-value-type.md)) and the program running.
- **Truncating the exit status to 0 to 255 everywhere.** It would hide a Windows status a program
  means, such as an `HRESULT`.

## Why

The arguments are an input of the run, as chapter 1 already says; a parameter of `main` makes them
an explicit one. Defining the replacement makes every argument a valid `String` with one value,
whichever toolchain built the program.
