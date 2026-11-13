# 0103. An extern block's header is in its own package, or is a system header written in angle brackets

- Status: accepted (2026-11-12)
- Spec: `interop.extern`, `interop.native`

## Decision

- `@Include("path")` names a header of the block's own package. The compiler searches for the path
  in the package's `native/` directory, and then in the package's `include` directories, in the
  order the manifest lists them. It is a compile-time error if none of them holds the header. The
  error names the package and each directory that the compiler searched. If another package of the
  program holds a header of that path, a note says so.
- `@Include("<path>")` names a system header, such as one that the operating system installs with
  a library. The compiler does not search for it. The C++ code includes it as `#include <path>`, and
  the C++ compiler searches its own directories. If the C++ compiler cannot find the header, the
  build reports the C++ compiler's error at the `@Include` (`WYL0513`).
- It is a compile-time error if a path that starts with `<` does not end with `>`, or names nothing
  between them.
- The standard library's extern blocks include the runtime's header, which is the standard
  library's own.
- A manifest does not say how to get a package's missing native files. A later decision will let a
  manifest name files to download, each with its hash, and the compiler will fetch them.

## Alternatives

- **Every header in the package, and none from the system.** A binding of a system library would
  need a header in `native/` whose only line includes the system header. The rule would be simple,
  but every such binding would carry the extra file.
- **The C++ include path, as the compiler built it before this decision.** If a block's package did
  not hold the header, the C++ code included the path as written, and the C++ compiler searched the
  runtime, every package's `include` directories, and its own. A block could then find another
  package's header without any rule allowing it, and a missing header was only a C++ compiler error.
  The spec already said that the search does not look in another package.
- **A manifest entry that tells the user how to get missing files**, such as a `missing` string that
  the error prints as help. The entry would name a script that each library writes for itself. The
  user and the assistant chose to let a manifest name the files to download instead, so that no
  library needs a script and the compiler fetches the files itself.

## Why

A block's quoted header is a file that the package owns. If the compiler finds the file before C++
runs, the compiler can say which package and which directories it searched, and the error is the
same with every C++ compiler. A system header is not the package's, and only the C++ compiler knows
its directories, so the program marks a system header in the path itself. The program marks it
with angle brackets because C's `#include <path>` searches only places that the implementation
defines (C17 6.10.2, and [cppreference](https://en.cppreference.com/w/c/preprocessor/include)).

The generated program's include path still holds every package's `include` directories, because a
package's headers may include each other. A system header that a block includes in angle brackets
is therefore found first in a package's directory if one holds a file of that path.
