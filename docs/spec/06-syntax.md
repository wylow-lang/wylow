# 6. Syntax

Wylow syntax is Java's unless stated otherwise. This chapter covers the differences and additions.

## 6.1 Packages, modules, imports

Code is organized as in Go and Swift, with Rust's explicit `pub`: a **package** is a project, a
**module** is a directory of it, and **visibility** starts at the file.

```
mygame/
    package.wylcfg        the manifest: the package's name, and later its dependencies
    src/
        main.wyl          the root module
        world/
            chunk.wyl     module world
            mesher.wyl    module world
            gen/
                noise.wyl module world::gen
```

```java
// src/world/chunk.wyl
import world::gen::Noise;
import std::memory;

pub struct Chunk {
    pub int x, z;
    byte[4096] blocks;                  // file-private: only chunk.wyl sees it

    pub byte get(int i) { return blocks[i]; }
}

pub(module) int chunkIndex(int x, int y, int z) { ... }   // every file of 'world' sees it
```

```java
// src/main.wyl
import std::print;
import world::Chunk;
import world::gen;                      // a module itself

void main() {
    var c = Chunk(x: 0, z: 0);
    print(gen::seed());
}
```

- **A package** is the unit of compilation and of dependencies, like a Rust crate: a directory with
  a `package.wylcfg` manifest and a `src/` directory. A single `.wyl` file compiled on its own is a
  package with one module and one file, and no name, so no other package can depend on it. Messages
  and panics name a file by its path under `src/`: `world/chunk.wyl:3:5`. The whole package is
  compiled together, so declarations
  may refer to each other in any order and across files, and modules of one package may import
  each other in a cycle. Packages may not depend on each other in a cycle.
- **A module is a directory.** `src/` is the root module, and each directory below it is a module
  named by its path: `src/world/gen/` is `world::gen`. Every `.wyl` file in the directory belongs to
  the module, and a file doesn't declare which: there is no `package` line. Module names are
  `snake_case` (§6.11). A directory holding `.wyl` files must be named like a module (an identifier,
  not a keyword); one holding none, and one whose name starts with `.`, is no module.
- **A module's names** are the declarations its files make visible beyond themselves
  (`pub(module)` or wider, §6.2), and its submodules. Two files of a module may not declare the same
  such name, nor may one declare a name another keeps private, since that file would see two meanings
  of it. A function's overloads are one meaning, so they may be declared in any of the module's
  files (`lerp(Vec3)` beside `Vec3`, `lerp(Color)` beside `Color`), but no file may see one
  signature twice. A file-private declaration is not one of the module's names, so two files may
  each have a private helper of the same name.
- **Within a module**, a file sees the module's names without importing them, as Java's classes
  see their package. Everything else is reached through an import.
- **Any number of declarations per file**, of any visibility. Types, functions, and constants may
  all be top-level.

### Imports

```java
import world::Chunk;                    // one name
import world::{Chunk, Block};           // several
import world::*;                        // every name of 'world' visible here
import physics::Body as PhysicsBody;    // renamed
import engine::math;                    // the module itself: math::clamp(x)
pub import world::Chunk;                // re-exported: also one of this module's names
```

- An import's path starts with a top-level module of this package, or with the name of a package
  it depends on; `std` is always one. A top-level module named like a dependency is an error, so a
  path's first name has one meaning. A name of the root module is imported by itself:
  `import Config;`. An import's path walks directories; a name another module re-exports is
  imported from the module that re-exports it.
- An import is visible in its own file only, as in Java and Rust. A visibility on it (`pub import`,
  `pub(package) import`) also makes the name one of the module's, visible that far, which is how a
  package presents a public surface whose layout differs from its directories.
- **`::` walks modules, and `.` reaches into a type or a value**, so each has one meaning:
  `math::clamp(x)` is a module's function, `Vec3.zero()` a type's static method, `Color.Red` a
  variant, and `v.length()` a value's method. A type in another module is
  `world::Chunk.make()`.
- **Modules have a namespace of their own.** A path is told apart from a value by its `::`, so a
  module may share its name with a type, function, or local: `panic::checkIndex(i, n)` and
  `panic("message")` don't conflict, and neither does a local named `math` with `math::clamp`.
- **A path in code starts with a module the file already sees**: one it imported, or a submodule
  of its own module, which is one of the module's names. In `src/main.wyl`, `world::Chunk.make()`
  needs no import, but in `src/world/gen/noise.wyl` it needs `import world;` (or
  `import world::Chunk;` and then `Chunk.make()`), and `std::memory::free(p)` needs `import std;`:
  the usual way is to import `std::memory` and write `memory::free(p)`. So a file's imports list
  every module outside its own that it uses. Imports themselves always start at the top, never
  relative to the file.
- An import brings in what its last name names in each namespace: `import std::panic;` is the
  module `std::panic` (`panic::checkIndex(i, n)`) and the function `panic` that `std` re-exports
  (`panic("message")`).
- Importing a name that isn't visible to the importing file is an error that says how far it is
  visible.
- **One name, one meaning in a file.** An import of a name the module already has (its own
  declaration in any of its files, or a submodule) is an error at the import, and so is importing
  one name twice; `as` renames one of them. Names brought in by `*` give way to the module's own and
  to named imports, and two `*` imports that both bring in a name are an error only where the name
  is used. A name hides every declaration of the same name from elsewhere, so a module's own
  `print(Vec3)` is the only `print` it sees: overloads never merge across where they came from.
- **There is no prelude**: a file names what it uses. The exception is what the language itself is
  built on, which every file sees without importing: the types the compiler knows by name because
  the syntax needs them (`String` for literals, `Array` for `T[]`, `Span` and `ReadOnlySpan`,
  `Result` for `try`, and the interfaces `Spannable`, `ReadOnlySpannable`, `Borrowable`,
  `Printable`, and `Comparable`). Everything else is imported, `print` and `List` included. A
  module's own declaration of one of these names is an error, as redeclaring a builtin type is.
- **`std::panic`** holds what is about panics (§1.3): `panic(message)`, which `std` re-exports, and
  the checks the built-in arrays make, for a container whose index isn't its storage's (a ring
  buffer on an `Array`): `checkIndex(index, length)` and `checkRange(start, count, length)`, which
  report the location of a `@CallerLocation` caller as the built-in checks do. A message is all a
  panic carries: it is a bug, not a value to recover (§1.3).
- **`std::memory`** holds raw memory (`allocate`, `free`, `initialize`, ..., all `unsafe`, §7) and
  the cycle collector (`collectCycles`, `cycleStats`, §3).

> Implemented, but for extensions found through imports (extensions aren't implemented). Only
> the standard library's sources are in modules other than a root so far: `std::memory`,
> `std::panic`, and `std` itself.

### How messages name types

An error, a warning, or a runtime diagnostic names a type by its name alone, as code that imported
it would: `Chunk`, `List<Node>`. Only when another type of the program has the same name is each
of them named by its package and module path (`voxel_game::world::Chunk`,
`voxel_game::world::gen::Chunk`, `std::List`), as rustc trims paths; a lone file's package has no
name, so it gives the module path alone. File-private types of one name in one module add their
file: `voxel_game::Node (enemy.wyl)`. So a message is short where a name has one meaning, and
unambiguous where it doesn't.

### Functions or static methods

Both exist, for different things. A **free function** belongs to a concept, and lives in the
module named for it: `math::clamp(x, lo, hi)`, `noise::sample(p)`. A **static method** belongs to a
thing, a type: its factories and constants, `Vec3.zero()`, `Color.WHITE`. A class with only static
members, written to hold functions, is a module in disguise; make it a module.

### Entry point

A top-level `void main()` or `int main(String[] args)` in the root module.

### The manifest

`package.wylcfg` says what the package is, in Wylow's own tokens: comments, string literals with
their escapes, and identifiers are read exactly as in a `.wyl` file, and line breaks don't matter.

```
// voxel_game/package.wylcfg
name = "voxel_game";

dependencies {
    engine { path = "../engine"; }
}

native {
    include = ["third_party/sdl/include"];
    link = ["SDL3"];
}
```

- A file is a list of entries. An entry is `key = value;` or `key { entries }`, and a value is a
  string or a list of strings, `["a", "b"]`.
- The keys are fixed. An unknown key, a repeated one, or a value of the wrong kind is an error, so
  a misspelled key fails rather than being ignored.
- `name` is required: the package's name as other packages write it, `voxel_game::world`. It is
  written like a module's name (an identifier, `snake_case` by §6.11), is not `std`, and need not
  match the directory's name.
- `dependencies` names other packages, each found by a `path` relative to the manifest. Packages
  may not depend on each other in a cycle.
- `native` gives the include directories and the libraries to link for `extern` code (§7).
- What the files already say isn't written again: a package with a `main` in its root module
  (§6.1, Entry point) is a program, and one without is a library; the sources are always `src/`.
- Build settings (`--release`, `--cc`, `--panic=abort`) are the build's, not the package's, and
  stay on the command line. A version and a language edition wait until a registry and
  stability promises give them a meaning.

> Implemented: `name`. `dependencies` and `native` are read and checked against their keys, then
> reported as not supported yet. A package needs its `main` until libraries can be depended on.

## 6.2 Visibility

Everything is private to its file unless it says otherwise:

| Written | Visible to |
|---|---|
| nothing | The file it's declared in |
| `pub(module)` | Every file of its module: its directory, not the directories inside it |
| `pub(package)` | The whole package |
| `pub` | Everyone, including other packages |

```java
pub abstract class Entity {
    pub Vec3 position;
    int ticksAlive;                                 // this file only

    pub abstract void onUpdate(float dt);           // game code overrides it
    pub(package) virtual void onPhysics(float dt) { }   // only the engine's package overrides it
    pub(package) void tick(float dt) { ticksAlive++; onPhysics(dt); onUpdate(dt); }
}
```

- A module and the modules inside it see each other only as any two modules do: `world::gen` reaches
  `world`'s names that are `pub(package)` or wider, never its `pub(module)` ones.
- The same levels apply to top-level declarations and to members. A member is visible only where
  its type is too: a `pub` method of a `pub(module)` class is visible within the module.
- **There is no type-private level.** A type's unmarked members are visible to the rest of its
  file, including other types there. A type whose internals must be sealed from its neighbours
  goes in a file of its own.
- **There is no `protected`.** A subclass sees what its file and module see, like any other code.
  A method that subclasses elsewhere override is as visible as those subclasses need, often `pub`.
  A method that isn't visible can't be overridden.
- **An override is written with exactly the visibility of the method it overrides**, and an
  interface method's implementation with the interface's visibility, so a method's reach is the
  same in the base class and every subclass.
- **A visible signature names only types visible as far:** a function's parameter and return types,
  a field's type, a variant's field types, a class's base class and interfaces, type parameters'
  bounds, and their type arguments. A `pub` function returning a file-private type is an error,
  since a caller couldn't name what it got. "As far" is how far the declaration really reaches,
  within its type's: a `pub` method of a file-private struct may take a file-private type.
- A type's memberwise constructor is as visible as its least visible field. A `pub` struct with a
  private field can be named everywhere but built only where the field is visible, or through a
  constructor or factory that is `pub`. A class's implicit parameterless constructor is as visible
  as the class.
- **A call chooses only among the overloads visible where it is**, as in Java: one it can't see
  never wins over one it can. If none is visible, the error says how far the one that fits reaches.
- The entry point needs no visibility: the compiler, not other code, calls `main`.
- Members may be declared in any order. A struct's fields are laid out in declaration order (§2.9),
  which is also the order of the memberwise constructor's parameters (§6.3).
- The visibility comes first among a declaration's modifiers, after its annotations:
  `@CallerLocation pub static void check(...)`.
- The destructor takes no visibility, since only the runtime calls it. An enum's variants and an
  interface's methods take none either: they are as visible as the enum or interface.

## 6.3 Construction

> **`new` appears only where memory is heap-allocated.** Structs are constructed without `new`.

```java
var v = Vec3(1, 2, 3);                         // struct constructor, no allocation
var e = Enemy(health: 50, position: v);        // fields by name
var list = new List<Enemy>();                  // class: heap allocation
var arr = new float[1024];                     // heap array
```

This makes every allocation visible: scanning for `new` finds them all.

A local must be assigned before it is read, even when its type has a default value, so that a
branch that forgets to assign it is an error rather than a silent zero. Declaring it makes no value,
so a struct's field initializers don't run for it (a fixed-array local is the exception; see
[02-types.md](02-types.md)). `var` keeps this short where the type is visible, as in
`var v = Vec3();`; write the type out where the value doesn't show it, as in
`Vec3 target = path.last();`.

### Struct constructors

A struct is built one way: by calling a constructor. There is no separate initializer syntax such
as C#'s `T { field = value }`; the memberwise constructor with named arguments does that job, so
default values, positional construction, and setting fields by name are all the same call.

- A struct that declares no constructor gets a **memberwise constructor**, which takes every field
  in declaration order: `Vec2(1, 2)` for `struct Vec2 { pub float x, y; }`. Each parameter defaults to
  its field's initializer, or else to the field type's default value, so arguments may be left out
  or passed by name (§6.4):

  ```java
  struct Enemy {
      pub Vec3 position;
      pub int health = 100;
      pub Player? target;
  }

  Enemy()                    // every field defaulted
  Enemy(pos)                 // health 100, no target
  Enemy(health: 50)          // position defaulted
  ```

  A field with no initializer whose type has no default (a non-null reference, a type parameter, or
  a struct containing one) is a required argument. The memberwise constructor is as visible as its
  least visible field.
- Arguments are evaluated in the order written, as in any call. Then each field is set, in
  declaration order, to its argument or, for one left out, to its initializer. Initializers may call
  functions but cannot use `this` or other fields.
- **`T()` is the default value.** It exists for a struct whose fields all have defaults, including
  one that declares constructors, since arrays rely on it, and it cannot be declared.
- Declaring any constructor removes the memberwise one, apart from `T()`. A declared constructor
  starts from the default value, then runs its body, unless it starts with `this(...)`: it then
  starts from the value the constructor it calls makes (which may be `T()`). The arguments of
  `this(...)` cannot use `this`, and a constructor whose `this(...)` chain comes back to it is an
  error, as for classes. `this` refers to the value being built, and
  `return;` ends construction early. To set fields its parameters don't cover, assign them after the
  call: `mut var e = Enemy(pos); e.health = 50;`.
- Constructors are declared Java-style:

```java
struct Enemy {
    pub Vec3 position;
    pub int health = 100;

    pub Enemy(Vec3 pos) { position = pos; }
}
```

### Class constructors

Java-style, including `super(...)` and `this(...)` chaining:

```java
class Shotgun extends Weapon {
    pub Shotgun(int pellets) { super(1.0); this.pellets = pellets; }
    int pellets;
}
```

Classes are always created with `new`, and structs never are. The order of construction, what
`this(...)` runs, and what a constructor may do before its fields are assigned are in
[02-types.md](02-types.md#construction).

## 6.4 Methods and parameters

### Parameter modes

| Mode | Meaning | Call site |
|---|---|---|
| `T x` | By value (structs are copied) | `f(x)` |
| `ref T x` | Mutable reference | `f(ref x)`; the `ref` keyword is **required** |
| `in T x` | Read-only reference (no copy, no mutation) | `f(x)` |

```java
void applyDamage(ref Enemy e, in DamageInfo info) { e.health -= info.amount; }

applyDamage(ref enemy, info);
```

`ref` and `in` parameters follow the reference rules in
[03-memory-model.md](03-memory-model.md#33-references). `in` is intended for large read-only structs
such as matrices. On class-typed parameters, `ref`/`in` are not meaningful (a class is already passed
by reference) and are a compile error. So is a function-typed parameter, nullable or not, since a
function is already passed without copying.

- A `ref` argument must be a mutable place: a `mut` local or parameter, a field path rooted at
  one (`ref enemy.position`), an element (`ref points[i]`), or a reference a method returned. Its
  type must match the parameter exactly; no conversions apply.
- A place is read-only, and cannot be assigned or passed by `ref`, if it is rooted at an `in`
  parameter or variable, a variable, parameter, or field that isn't `mut` (§2.7), `this` inside a
  struct method that isn't `mut`, or a call that returns `in`.
- Arguments are evaluated left to right, and a `ref` argument is a reference to the place, not a
  snapshot of its value: in `x + increment(ref x)`, the left `x` is read before the call.

### `mut` struct methods

A struct method can't change `this` unless it is declared `mut`, as nothing else can change
without `mut` (§2.7); in a `mut` method, `this` is a `ref`. Interface methods may be `mut` too
([02-types.md](02-types.md#212-interfaces)). A `mut` method can't be called on a read-only place:
through an `in` parameter or a `ReadOnlySpan<T>`, or on a variable that isn't `mut`.

A temporary struct or fixed array (one a call or constructor returned, a `?:` of two values, `v!`)
is a value, not a variable, so nothing writes to it: what the write changed would be gone after
the statement. Its `mut` methods can't be called, its fields and elements can't be assigned (through
`operator []=` either), it can't be passed by `ref` or referred to, and a `ref` loop can't iterate it;
store it in a `mut` variable first. This is Swift's and Go's rule; C#, C++, and Rust allow some of these
writes, which then change a copy no one sees. A write that reaches an object through a temporary
is kept, as in `makeList().add(1)` or a struct's `operator []=` that isn't `mut`.

```java
struct Vec3 {
    pub float x, y, z;

    pub float length() { return sqrt(x*x + y*y + z*z); }
    pub mut void normalize() { var l = length(); x /= l; y /= l; z /= l; }
}
```

Class methods take no `mut`: calling one never changes the reference it is called through, and
what it may change in the object is decided by which fields are `mut`. Enum methods take none
either, since an enum value can't be changed in place. Static methods have no `this`.

### Overloading, defaults, named arguments

```java
void spawn(Vec3 pos, int count = 1, float spread = 0.5) { ... }

spawn(pos);
spawn(pos, 5);
spawn(pos, spread: 2.0);     // named argument
spawn(pos, count: 3, spread: 2.0);
```

- Methods may be **overloaded** by parameter types, as in Java.
- Parameters may have **default values**: a constant (a literal, an enum variant, or an operator
  applied to constants) or a struct constructor given constants, as in `Vec3 up = Vec3(0, 1, 0)`.
  A call that leaves the parameter out passes its default, evaluated at the call.
  - A `ref` parameter cannot have a default, since it must refer to a variable.
  - In generic code, a parameter whose type involves a type parameter can only default to `null`.
  - A struct constructor cannot give every parameter a default, since `T()` is always the
    struct's default value (§6.3).
- Arguments may be passed **by name**, as in C#. Positional arguments come first. Named ones follow
  **in the order of the parameters**, as in Swift, and may skip parameters that have defaults:
  `spawn(pos, spread: 2.0, count: 3)` is an error. So the order an argument list is written in is
  the order its values are evaluated in: each parameter's argument, or its default if left out.
  A struct's memberwise constructor takes both, which is how a struct is built with some fields
  named (§6.3).
- Overload resolution considers the overloads that can take the arguments by count and name, then
  compares the parameters that are given arguments. Among equally good overloads, one that leaves
  fewer parameters to their defaults wins, as in C#, then a non-generic one.
- A method that overrides or implements another takes that method's defaults and cannot declare
  its own, as in Kotlin, so a call passes the same values whatever type it is made through. Named
  arguments use the parameter names of the method the call finds, in the type it is made through.
- A function type's parameters have no names or defaults, so a function value is called with every
  argument, positionally. A method used as a function value takes all its parameters.

### Extension methods

A function whose first parameter is marked `this` can be called like a method of that parameter's
type, as in C#. It adds methods to types its author can't change, and to interfaces, which can't
have method bodies or generic methods themselves (§2.12).

```java
bool any<T>(this ReadOnlySpan<T> items, fn(T) -> bool test) {
    for (in T item : items) {
        if (test(item)) {
            return true;
        }
    }
    return false;
}

void scale(this ref Vec3 v, float factor) { v.x *= factor; v.y *= factor; v.z *= factor; }

if (mobs.any(m -> m.health <= 0)) { ... }
position.scale(2);
```

- The receiver takes a parameter mode: `this T` (a copy, or a reference for a class), `this in T`,
  or `this ref T`, which needs a mutable place, as calling a mutating struct method does, but no
  `ref` at the call. `this ref` and `this in` can't be used with class types, as for parameters.
- **Members win.** If the receiver's type has a method or field of that name, its
  extensions are not considered, so an extension can never change what existing code calls. Among
  extensions, overload resolution chooses as for functions.
- The receiver converts to the `this` parameter's type by identity, upcasts, and the implicit span
  conversions (§2.11), so one extension on `ReadOnlySpan<T>` serves fixed arrays, `T[]`, `List`,
  and spans. No other conversions apply. Type arguments are inferred from the receiver as from any
  argument.
- A nullable receiver needs `?.` or a check, as for members, unless the `this` parameter's type
  is itself nullable: `bool isNullOrEmpty(this String? s)` can be called on a `String?` directly.
- An extension is an ordinary function: it sees the receiver's members that its own file can see, is statically
  dispatched (never virtual), and can also be called as `any(mobs, test)`.
- An extension is found where it is visible and its name is imported, or within its own module.
  Until programs have more than one file, every extension in the program and the standard library
  is visible.

> Not implemented yet. The standard library will use extensions to move `String`'s higher-level
> methods into Wylow over its native primitives, `bytes()` among them, so the C++ runtime no longer
> refers to the standard library's `ReadOnlySpan`.

## 6.5 No properties

Wylow has no properties (C#'s `get` and `set`), as Java has none. A value is either a field, read
and assigned directly, or a method, called with parentheses:

```java
class Player {
    pub mut int health = 100;
    pub bool isDead() { return health <= 0; }
}

if (player.isDead()) { ... }
```

A class field that isn't `mut` can be read anywhere but assigned only by its class's constructors,
which covers what a read-only property would: `array.length`. A struct's fields are as mutable as
the variable holding it (§2.7), so a struct whose fields must keep an invariant leaves them unmarked, private to its file,
and offers methods: `span.length()`. Turning a field into a method later means updating its uses.

## 6.6 Indexers

There is no operator overloading: an operator always means what it means for the built-in types,
as in Java, so `a + b` never runs user code. Vector math is written with methods
(`a.add(b)`, `v.scale(s)`). The one exception is indexing: any struct or class may declare an
indexer, so that a user's container indexes like the standard library's.

```java
struct Mat4 {
    pub float[16] m;
    pub mut ref float operator [](int row, int col) { return ref m[row * 4 + col]; }
}
```

An indexer is an instance method named `operator []`, as in C++, so `m[1, 2]` calls
`m.operator [](1, 2)`. One returning `ref` gives mutable access to an element (see the `ref` return
rule in [03-memory-model.md](03-memory-model.md#returning-references)), and one returning `in`
gives read-only access.

An indexer may return `ref`, `in`, or a value, and take any number of parameters.

Where there is no element to refer to, such as in a packed array of 4-bit values, `operator []=`
handles assignment: it takes the indices and then the assigned value, and returns `void`.
`grid[i] = v` calls it, and `grid[i] += v` and `grid[i]++` read with `operator []` and write with
`operator []=`. A type may not declare both `operator []=` and an `operator []` that returns `ref`,
inherited or not, so an assignment never has two meanings. Such an element is not a place:
`ref grid[i]`, passing `grid[i]` by `ref`, and returning it with `return ref` are errors.

```java
struct Nibbles {
    byte[2048] bytes;

    pub byte operator [](int i) { return bytes[i >> 1] >> ((i & 1) * 4) & 0xF; }
    pub mut void operator []=(int i, byte value) { ... }
}
```

As in C#, where one indexer declares both its `get` and its `set`, an `operator []=` is the writing
half of an indexer:

- Each `operator []=` pairs with the `operator []` whose index parameters have the same types and
  modes, which returns a value or `in` (not `ref`, nor `in?`), and its value parameter has that
  indexer's element type. A type with only `operator []=` is an error.
- An assignment resolves its indices against the `operator []` overloads, with conversions,
  default values, and named arguments as for any call, then takes the paired `operator []=`. So
  `operator []=` declares no default values: its indices take the reading indexer's.
- The value converts to the value parameter as an argument does. It is passed by value or `in`,
  never `ref`.
- The receiver, then each index, is evaluated once, before the value; a compound assignment reads
  the element with `operator []` after the indices and before the value
  ([03-memory-model.md](03-memory-model.md#evaluation-order-and-the-heap)). A by-value or `in` index
  is evaluated into a temporary that both calls take, so the value can't change it in between.
- It is an instance method, which may be `virtual` or declared in an interface, and may be less
  visible than its `operator []`, as C#'s `private set`. On a struct it needs `mut` to change the
  struct, and then a mutable place, not a temporary, as any `mut` method call does (§6.4); one that isn't `mut` suits a
  struct that writes through a class it refers to. A class receiver needs neither.
- Indexing a type parameter, to read or to assign, is not supported yet.

### Equality

| Kind | `==` means |
|---|---|
| Struct | Memberwise equality, **generated automatically** if every field supports `==`; it cannot be redefined |
| Class, interface | **Reference identity** (same object), as in Java. Value equality is `equals()`, from an `Equatable<T>` interface beside `Comparable<T>`, designed with the standard library. Allowed only when one side could be the other (or is `null`) |
| Enum | Same variant and equal data |
| `String` | **Same contents** (the same bytes), as in C#, unlike Java's identity: a literal equals a string built at run time with the same text |

## 6.7 Lambdas and function types

Lambda syntax is Java's. Function types are written `fn(Params) -> Return`, a built-in type like
`int`, so a function type never looks like a lambda:

```java
fn(int, int) -> int add = (a, b) -> a + b;
list.sort((a, b) -> a.depth - b.depth);
button.onClick(() -> { play(sound); });
```

- A function type's parameters are always parenthesized, even none or one: `fn() -> void`,
  `fn(Mob) -> bool`. They take parameter modes, which are part of the type: `fn(ref Vec3) -> void`.
- The return type is always written, `void` included, and takes any suffix: `fn(int) -> int?`
  returns an `int?`. To make the function itself nullable, or an array of functions, parenthesize
  it: `(fn(int) -> int)?`, `(fn(int) -> int)?[]`. Parentheses in a type are only for that: they
  hold one function type and are followed by `?`, `[`, or `*`, so `(fn(int) -> int) f` is an error.
- `->` groups to the right: `fn(int) -> fn(int) -> int` returns a function.
- A lambda's parameters are either all untyped, taking their types and modes from the target
  (`x -> ...`, `(a, b) -> ...`), or all typed (`(int a, ref Vec3 v) -> ...`). Its body is an
  expression or a block.
- As in Java, a lambda is never an operand: its body extends as far as an expression can, so
  `x -> x + 1` is a lambda returning `x + 1`.
- A lambda has no type of its own: it takes one from where it's used (a declared variable, field,
  parameter, or return), so `var f = x -> x;` is an error. Passed to overloaded functions, it
  rules out those whose parameter takes a different number of arguments, or other types than it
  declares; overloads that differ only in what an untyped lambda's parameters would be are
  ambiguous.
- A generic function's type arguments are inferred first from its other arguments and from the
  parameter types lambdas declare. Those then give each lambda its parameter types, and a type
  argument still unknown comes from what the lambdas return, each returned value counting like an
  argument: `result.map(m -> m.name)` calls `map<String>`. Lambdas are taken in order, each once
  its parameter types are known, so one lambda's result can type the next one's parameters
  (`compose(2, x -> x * 0.5d, y -> y > 1d)`). Where lambdas disagree, the type they all convert to
  wins, as for arguments: `pick(() -> 1, () -> 2L)` infers `long`. A type argument that a lambda's
  parameters were bound with can't change after, so a later lambda that returns a wider type is an
  error.
- Inferring from a lambda's body needs the parameter types of the one overload being called, so it
  happens only when one overload is left after the other arguments are checked. If others are
  left too, the call is an error: specify the type arguments. A lambda returning nothing infers
  nothing, and `try` can't be used in a lambda whose return type is being inferred.
- A function value is called like a function, whether it's a local, a field (`button.onClick(5)`),
  or the result of a call (`adder(2)(3)`).
- A free function or static method used as a value, where a function type is expected, is a
  **function reference**: the lambda that calls it with its arguments, so `list.sort(Mob.byDepth)`
  passes `(a, b) -> Mob.byDepth(a, b)`. The function type's parameters pick the overload as a call's
  arguments would, and the result converts to its return type, or is discarded if that is `void`.
  Otherwise it is a lambda: it is escaping when its target is, a generic function's type argument
  can be inferred from what it returns, and `?:` can choose it. Passed to overloaded functions, it
  is an untyped lambda taking as many parameters as the function does, so it needs a function type
  from where it is used if the function's overloads take different numbers. An instance method
  can't be used as a value (`enemy::update` may come later); use a lambda that calls it.
- `return` in a lambda returns from the lambda; `break` and `continue` can't leave it.
- `?:` and switch expressions choose between functions, wherever a function can be used:
  `fn(int) -> int f = up ? x -> x + 1 : x -> x - 1;`, or `sort(items, up ? ascending : descending)`.
  The choice is made once, when the expression is evaluated. Passed as an argument, a choice
  whose options include lambdas waits for its function type as a lambda does: overloads and
  generic inference treat each option as the argument (`apply(3, up ? x -> x * 2 : x -> x + 1L)`
  infers `long`), and an option that is a function-typed local constrains the type like any other
  argument. Anywhere else, a lambda option without a target type takes another option's function
  type. A lambda a switch expression chooses can't use a name its case declares
  (`case Add(var n) -> x -> x + n` is an error), since the lambda is made outside the case; use a
  switch statement.
- A function type has no default value, so a field of one must be assigned by every constructor
  or be nullable. `(fn(int) -> int)?` is a nullable value, like `int?`, narrowed by null checks.
  Function values have no `==`.
- A lambda can use a local only if it is definitely assigned where the lambda is created. A null
  check outside a lambda doesn't narrow inside it, since the code before the lambda runs could
  change the local; and a local that any lambda assigns is never narrowed, since the lambda may
  run during any call.

A function-typed parameter compiles to a C++ template parameter, so each lambda passed to it is
inlined into the function. Where C++ can't use a template, it is instead a non-owning reference
to the lambda, called indirectly: in an overridden virtual method (one that nothing overrides is
an ordinary method), an abstract or interface method, a class's implementation of an interface
method, a recursive function that passes itself a new lambda (or a choice) on some cycle of calls,
which would otherwise instantiate itself without end, a constructor that shares its parameter
count with another of its type, or a function type's own parameters. A recursive function that
only passes its function parameters on, or escaping values, is still a template:
`walk(child, visit)` instantiates the same one again. Overloads that differ only in function types
are told apart by the C++ name. A function chosen by `?:` or a switch expression holds every
function it could be and the index of the chosen one, so calling it branches to a direct call,
and each lambda can still be inlined.

> Implemented so far: everything above.

### Escaping and non-escaping

A function-typed **parameter or local** is **non-escaping** by default: the function may call it
and pass it to other non-escaping parameters, but may not store it, return it, or pass it to an
escaping parameter. Declaring the parameter or local `escaping` opts in.

| | Non-escaping | Escaping |
|---|---|---|
| Where | Parameters and locals | Fields, return values, `escaping` parameters and locals, containers |
| Allocation | **None** (compiled to an inline template or function reference) | Heap-allocated, refcounted closure |
| Captures | By reference: may capture `ref`s and `Span`s, and may **mutate** captured locals | By copy: captured locals can't be `mut` |

```java
mut int count = 0;
enemies.forEach(e -> { if (e.isDead) count++; });   // non-escaping: mutates local, no allocation

void onEvent(escaping fn() -> void handler) { handlers.add(handler); }

fn(Mob) -> bool isDead = m -> m.health <= threshold; // non-escaping: no allocation
escaping fn() -> void onDone = () -> play(sound);    // escaping: a heap closure, can be stored
handlers.add(onDone);
```

`escaping` is a keyword written before the type, after any `mut`: `mut escaping fn() -> void f`.
It is written only where a function could be either form: on a parameter, a local, or a function
type's own parameter. Anywhere else a function is always escaping, and writing `escaping` is an
error.

The cost of a lambda is visible where it's declared, never inferred from how it's used later: a
non-escaping local stays non-escaping, and storing it is an error that suggests `escaping`.
Like a `ref` local, a non-escaping local is bound once, at its declaration, and can't be
reassigned or declared `mut`, so it can't outlive what its lambda captures. An escaping local can
be reassigned if it is `mut`.

- **Escaping is part of the function type.** Each function type has a non-escaping and an
  escaping form, written the same way. Written as the type of a parameter, a local, or a function
  type's own parameter, it is non-escaping unless that is declared `escaping`, as in
  `fn(escaping fn() -> void) -> void`; anywhere else (a field, a return type, a type argument, an
  array element, or a function type's return type) it is escaping. A nullable function follows the
  same rule, except where noted below. `var` takes the form of its initializer. An escaping value
  converts to the non-escaping form, so it can be passed to a non-escaping parameter or bind a
  non-escaping local; nothing converts the other way.
- **A lambda is escaping when its target is**: the variable, field, parameter, or return type it
  is converted to, or the type of the `?:` or switch expression it is an option of. A lambda
  passed as an argument takes the form of the parameter that overload resolution picks.
- **Type arguments are always escaping**, so a type argument inferred from a function is its
  escaping form: `identity(f)` needs `f` to be `escaping`.
- **Nullable functions.** A parameter of type `(fn(int) -> int)?` is non-escaping unless declared
  `escaping`, so an optional callback is passed without allocating. Like any non-escaping
  parameter it is bound once, and it is narrowed by null checks. Only a parameter can hold one: a
  local of nullable function type must be `escaping`, since a non-escaping local owns the lambda
  it holds and a null one holds none. For the same reason a `?:` or switch expression of nullable
  function type is escaping, so its lambdas are closures, and `??` can't be applied to a nullable
  non-escaping function; check it with `if`.
- **Captures.** An escaping lambda copies the locals and parameters it uses when it is created,
  so none may be `mut` (§2.7): the copy and the variable could then differ. Whether a `mut` local
  actually changes doesn't matter; the declaration decides. A local declared without a value and
  assigned once later can be captured, since it must be assigned before the lambda is created.
  Inside the lambda, the copies are read-only. A for-each or range loop's variable is a new
  variable in each iteration, so it can be captured, but the `mut` counter of a
  `for (mut int i = 0; ...; i++)` loop can't; copy it to a local in the body.
- An escaping lambda can't capture a `ref` or `in` local or parameter, a span or other
  `ref struct`, a non-escaping function, or `this` in a struct's method or constructor (copy what it
  needs to a local first). A captured class reference, including `this`, keeps its object alive.
  So a closure can be part of a cycle, as in `this.onHit = () -> react(this);`, which
  `memory::collectCycles` collects like any other.
- **Choices.** A `?:` or switch expression of escaping type evaluates only the option it chooses,
  like any other. One of non-escaping type makes its lambdas and refers to its non-escaping
  locals up front, which no one can observe; the check a `!` makes runs only where its option is
  chosen, so an option that isn't chosen may still be null, and an escaping option (a field, a
  call, an escaping local) is evaluated only when it is chosen.
- **Signatures.** An override or interface implementation must declare the same parameters
  `escaping` as the method it overrides or implements. Overloads can't differ only in which
  parameters are `escaping`. A function type's `escaping` parameters are part of the type, and
  print as written: `fn(escaping fn() -> void) -> void` and `fn(fn() -> void) -> void` are different
  types, and neither converts to the other. A lambda of the first may store its argument.
- A type pattern can't name a function type (`f is fn(int) -> int g`); narrow a nullable function
  with a null check.

```java
for (Mob m : mobs) {
    handlers.add(() -> m.hit());            // fine: each iteration has its own 'm'
}
mut int total = 0;
escaping fn() -> int f = () -> total;      // error: 'total' is 'mut'
total++;
```

An escaping function is a `wyl::Function`, two words: a pointer to the code that runs the lambda's
body, and a reference to a closure object, with the same header and reference count as a class
object, that holds copies of what its lambda captured. A lambda that captures nothing has no
closure, so making one allocates nothing, and copying it only tests for null; a function reference
captures nothing. Calling a function value is one indirect call. Copying it copies the reference. A
nullable function needs no flag: its null is a null code pointer. The cycle collector
visits the class references a closure holds. A call of a function value stored where the call
could replace it (a field, a list element, a local that a lambda assigns) holds a reference to the
closure until the call returns. A closure that provably can't outlive the scope making it may be
on the stack, as a class object can be ([03-memory-model.md](03-memory-model.md#stack-allocation)).

A nullable non-escaping parameter is a `wyl::Optional` of a `wyl::FunctionRef`, made where the call
is, so that what it refers to (a lambda, or a copy of a stored function) lives until the call
returns. A `wyl::FunctionRef` made from a stored function refers to its closure and calls its code,
so the call is no more indirect than the stored function's.

Non-escaping lambdas are what make `Jobs.parallelFor` and `Mutex.lock` safe
([05-concurrency.md](05-concurrency.md)).

> Implemented so far: everything above.

### Lambdas held by `ref struct`s

A `ref struct` may have fields of function type, which hold non-escaping lambdas: the struct is
second-class ([03-memory-model.md](03-memory-model.md#spans)), so it can't outlive the lambda's
captures either. A function may return such a struct holding its own function-typed parameters,
where it may return a `ref struct` at all. This is what lets a lazy query such as
`mobs.where(m -> m.health < 5)` store its lambda without allocating
([02-types.md](02-types.md#queries)).

Each such field is compiled as a C++ template parameter of the struct, so the optimizer can inline
the lambda into the loop that calls it: `Where<T>` in Wylow is `Where<T, F>` in C++, one type per
lambda. A `ref struct` can't be reassigned or chosen by `?:`, so a variable never needs to hold two
of them; a function taking one as a parameter is compiled once per lambda, as a generic function
is once per type argument.

> Not implemented yet.

### Costs to remove later

What the implementation above pays that it needn't. None of these changes behavior, so each can
be done when profiling shows it matters.

- **Indirect calls where templates can't be used.** A `wyl::FunctionRef` costs an indirect call
  and keeps the lambda from being inlined. It could be avoided by devirtualizing calls whose
  receiver type is known, and for constructors sharing a parameter count, by telling them apart
  with a tag argument.
- **Code size.** Each distinct lambda passed to a template instantiates the callee again, as
  generics do per type. A performance warning could point out large instantiation counts.
- **Pins for locals a lambda assigns.** Such a local is held while in use as a receiver or
  argument (a reference count increment and decrement) even when no lambda that assigns it can
  run during that call. Only calls passed such a lambda, directly or by forwarding, can change it.
- **Evaluation order.** Any call given a function value, and any call of one, is treated as
  writing every local that a non-escaping lambda captures and that is changed anywhere, so operands
  reading one are hoisted into an ordering lambda, though the lambda the call can run may not change
  it. Tracking which locals each lambda changes, and which lambdas each call can run, would avoid
  that. Each hoisting nests a C++ block, so about 25 levels of nested lambdas exceed MSVC's limit on
  block nesting. Real code doesn't nest that deep, so this is a known limit, not a planned fix.
- **Element aliasing and cycles.** A call of a function value counts as writing what every lambda
  of its type writes, so one `fn(Mob) -> void` lambda storing into elements of type `X` anywhere
  makes every loop over `X` that calls a `fn(Mob) -> void` count references per element
  ([03-memory-model.md](03-memory-model.md#loop-elements)); likewise, a function field may hold
  any lambda of its type for the cycle analysis. Both could count only the lambdas that can reach
  that call or field.
- **Narrowing.** A null check outside a lambda doesn't carry into it (except a `ref?` variable's,
  which nothing can make refer elsewhere), and a local declared outside any lambda that assigns it
  is never narrowed, which can force a `!` (and its check).

### Decisions to revisit

*Open*: reasonable on paper; real programs will show whether they get in the way.

- **Bind-once function locals.** A non-escaping local can't be reassigned; `?:` and switch
  expressions cover choosing one.
- **Narrowing.** Carrying a null check on a local that isn't `mut` into a lambda would be sound.
- **`F?` is a nullable value**, not reference-like. Only parameters can be non-escaping `F?`:
  locals, choices, and `??` would need an optional that owns a lambda, which non-escaping locals
  and choices avoid by being bound once or holding every option.
- **Untyped lambdas are ambiguous between overloads** that differ only in function parameter
  types, rather than each overload being tried.
- **Parameter modes are part of a function type**, and an untyped lambda takes them from its
  target; function-typed parameters can't be `ref` or `in`.
- **Expression-bodied `void` lambdas** must have a call as their body, as statements must.
- **Which functions take a `wyl::FunctionRef`**: the rules are simple and conservative, and could
  be wrong in the slow direction often enough to matter.
- **Inferring from a lambda's body needs one overload left.** Two generic overloads whose lambdas
  take the same number of parameters, or a generic and a non-generic one, make
  `x.map(m -> m.name)` an error asking for type arguments. Binding the body once per distinct
  parameter-type list, and sharing it, would lift that.

### Standard library

> Not implemented yet: `List.removeWhere`, and a `sort` on spans that takes a comparison, both
> written in Wylow.

## 6.8 `defer`

`defer statement;` runs the statement when the enclosing scope exits (normally, or via
`return`/`break`/`continue`). Multiple defers run in reverse order.

```java
void render() {
    gpu.beginPass(pass);
    defer gpu.endPass();
    ...
}
```

Owned resources are released by class destructors; `defer` is for paired calls that aren't objects.

## 6.9 Control flow

All of Java's control flow (`if`, `while`, `do`/`while`, C-style `for`, for-each, labeled
`break`/`continue`, `?:`), with these changes and additions.

### Assignment is a statement

`=`, compound assignments (`+=` etc.), `++`, and `--` are statements, not expressions. They
cannot appear inside other expressions, so `a = b = c`, `f(x = 1)`, and `arr[i++]` are compile
errors. This removes a class of evaluation-order bugs. `++` and `--` may be written prefix or
postfix with the same meaning.

### Evaluation order

Operands and arguments are evaluated left to right. This includes reads of object fields relative
to calls; see [03-memory-model.md](03-memory-model.md#evaluation-order-and-the-heap).

### Unreachable statements

A statement that can never run — one after `return`, `break`, `continue`, `panic`, or a call that
never returns — is allowed and does nothing: it is not an error, and the compiler emits no code for
it.

### Mandatory braces

Bodies of `if`, `else`, `for`, `while`, and `do` must be blocks. `else if` is allowed.

```java
if (x) foo();        // compile error
if (x) { foo(); }    // OK
```

### Ranges

```java
for (int i : 0..n)  { ... }    // 0, 1, ..., n-1
for (int i : 0..=n) { ... }    // 0, 1, ..., n
```

- The bounds are integers, evaluated once, start first. The variable has their common type (§2.4),
  or its declared integer type, and is a copy: assigning it doesn't change the iteration.
- A range whose end is before its start runs no iterations. `a..=b` stops without overflowing when
  `b` is the type's maximum value.
- Ranges are only valid in a for-each.

### For-each with keys

Collections that have keys allow a two-variable for-each:

```java
for (ref Enemy e : enemies) { ... }                       // values only
for (int i, ref Bullet b : bullets) { ... }               // List / arrays / spans: index + value
for (String name, int score : scores) { ... }             // Map: key + value
```

A for-each loops over a range, a fixed array, a span, a value that converts to a span
([02-types.md](02-types.md#spans)), such as an `Array` or a `List`, or the characters of a
`String`, whose index is a byte offset ([02-types.md](02-types.md#strings)):

- `T x` copies each element, `ref T x` refers to it, and `in T x` refers to it read-only. `var x`
  copies with the element's type. A `ref` loop needs a mutable source: not an `in` parameter, a
  `ReadOnlySpan`, a fixed array in a variable or field that isn't `mut`, or a temporary fixed array (§6.4).
- Each variable, including a range's, is a new variable in each iteration and is immutable unless
  declared `mut`, as in `for (mut Vec3 p : points)`; changing a copy doesn't change the element.
- `int i, ... x` adds the element's index, a copy of the position.
- The loop holds what it iterates for its whole duration, as a span variable would, so a list is
  borrowed until the loop ends ([03-memory-model.md](03-memory-model.md#borrow-counters-on-containers)).
  A fixed array is referred to, not copied, unless it is a temporary.
- A loop over a value that converts to a span iterates a `ReadOnlySpan` unless its variable is a
  `ref`, so read-only loops work on read-only sources.
- A nullable local can be looped over where it is known not to be null, as for using its members.

> Implemented so far: ranges, fixed arrays, spans, `Spannable` types (`Array`, `List`), and strings.
> Maps don't exist yet.

### `switch`

```java
var label = switch (state) {
    case Idle           -> "idle";
    case Chasing(var t) -> "chasing";
    case Fleeing(_, _)  -> "fleeing";
};

switch (state) {
    case Idle -> wander();
    case Chasing(var t) -> {
        moveToward(t);
        attack(t);
    }
    case Fleeing -> runFrom(threat);
}
```

- Arrow form only (`case X -> ...`). **No fallthrough.** `case A, B -> ...` matches either pattern;
  such a case cannot bind names.
- **Guards**, as in Java: `case Pattern when condition -> ...` matches only if the pattern matches
  and then the condition, a `bool`, is true. `when` is a keyword. The guard runs after the case's
  names are copied (below), so the guard and the case see the same copies. The names the pattern
  binds are in scope in the condition, and the case is as an `if`'s body is to its condition: names
  an `is` in the condition binds are in scope in it, and what the condition narrows stays narrowed.
  Likewise the names of a case whose guard is false live until the switch ends, as an `if`
  condition's names live through its `else`. In `case A, B when c`, the guard applies to the whole
  case. A case with `default` or `null` can't have a guard. A guarded case never counts toward
  exhaustiveness, and never makes a later case unreachable, since its condition may be false; it
  is still an error if the cases above already match everything it matches.

  ```java
  switch (shape) {
      case Circle(var r) when r > 10 -> big();
      case Circle(var r) -> small(r);
      case Rect(var w, var h) -> area(w, h);
  }
  ```

  **Watch out:** a switch matches its value where it is, without a copy of its own, so a guard
  that changes the value, or calls code that does, changes what the later cases test. Each case
  tests the value as it is when that case is tried (including whether it is null, so this is never
  unsafe). To match the value as it was, copy it first:

  ```java
  var snapshot = ai.state;
  switch (snapshot) {
      case Chasing(var t) when ai.calm() -> chase(t);   // calm() may set ai.state
      case Idle -> rest();
      case Chasing(var t) -> follow(t);
  }
  ```

  A hidden copy would cost a large struct's copy on every switch, unless the optimizer removed it,
  so the copy is written where it is wanted.

- **`case null`**, as in Java: a switch with a `case null` takes its value as it is, nullable or
  not. Without one, a nullable value must be narrowed first (§2.5), or it is an error. `case null`
  matches only null, and may be written anywhere among the cases. `default` doesn't match null;
  write `case null, default ->` for both (only `null` may be listed with `default`). The other
  cases' patterns match the non-null value, so `case Enemy e`, `case var v` (a `v` of the non-null
  type), and `case Chasing(var t)` match as they would on a narrowed value. A case's test narrows
  the switched local in that case's body, as an `if`'s would; to name the value, bind it.

  ```java
  switch (findTarget()) {
      case Boss b -> flee(b);
      case null -> wander();
      case var e -> attack(e);
  }
  ```

  A `case null` on a value whose type can't be null is an error, and so is one on a local that
  narrowing knows isn't null there, such as one assigned a value or tested just before: the case
  could never match. A `ref?` or `in?` reference is tested with `is ref` instead
  ([03-memory-model.md](03-memory-model.md#nullable-references-ref-t)), not
  switched on with `case null`.
- A **switch statement**'s case is a block or a single statement other than a declaration. A
  **switch expression**'s case is an expression followed by `;`. A case that cannot complete, such
  as `default -> panic("...")`, needs no value; the others must all convert to one type (the
  expected type, if known, else the first case's type that every other converts to, as for `?:`).
- **Every switch must be exhaustive**: its cases must match every value, or end with `default`.
  Over an enum or `bool`, listing every variant (or both values) is enough; over integers and class
  types, a `default` or a pattern that always matches is needed. A nullable value is null or a
  value of its type, and a nullable field likewise. The error names a value that no case handles,
  such as `Pair(South, _)` or `Moved(null)`.
- `default` must be the last case. **A case that can never match is an error**: one whose values the
  cases above all match, including a `default` after cases that already cover everything. A type
  test on a class counts as possibly failing, so it never makes a later case unreachable unless it
  always matches.
- `break` and `continue` inside a case apply to the enclosing loop, as in Kotlin; a switch is not a
  loop and needs no `break`.
- The value is evaluated once, before any case is tried. It must have a type of its own: `null`
  alone and a `void` call have none, and both are errors. Names a case binds are copies made before
  its guard and the case run, so a case may freely change the value it matched. If the value is a temporary, it
  lives until the switch ends (for a switch expression, until the switch's value is produced).

### Patterns

Patterns appear in `case` and after `is`. A pattern either always matches or tests the value:

| Pattern | Matches |
|---|---|
| `_` | Anything |
| `var name` | Anything, and binds it to `name` |
| `Variant`, `Enum.Variant` | That variant, whatever its fields |
| `Variant(p1, p2)` | That variant, if each field matches its pattern; every field must be listed |
| `Struct(p1, p2)` | A struct value, if each field, in declaration order, matches its pattern; every field must be listed |
| `null` | Null: a switch's nullable value (above), or a nullable field, in a `case` |
| `Type name`, `Type` | A class or interface value that is non-null and a `Type`. For a value type, only its own type, which just states it |
| `42`, `-1`, `'a'`, `true` | An integer, `char`, or `bool` equal to the literal |

- The enum qualifier in `Enum.Variant` must be the matched value's enum; type arguments may be left
  out, since the value determines them: `case Result.Ok(var v)`.
- An integer pattern is a constant of the matched value's integer type: an unsuffixed literal
  converts to it where it fits, so matching a `byte` writes `case 200`, and matching a `long` with a
  value past `INT_MAX` writes `case 2147483648L`, since a literal too large for `int` needs a suffix
  (§2.2).
- A lone name is a variant when matching an enum, and otherwise a type: `x is Dog` tests a class.
- A struct pattern deconstructs, as Java's record patterns do. A struct value is always its own
  type, so the pattern matches whenever its field patterns do; it is useful nested, as in
  `case Moved(Vec3(var x, _, _))`, or on a nullable value, where `pos is Vec3(var x, _, var z)`
  tests that a `Vec3?` isn't null and binds two fields. On a `Vec3` with field patterns that all
  always match (`_`, `var`, or such a struct pattern) it always matches, so it is an error after
  `is`, like `var x`. Every field must be visible where the pattern is written. As for an enum, the
  type arguments may be left out, since the value determines them: `case Pair(1, var s)` on a
  `Pair<int, String>`.
- Floating-point and string literals are not patterns. `null` is one only in a `case`, alone or
  inside another pattern, as in `case Moved(null)`, and on a type that can be null. A whole `is`
  pattern can't be `null`, since `== null` says that; `s is Moved(null)` is allowed.
- `x is pattern` is `true` when the pattern matches. Patterns that always match (`_`, `var x`) are
  errors after `is`. Names bound in an `if` or `while` condition follow the rules of §2.5: in scope
  for the whole statement, definitely assigned where the test succeeded.

```java
if (state is Chasing(var target) && target.isAlive()) { ... }
while (node is Some(var n)) { node = n.next; }
```

- Binding names in an `is` stores to existing locals, left to right, which releases their old
  values. Every bound value is copied out of the matched value before any of those releases, since a
  destructor they run may change it.

### String interpolation

```java
log($"hp: {player.health}, pos: {player.position}");
```

A string literal prefixed with `$` is interpolated, as in C#: each `{expression}` in it is replaced
by the expression's value, and the result is a new `String`. Only `$` strings interpolate, so braces
in ordinary strings mean nothing special.

- In a `$` string, `{{` and `}}` stand for `{` and `}`. A lone `}` is an error. Escapes (§2.2)
  work as in any string.
- The parts are evaluated left to right, each exactly once.
- A hole holds any expression on one line, including other strings, but a conditional
  `a ? b : c` must be parenthesized, since `:` starts a format specifier, as in C#.
- An interpolated value must be *printable*. Primitives and `String` are, and are written as
  `print` writes them (`3.0`, `true`, a `char` as its character), a nullable one as its value or
  `null`. A floating-point value is written as the shortest text that reads back as the same
  value, in the plain or exponential form C++'s `std::to_chars` picks, with `.0` added to a whole
  number written without an exponent; `inf`, `-inf`, and `nan` name the rest.
- Any other type is printable when it implements the standard library's `Printable` interface,
  and is written by its `toString()`. A nullable value of such a type is written as `null` when
  it is null. There is no default `toString()`: a type opts in, so printing never silently shows
  something like Java's `Player@1b6d3586`.

```java
interface Printable {
    String toString();
}

struct Vec3 implements Printable {
    pub float x;
    pub float y;
    pub float z;

    pub String toString() { return $"({x}, {y}, {z})"; }
}
```

- Primitives, `String`, and their nullables satisfy a `T extends Printable` bound without
  implementing it, so generic code can print any printable value, including an `int`, with
  `$"{value}"` or `value.toString()`. Outside generic code too, they have a `toString()` that
  writes them as interpolation does: `5.toString()` is `"5"`.

`print` writes its value and then one `\n` byte. The runtime writes standard output unchanged on
every platform (the stream is binary), so a value that ends in a newline character carries exactly
what the program made, with no translation of its own.

#### Format specifiers and alignment

```java
log($"{name,-12}|{score,6:D}|{ratio,8:F2}|{flags:X8}");
```

As in C#, a hole is `{value,alignment:format}`, and both parts may be left out.

- The **alignment** is an integer literal, optionally negative. The value's text is padded with
  spaces to at least that many characters: on the left for a positive alignment, so the text is
  right-aligned, and on the right for a negative one. Characters are counted as Unicode code
  points. Any printable value may be aligned, including `null`.
- The **format specifier** is a letter and an optional precision of up to two digits. It is
  checked against the value's type, so a specifier that doesn't apply is a compile error rather
  than being ignored at run time, as in C#. A nullable value is formatted when it isn't null.

| Specifier | Applies to | Writes | Precision | Example |
|---|---|---|---|---|
| `D`, `d` | Integers | Decimal | Minimum digits, zero-padded after the sign | `{-42:D5}` is `-00042` |
| `X`, `x` | Integers | Hexadecimal, upper- or lowercase | Minimum digits | `{255:x4}` is `00ff` |
| `B`, `b` | Integers | Binary | Minimum digits | `{5:B8}` is `00000101` |
| `F`, `f` | `float`, `double` | Fixed-point | Digits after the point; default 2 | `{3.14159:F3}` is `3.142` |
| `E`, `e` | `float`, `double` | Exponential, with the case of the `E` given | Digits after the point; default 6 | `{1500.0:E2}` is `1.50E+003` |

- `X` and `B` write a negative value's two's-complement bits at the type's width:
  `{-1:X}` is `FFFFFFFF` for an `int`.
- `F` and `E` round to the nearest digit. Infinities and NaN are written as `print` writes them
  (`inf`, `-inf`, `nan`). Every NaN is written `nan`, whatever its sign.
- The other C# specifiers (`N`, `P`, `G`, `C`, `R`) and custom formats such as `0.00` are not
  supported. `N` and `C` depend on a culture, which Wylow doesn't have. A `Printable` type has no
  specifiers of its own; it may only be aligned.

> Implemented so far: everything above. Not yet implemented:
> - `Printable` stdlib types (`List`, `Array`, `Span`, `Result`, ...). A collection should be
>   printable when its element type is, which needs conditional implementations, or a `toString()`
>   with its own `T extends Printable` bound;
> - a generated field dump for debugging, like Rust's `#[derive(Debug)]`, so small types needn't
>   write `toString()` by hand;
> - `StringBuilder` (§2.10), for strings built up piece by piece, as in a loop. Interpolation builds
>   strings with the runtime's `wyl::StringBuffer`, which a `StringBuilder` could wrap.

## 6.10 Annotations

Java syntax: `@Name` or `@Name(args)`. Currently compiler-defined only:

| Annotation | Meaning | Chapter |
|---|---|---|
| `@CallerLocation` | A panic inside the function reports where it was called from | below |
| `@AllowLargeStack` | Suppress the large-stack warning | [01-safety.md](01-safety.md) |
| `@Align(n)`, `@Packed` | Struct layout | [02-types.md](02-types.md) |
| `@Repr(T)` | An enum's integer representation | [07-interop.md](07-interop.md#75-enums-with-integer-representations) |
| `@Flags` | An enum whose values combine as bits | [07-interop.md](07-interop.md#75-enums-with-integer-representations) |
| `@Acyclic` | A class's objects never form cycles | [03-memory-model.md](03-memory-model.md) |

An argument is a type, as in `@Repr(uint)`, or a literal, as in `@Align(16)`. Settings that
change how a declaration is laid out or treated, rather than what it is, are annotations rather
than new syntax. **An annotation never changes a type**: what can be assigned, converted, or
captured. Whatever is part of a type is a keyword, such as a parameter's mode (`ref`, `in`) or
whether a function is `escaping` (§6.7).

Parentheses right after an annotation are its arguments unless they start with `fn`, which makes
them the parentheses that group the annotated declaration's function type: `@Name (fn(int) -> int)? f`.

User-defined annotations (and reflection) are *open*.

> Implemented so far: `@CallerLocation`, `@Acyclic`, and `@Repr`. Any other
> annotation is an error.

### `@CallerLocation`

Every panic inside a function marked `@CallerLocation`, including those of the checks it calls
and of other `@CallerLocation` functions it calls, reports the location of the call to it. The
standard library uses it so that a failed `list[i]` points at the user's line rather than into
`List`. A lambda written in such a function reports its own locations, since it may run after the
call has returned. It applies to functions, methods, constructors, and indexers, but
not yet to virtual, abstract, or interface methods.

## 6.11 Naming conventions

Enforced with compiler **warnings** (not errors):

| Kind | Convention | Example |
|---|---|---|
| Types (classes, structs, enums, interfaces) | `PascalCase` | `EnemySystem` |
| Enum variants | `PascalCase` | `AiState.Chasing` |
| Methods, fields, locals, parameters | `camelCase` | `applyDamage`, `isDead` |
| Constants (`const`, and `static` fields that aren't `mut`) | `UPPER_SNAKE_CASE` | `MAX_SPEED` |
| Modules, and the directories that are them | `snake_case` | `world_gen::noise` |
| Files | `snake_case`, named for what they hold | `chunk_mesher.wyl` |

> Not implemented yet: the compiler emits no warnings, so these conventions aren't checked.

## 6.12 Complete example

```java
// src/enemies/system.wyl, in module enemies
import std::List;
import math::*;

pub enum AiState {
    Idle,
    Chasing(int target),
}

pub struct Enemy {
    pub Vec3 position;
    pub Vec3 velocity;
    pub int health = 100;
    pub AiState state = AiState.Idle;

    pub bool isDead() { return health <= 0; }
}

pub class EnemySystem {
    const float CHASE_SPEED = 4.0;

    List<Enemy> enemies = new List<Enemy>();

    pub int spawn(Vec3 at) {
        enemies.add(Enemy(position: at));
        return enemies.count() - 1;
    }

    pub void tick(float dt) {
        for (ref Enemy e : enemies) {
            switch (e.state) {
                case Idle -> {}
                case Chasing(var t) when enemies.get(t) is ref Enemy target -> {
                    e.velocity = target.position.sub(e.position).normalized().scale(CHASE_SPEED);
                }
                case Chasing -> e.state = AiState.Idle;
            }
            e.position = e.position.add(e.velocity.scale(dt));
        }
        log($"{enemies.count()} enemies alive");
    }
}
```
