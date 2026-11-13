<a id="programs"></a>
# 3. Programs, packages, and modules

A program is a package, built together with the libraries it depends on. A package's source files
are grouped into modules, one per directory, and each declaration says how far it is visible: its
file, its module, its package, or everywhere.

<a id="programs.packages"></a>
## 3.1 Packages

A **package** is a directory holding a manifest, `package.wylcfg`
([The manifest](#programs.manifest)), and a directory `src/` holding its source files. Its
modules are `src/` and the directories below it ([Modules](#programs.modules)).

A package is a **program** or a **library**, as its manifest's `kind` says. A program has an entry
point ([The entry point](#programs.entry)) and is built into an executable. A library has no entry
point, and its code runs only as part of a program that depends on it
([Dependencies](#programs.manifest.dependencies)).

A source file compiled on its own is a program of one module and one file, which has no name.

A file of a program is named by its path below `src/`, with `/` between directories:
`geometry/shapes.wyl`. A file of a library that the program depends on is named by the library's
name, `::`, and its path below the library's `src/`: `parser::lexer/tokens.wyl`. A file compiled on
its own is named by its file name. This is the name a panic's location gives
([Panics](18-errors.md#errors.panics)).

A package is compiled as a whole, so its declarations may refer to each other in any order and from
any of its files, and its modules may import each other in a cycle. A program is compiled together
with the source files of every library it depends on.

- It is a compile-time error if a package has no `src/` directory.
- It is a compile-time error if a package has no source file.

Every program is compiled with the standard library, the package `std`
([The standard library](a-library.md#library.names)).

**Example:**

```
shop/
    package.wylcfg          the manifest
    src/
        main.wyl            the root module
        orders/
            order.wyl       module orders
            queue.wyl       module orders
            format/
                text.wyl    module orders::format
```

<a id="programs.manifest"></a>
## 3.2 The manifest

A manifest is read as tokens of chapter 2: whitespace, comments, identifiers, and string literals
with their escapes are as in a source file, so line breaks have no meaning in it. Its form is:

```ebnf
Manifest = { Entry } .
Entry    = Identifier ( "=" Value ";" | "{" { Entry } "}" ) .
Value    = StringLiteral | "[" [ StringLiteral { "," StringLiteral } [ "," ] ] "]" .
```

An entry's identifier is its **key**. An entry is a string (`key = "text";`), a list of strings
(`key = ["a", "b"];`), or a block of entries (`key { ... }`). These are the keys:

| Key | Where | Kind |
|---|---|---|
| `name` | top level | string |
| `kind` | top level | string |
| `version` | top level | string |
| `dependencies` | top level | block |
| *any name* | in `dependencies` | block |
| `path`, `version` | in an entry of `dependencies` | string |
| `native` | top level | block |
| `include`, `libraryPaths`, `link`, `copy`, `defines` | in `native`, or in a platform block of it | list of strings |
| `fetch` | in `native` | block |
| `url`, `sha256` | in `fetch` | string |
| `files` | in `fetch` | list of strings |
| `windows`, `linux`, `macos` | in `native` | block |

`name` gives the package's name, which need not be its directory's.

`kind` is `"program"` or `"library"` ([Packages](#programs.packages)). A package whose manifest has
no `kind` entry is a program.

`version` gives the package's version ([Versions](#programs.manifest.versions)).

`dependencies` lists the libraries that the package uses
([Dependencies](#programs.manifest.dependencies)).

`native` gives what the package's extern blocks and native source files need to build
([Interop](21-interop.md#interop)). Each path in it is relative to the directory of the package
whose manifest it is in.

- `include` lists directories that are searched for the headers that the package's `@Include`
  annotations name.
- `libraryPaths` lists directories that are searched for the libraries that `link` names.
- `link` lists libraries that are linked with the program. Each is named without the platform's file
  prefix or extension: `"parser"` names `parser.lib` on Windows and `libparser` elsewhere.
- `copy` lists files that are copied next to the built program, such as a shared library that it
  loads when it starts.
- `defines` lists preprocessor definitions, `"NAME"` or `"NAME=value"`. They are given to the
  package's own native source files, and to the C++ source into which any package's extern block
  includes its header.
- A platform block, `windows`, `linux`, or `macos`, holds entries of the same keys. When the
  program is built for that platform, each of its lists is added after the list of the same key in
  `native`.

`fetch` names a file that the compiler downloads for the package's native files. The `url` entry
is an absolute URL whose scheme is `https`. The `sha256` entry is the SHA-256 hash of the
downloaded bytes, written as 64 lowercase hexadecimal digits. The `files` entry lists the files of
the download that the package uses, each by its path in the download. A path is one or more
segments separated by `/`. A segment is not empty, `.`, or `..`, does not end with `.` or a
space, and is not a name that Windows reserves for a device. The path holds no backslash and no
character that a Windows file name cannot hold.

The compiler downloads the file, checks the hash, and keeps the file in a cache outside the
package. A URL whose path ends with `.zip`, `.tar`, `.tar.gz`, or `.tgz` names an archive, and the
compiler extracts the files that `files` names. Any other URL names one file, and `files` gives the
name of that file. The fetched files join the package's native files
([Native source files](21-interop.md#interop.native)).

**Example:**

```
// sqlite/package.wylcfg
name = "sqlite";
kind = "library";
version = "0.1.0";
native {
    fetch {
        url = "https://sqlite.org/2026/sqlite-amalgamation-3540100.zip";
        sha256 = "0a9c8e34352dad46006aa82836d5383bff9dc6bc9ce5a60553c05aa30e6ad1f9";
        files = ["sqlite-amalgamation-3540100/sqlite3.c",
                 "sqlite-amalgamation-3540100/sqlite3.h"];
    }
}
```

**Example:**

```
// parser_tool/package.wylcfg
name = "parser_tool";
native {
    include = ["vendor/parser/include"];
    link = ["parser"];
    windows {
        libraryPaths = ["vendor/parser/lib/windows"];
        copy = ["vendor/parser/lib/windows/parser.dll"];
    }
}
```

- It is a compile-time error if the manifest does not have the form above.
- It is a compile-time error if a string in the manifest is an interpolated string with a hole.
- It is a compile-time error if an entry's key is not one the table gives for where it is, if a key
  is given twice in one block, or if an entry is not of its key's kind.
- It is a compile-time error if the manifest has no `name` entry.
- It is a compile-time error if the name is not an identifier ([Identifiers](02-lexical.md#lexical.identifiers)),
  or is `std`.
- It is a compile-time error if `kind` is neither `"program"` nor `"library"`.
- It is a compile-time error if a library's manifest has no `version` entry.
- It is a compile-time error if a directory that `include` or `libraryPaths` names does not exist,
  or if a file that `copy` names does not exist. These rules apply to the lists of `native` and to
  the lists of the platform block of the platform that the program is built for.
- It is a compile-time error if `url` is not an absolute `https` URL.
- It is a compile-time error if `sha256` is not 64 lowercase hexadecimal digits.
- It is a compile-time error if `files` is empty, or a path in `files` is not a relative path
  inside the archive that every platform can write.
- It is a compile-time error if the downloaded file's SHA-256 hash is not the one that `sha256`
  gives.
- It is a compile-time error if the downloaded file is not a readable archive, or the archive
  holds no file of a path that `files` gives.
- It is a compile-time error if the downloaded file is larger than 512 MiB.
- It is a compile-time error if the compiler extracts more than 512 MiB from the download, or
  reads more than 1 GiB of the archive while it unpacks it.
- It is a compile-time error if the compiler cannot download the file. The error names the URL and
  the reason.

**Example:**

```
// shop/package.wylcfg
name = "shop";
```

<a id="programs.manifest.versions"></a>
### 3.2.1 Versions

A package's **version** is written `"major.minor.patch"`: three numbers, each written in decimal
digits, with no leading zero except in the number `0` itself. `"1.4.2"` has the major number 1, the
minor number 4, and the patch number 2.

An entry of `dependencies` may state the version that the package needs, written `"major.minor"` or
`"major.minor.patch"`. A missing patch number is 0. A library's version **satisfies** a stated
version if both of these hold:

- The library's version is not lower than the stated version. Versions are ordered by their major
  numbers, then by their minor numbers, then by their patch numbers.
- The library's major number equals the stated major number. If the stated major number is 0, the
  library's minor number also equals the stated minor number.

A stated `"1.4"` is satisfied by `1.4.0`, `1.4.7`, and `1.9.0`, and not by `1.3.9` or `2.0.0`. A
stated `"0.3"` is satisfied by `0.3.0` and `0.3.5`, and not by `0.4.0`.

- It is a compile-time error if a `version` entry does not have the form its place gives.
- It is a compile-time error if an entry of `dependencies` states a version and the library's
  version does not satisfy it.

<a id="programs.manifest.dependencies"></a>
### 3.2.2 Dependencies

Each entry of `dependencies` names a library that the package uses. The entry's key is the
library's name. The entry's `path` is the library's directory, relative to the directory of the
package whose manifest holds the entry. The libraries that these entries name are the package's
**dependencies**.

```
// shop/package.wylcfg
name = "shop";
dependencies {
    parser { path = "vendor/parser"; version = "1.4"; }
}

// shop/vendor/parser/package.wylcfg
name = "parser";
kind = "library";
version = "1.4.2";
```

A program is built from itself, its dependencies, their dependencies, and so on. These are the
**packages of the program**. Two paths that reach the same directory name the same package, and the
program compiles that package once.

A package sees its own dependencies, and no other library. A dependency's name is a path root in
every file of the package ([Paths](#programs.paths)). A file reaches the names of the dependency's
root module and its submodules through paths and imports, as far as their visibility allows
([Visibility](#programs.visibility)). A library may re-export a name of its own dependency with
`pub import` ([Imports](#programs.imports)).

The native settings of every package of the program apply to the program's build. Each package's
native source files are compiled with that package's own `include` and `defines` lists
([Native source files](21-interop.md#interop.native)). The `link`, `libraryPaths`, and `copy` lists
of every package apply to the program.

- It is a compile-time error if an entry of `dependencies` has no `path`.
- It is a compile-time error if the directory that `path` names does not hold a package.
- It is a compile-time error if the key of an entry is not the `name` of the package that its
  `path` names.
- It is a compile-time error if the package that `path` names is not a library.
- It is a compile-time error if a package is its own dependency, directly or through other
  dependencies.
- It is a compile-time error if two packages of a program have the same name and different
  directories.
- It is a compile-time error if a package has a dependency of the same name as a module directly in
  its `src/`.
- It is a compile-time error if two packages of the program define one preprocessor name with
  different values.
- It is a compile-time error if two files that `copy` lists for the platform that the program is
  built for have the same file name and different directories.

<a id="programs.modules"></a>
## 3.3 Modules

A **module** is a directory of a package. `src/` is the **root module**; each directory below it
that holds a `.wyl` file, itself or in a directory below it, is a module named by its path from
`src/`, with `::` between directories: `src/orders/format/` is the module `orders::format`. A
directory whose name starts with `.` is not a module, and nothing below it is part of the package.

Each `.wyl` file in a module's directory belongs to that module.

- It is a compile-time error if the name of a directory that is a module is not an identifier.
- It is a compile-time error if a directory directly in `src/` that is a module is named `std`.

<a id="programs.names"></a>
## 3.4 A module's names

A source file is a list of imports, then a list of declarations:

```ebnf
SourceFile  = { Import } { Declaration } .
Declaration = StructDeclaration | ClassDeclaration | EnumDeclaration | InterfaceDeclaration
            | FunctionDeclaration | ExternBlock .
```

A top-level declaration is a type or a function; a file may have any number of them. A declaration
is **file-private** if it has no visibility ([Visibility](#programs.visibility)).

A module's **names** are:

- the names of the top-level declarations of its files that are not file-private;
- the names of its submodules, the modules directly below it;
- the names it re-exports ([Imports](#programs.imports)).

Each file of a module sees the module's names and its own file-private declarations without an
import. A name is reached from any other module only through an import or a path
([Paths](#programs.paths)).

The functions of one name that a file sees are overloads of one function, wherever in the module
they are declared ([Overloading](12-functions.md#functions.overloading)).

- It is a compile-time error if a file sees two top-level declarations of one name, of its own or
  of its module's, unless both are functions. So a file-private declaration may share its name with
  a file-private declaration of another file, but not with a name of the module.
- It is a compile-time error if a variable is declared at the top level
  ([Statics](06-variables.md#variables.statics)).

**Example:**

```java unchecked
// orders/order.wyl
pub struct Order { ... }
String describe(Order o) { ... }    // file-private

// orders/queue.wyl
String describe(Order o) { ... }    // file-private here too: no conflict
module struct Order { }              // error[WYL0034]
```

<a id="programs.imports"></a>
## 3.5 Imports

```ebnf
Import     = [ Visibility ] "import" Identifier { "::" Identifier } [ ImportTail ] ";" .
ImportTail = "as" Identifier
           | "::" "*"
           | "::" "{" ImportName { "," ImportName } [ "," ] "}" .
ImportName = Identifier [ "as" Identifier ] .
```

An import's path is its identifiers before any `as`, `*`, or `{`. In `import a::b::*;` and
`import a::b::{N, M};`, every name of the path is a module; in `import a::b::N;`, every name but the
last is, and `N` is the name the import brings in. The first module is one directly in `src/`, the
root module of a dependency of the file's package, or the package `std`, and each after it is a
module within the one before. An import of one name with no module before it, such as
`import Config;`, brings in a module directly in `src/`, the root module of a dependency, the
package `std`, or a name of the root module. An import never starts relative to its file.

An import brings names into its file:

- `import a::b::N;` brings in `N`, the name `N` of module `a::b`.
- `import a::b::{N, M};` brings in each name listed.
- `import a::b::*;` brings in every name of `a::b` that is visible to the file.
- `as` gives the name another name in the file: `import a::b::N as K;` brings in `K`.

A name brought in means everything its module's name means in both namespaces: a module, and a
type or functions ([Paths](#programs.paths)). `import std::panic;` brings in both the module
`std::panic` and the function `panic` that `std` re-exports.

An import is seen only in its own file. An import with a visibility also **re-exports** the name:
the name is also one of the module's names, visible as far as the import's visibility. A name a
module re-exports is imported from that module.

In a file, a name means the first of these that has it, which hides the rest: the module's names
and the file's own declarations; the names its imports bring in by name; the names its `*` imports
bring in; the names every file sees ([Names every file sees](#programs.builtin-names)). So functions
of one name found in two of these are never overloads of each other.

- It is a compile-time error if a name of an import's path that is a module is not one, or if a
  name it brings in is not a name of its module.
- It is a compile-time error if an import brings in a type or function under a name that its file
  or module declares, or a module under the name of a submodule of its module.
- It is a compile-time error if two imports of a file bring in the same name by name, or two
  re-exports of a module give the same name.
- It is a compile-time error if two `*` imports bring in different meanings of a name, where the
  name is used. Where it is not used, the two imports are valid.
- It is a compile-time error if an import comes after a declaration, has a modifier other than a
  visibility, or has an annotation other than `@SuppressWarnings`
  ([`@SuppressWarnings`](22-annotations.md#annotations.suppress-warnings)).

**Example:**

```java unchecked
import std::Weak; import std::collections::List;
import orders::Order;
import orders::format;                 // the module: format::text(o)
import orders::format::text as show;
pub import orders::Status;             // also a name of this module
```

<a id="programs.paths"></a>
## 3.6 Paths

A **path** names something in a module: `orders::format::text(o)`. Each name before a `::` is a
module, and the name after the last `::` is a name of that module. `::` is used only between a
module and a name in it; `.` reaches into a type or a value
([Member access](13-expressions.md#expressions.members)): `orders::Order.empty()` calls the static
method `empty` of the type `Order` of module `orders`.

Modules have a namespace of their own. A path's first name is looked up only among modules, so a
module may have the name of a type, a function, or a local variable.

The first name of a path is a module the file sees. A path's first name means the first of these
that has it: a submodule of the file's module, a module that the file's module re-exports, a module
that the file imports, the root module of a dependency of the file's package, and the package `std`.
Every file sees its package's dependencies and the package `std` without an import.

- It is a compile-time error if a path's first name is not a module the file sees, including a
  module directly in `src/` that the file does not import.
- It is a compile-time error if a name before a later `::` is not a module within the one before.

**Example:**

```java unchecked
// orders/format/text.wyl
import orders::Order;
import std::memory;

pub String text(Order o) {
    int memory = 0;                    // a local and the module 'memory' don't conflict
    memory::collectCycles();
    std::print("formatting");          // std needs no import
    return orders::describe(o);        // error[WYL0046]
}
```

<a id="programs.visibility"></a>
## 3.7 Visibility

```ebnf
Visibility = "module" | "package" | "pub" .
```

A declaration's **visibility** says where it may be used:

| Written | Visible in |
|---|---|
| nothing | Its file |
| `module` | Every file of its module: its directory, not the directories below it |
| `package` | Its package |
| `pub` | Every package |

Visibility applies alike to top-level declarations and to members. A member is visible only where
its type is visible too: a `pub` method of a class marked `module` is visible in the class's module.
A module and the modules below it see each other as any two modules do: a submodule sees its
parent's `package` and `pub` names, but not its parent's `module` names.

A type's members without a visibility are visible in the rest of the type's file, including in the
file's other types. A subclass sees the members of its base class that its file sees, as any other
code does. A method of a subclass with the signature of a base class method that is not visible to
it declares a method of its own, which does not override the base class's
([Overriding](08-classes.md#classes.overriding)).

The visibility comes first among a declaration's modifiers, after its annotations:
`@CallerLocation pub static void check(...)`. Members of a type may be declared in any order.
`main` may have any visibility ([The entry point](#programs.entry)).

- It is a compile-time error if a declaration is used where it is not visible: by its name, by an
  import, as a member, or as a field a pattern matches.
- It is a compile-time error if a declaration has two visibilities, if its visibility comes after
  another modifier, or if it repeats a modifier.
- It is a compile-time error if a visibility is given to a block of members, as in `pub { ... }`.
- It is a compile-time error if a declaration is marked `public`, `private`, or `protected`, or
  if its visibility is written `pub(...)`.
- It is a compile-time error if a destructor, an enum variant, or an interface method has a
  visibility. A variant or an interface method is as visible as its enum or interface
  ([Destructors](08-classes.md#classes.destructors), [Variants](09-enums.md#enums.variants),
  [Interfaces](10-interfaces.md#interfaces.declarations)).
- It is a compile-time error if a re-export is visible farther than the name it re-exports.

**Example:**

```java
pub class Account {
    pub String owner;
    mut int balance;                          // this file only

    package void credit(int amount) { balance += amount; }
}
```

<a id="programs.visibility.signatures"></a>
### 3.7.1 Visible signatures

A declaration **reaches** as far as both it and the type it is a member of are visible.

It is a compile-time error if one of these names a type that is visible less far than its
declaration reaches:

- a function's or constructor's parameter types and return type;
- a field's type;
- an enum variant's field types;
- a class's base class, and the interfaces a type implements;
- the bounds of a declaration's type parameters.

A type is visible as far as it and each of its type arguments are: `List<Order>` as far as `Order`.

**Example:**

```java
struct Ticket { int id; }                 // file-private

pub Ticket next() { ... }                 // error[WYL0056]

struct Local {
    pub Ticket current;                   // valid: 'Local' is file-private, so the field is too
}
```

<a id="programs.builtin-names"></a>
## 3.8 Names every file sees

Every file sees these declarations of `std` without importing them:

- `String` ([`String`](04-types.md#types.string)), `Array` ([Arrays](04-types.md#types.arrays)),
  `Span` and `ReadOnlySpan` ([Spans](04-types.md#types.spans));
- `Result` ([`Result`](18-errors.md#errors.result));
- the interfaces `Spannable`, `ReadOnlySpannable` ([`Spannable`](a-library.md#library.spannable)),
  `Borrowable` ([`Borrowable`](17-memory.md#memory.borrows.borrowable)), `Printable`
  ([`Printable`](a-library.md#library.printable)), and `Comparable`
  ([`Comparable`](a-library.md#library.comparable)).

Every other declaration is reached through an import or a path, `print` and `List` among them.

These names always mean these declarations:

- It is a compile-time error if a declaration outside `std` has one of these names: a type, a
  function, a method, a field, an enum variant or its field, a type parameter, a parameter, a local
  variable, or a pattern's binding.
- It is a compile-time error if an import outside `std` brings in anything under one of these names
  but the declaration of that name: `import std::Result;` is valid, and `import std::collections::List as Result;`
  is not.

<a id="programs.entry"></a>
## 3.9 The entry point

A program's **entry point** is the function named `main` declared at the top level of its root
module. It returns `void` or `int`, and takes no parameters or one by-value parameter of type
`String[]`: `void main()`, `int main()`, `void main(String[] args)`, or `int main(String[] args)`. A
function named `main` in any other module is an ordinary function. A library has no entry point.

A `String[]` parameter holds the program's **arguments**, in order, without the program's name.
Each is UTF-8 text: a part of an argument that is not well-formed text is replaced by U+FFFD, one
for each maximal ill-formed subsequence, as Unicode recommends (*The Unicode Standard*, section 3.9,
"U+FFFD Substitution of Maximal Subparts"). The arguments are an input of the run
([Observable behavior](01-introduction.md#introduction.observable)).

- It is a compile-time error if the root module of a program has no function named `main`, or has
  more than one.
- It is a compile-time error if the root module of a library has a function named `main`.
- It is a compile-time error if `main` has type parameters, or parameters other than one by-value
  `String[]`.
- It is a compile-time error if `main` returns a type other than `void` or `int`.

**Example:**

```java
int main(String[] args) {
    if (args.length == 0) {
        std::print("usage: greet <name>");
        return 2;
    }
    std::print($"hello, {args[0]}");
    return 0;
}
```

<a id="programs.execution"></a>
## 3.10 Running a program

A program runs on its **main thread**. First, every static of the program is initialized, as
[Statics](06-variables.md#variables.statics) gives. Then `main` is called.

The program ends when `main` returns, after `main`'s locals and temporaries have been released as at
the end of any function ([Releasing](17-memory.md#memory.release)). Then:

- No static is destroyed, and no object that only a static refers to is released.
- The cycle collector does not run ([Cycles](17-memory.md#memory.cycles)).
- A thread still running stops where it is, and runs no destructor and no deferred statement. A
  `Thread` that `main` still holds is joined when it is released, before `main` returns.

The exit status of a program that ends this way is the value `main` returns, or 0 if `main` returns
`void`. How the target reports a value outside 0 to 255 is implementation-defined
([Implementation-defined behavior](01-introduction.md#introduction.implementation-defined)). A
program that panics or aborts ends as [Program behavior](01-introduction.md#introduction.behavior)
gives.
