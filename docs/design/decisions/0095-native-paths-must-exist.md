# 0095. Each path that the manifest's `native` block names must exist

- Status: accepted (2026-11-12)
- Spec: `programs.manifest`

## Decision

It is a compile-time error (`WYL0518`) if a directory that `include` or `libraryPaths` names does
not exist, or if a file that `copy` names does not exist. The compiler checks the lists of `native`
and the lists of the platform block for the platform that the program is built for. It does not
check the platform blocks of other platforms, and it does not check the libraries that `link` names.

## Alternatives

- **Leave each path to the C++ toolchain.** A missing include directory then shows up as a header
  that a C++ compiler cannot find, reported at the generated `#include` of an extern block, not at
  the manifest. A missing library directory shows up as a library that the linker cannot open. A
  missing `copy` file shows up only when the built program starts and cannot load its shared
  library. In each case the error names neither the manifest nor the entry that is wrong.
- **A warning instead of an error.** A build with a missing path fails later in every case above,
  so a warning would only delay the same failure and move it away from its cause.
- **Check the platform blocks of every platform.** A package may keep each platform's prebuilt
  libraries only on that platform's machines, so a Windows checkout need not hold the Linux
  libraries. The check would then fail on a package that builds correctly everywhere.
- **Check that each library in `link` exists.** A library in `link` may come from a system
  directory that the platform's linker searches, such as an installed `sqlite3`. The compiler does
  not know every such directory, so it cannot decide that a library is missing.

## Why

The compiler reports an error where the programmer can fix it. Each checked path is the package's
own, relative to its directory, so the compiler can decide by itself whether the path exists, and
the error points at the manifest entry that names it.
