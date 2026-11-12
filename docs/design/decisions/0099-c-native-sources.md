# 0099. A package's `native/` directory may hold C sources, which compile as C17 into kept objects

- Status: accepted (2026-11-12)
- Spec: `interop.native`

## Decision

The build compiles each `.c` file under a package's `native/` directory as C17, and each `.cpp` file
as C++20. The build compiles no other kind of file. Each language has one fixed set of flags, and the
manifest has no list of compiler flags.

A C source gets the flags of the program that do not belong to C++: the sanitizers of the
toolchain, the optimization of the build mode, the include directories, and the definitions of the
manifest's `native` block. C and C++ sources share one list of definitions. A C source does not get
`/std:c++20`, `/EHsc`, `/permissive-`, or `WYL_DEBUG`. Neither the program nor a native source
names a C runtime library, so every object uses the toolchain's default, and the objects link
together.

Each native source compiles to an object of its own, and the build keeps the object for the next
build. The build compiles the source again only if the object's key changes. The key covers the toolchain,
the mode, the compiler's arguments, the manifest's `native` lists, and every file under `native/`
and under each `include` directory. A change to the Wylow sources alone therefore compiles no native
source again. The build shows a native source's warnings only if that source fails to compile.

The compiler adds a note and a help line to `WYL0519` if the package has a C source and the missing
symbol is a C++ name. The help tells the reader to declare the function inside `extern "C"`.

## Alternatives

- **A list of compiler flags in the manifest.** A flag of one toolchain is an error or a different
  option in another, so a package with such a list would build with one toolchain only. A library
  that needs more than the fixed flags ships a prebuilt library, which the manifest's `link` list
  already names.
- **Other extensions, such as `.cc`, `.cxx`, and assembly sources.** No package needs them yet.
  Each extension that the build accepts is part of the spec, so the list starts small.
- **A choice of C standard in the manifest.** Every supported toolchain compiles C17 (decision
  0098). A manifest key can be added if a library needs another standard.
- **Separate definitions for C sources.** SQLite's options are `SQLITE_` definitions, which mean
  nothing to a C++ source, so one list serves both languages until a package needs two.
- **Every warning of every native source, on every build.** The author of a package cannot act on
  the warnings of a library's source, and they would bury the program's own output. A source that
  fails to compile is easier to fix with its warnings, so a failure shows them.
- **One object cache that every build directory shares.** A shared cache would let two programs
  reuse one object. It also needs a lock and a rule for removing old objects. A cache in each build
  directory already keeps the amalgamation from compiling again on each Wylow change.
- **A key from the compiler's list of included files**, through `/sourceDependencies`, `-MMD`, or
  `/showIncludes`. Such a key compiles a source again only if a file that it did include changed,
  rather than any file that it could include. Each toolchain writes the list in another format, and
  a missing or stale list would keep a wrong object. The coarse key cannot keep a wrong object.
- **The language declares C linkage itself**, with an annotation on an extern function. The header
  already decides the function's linkage for every C++ file that includes it. A C++ program is
  ill-formed if two declarations give one function different language linkages
  ([dcl.link](https://eel.is/c++draft/dcl.link)), so an annotation could only repeat the header.

## Why

A library that is written in C is often distributed as source. SQLite ships as one C file, the
amalgamation, which "contains everything an application needs to embed SQLite"
([The SQLite Amalgamation](https://sqlite.org/amalgamation.html)). If the build compiles C sources, a
package can carry the library's source, and it needs no import library or prebuilt binary. The
amalgamation is about 9.6 MB. On the development machine, `examples/sqlite` took 4.8 seconds to
build with clang-cl in a debug build and 21 seconds in a release build. After the build had kept the
object, a change to the Wylow source took 1.9 seconds to build.

A C source compiles without support for C++ exceptions, and no Wylow code needs that support. A
panic inside a callback ends the program (`interop.callbacks`), and a C++ exception does not cross
into Wylow code (`interop.exceptions`). A panic therefore never unwinds through a frame of a C
source.
