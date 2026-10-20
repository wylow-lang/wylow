# 2. Types

## 2.1 Primitives

All sizes are fixed on every platform.

| Type | Size | Notes |
|---|---|---|
| `bool` | 1 byte | No implicit conversion to or from integers |
| `sbyte` / `byte` | 8 bits | **`byte` is unsigned** (like C#) |
| `short` / `ushort` | 16 bits | |
| `int` / `uint` | 32 bits | |
| `long` / `ulong` | 64 bits | |
| `float` / `double` | 32 / 64 bits | IEEE 754 |
| `char` | 32 bits | A Unicode code point |
| `nint` / `nuint` | pointer size | Intended for `unsafe` code and interop |

**Lengths and indices are `int`.** Signed lengths avoid the classic unsigned-underflow bug
(`i < length - 1` when `length == 0`).

## 2.2 Literals

| Literal | Type |
|---|---|
| `42` | `int` |
| `42u`, `42L`, `42uL` | `uint`, `long`, `ulong` |
| `1.5` | **`float`** |
| `1.5d` | `double` |
| `0xFF`, `0b1010`, `1_000_000` | hex, binary, digit separators |
| `'a'` | `char` |
| `"text"` | `String` |

Unlike Java, an unsuffixed floating-point literal is a `float`, since almost all game math is
single precision.

## 2.3 Conversions

**Implicit conversions are allowed only when lossless:**
`byte → int`, `int → long`, `int → double`, `float → double`, and similar widenings.
`int → float` is *not* implicit (it can lose precision).

**Integer constants** convert implicitly to `float` or `double` when the value is exactly
representable, so `float x = 0;` and `velocity * 2` work. `float f = 16777217;` is an error
(2^24 + 1 has no exact `float`).

**All other conversions require a cast** `(T)x`, and every cast has defined behavior:

| Conversion | Behavior |
|---|---|
| Integer narrowing | Wraps (`(byte)300 == 44`) |
| Signed ↔ unsigned (same size) | Reinterprets bits |
| Float → integer | Saturates to the target range; NaN → 0 |
| Double → float | Rounds to nearest |
| Enum without data → integer | The variant's value, then as integer narrowing |
| Integer → enum without data | Panics if no variant has the value |

A cast to a named type, such as `(Format)i`, is recognized as in Java: `(Name)` followed by something
that can only start an operand. `(Format)-1` is therefore a subtraction; write `(Format)(-1)`.

**Checked conversions** return a nullable: `int.tryFrom(someLong)` → `int?`.

## 2.4 Integer overflow

- Arithmetic operators panic on overflow in **debug** builds and wrap in **release** builds.
- Explicit operations are available when the behavior matters:
  - `a.wrappingAdd(b)`, `wrappingSub`, `wrappingMul`: always wrap (hashing, RNG).
  - `a.checkedAdd(b)` etc.: return `int?`, null on overflow.

## 2.5 Nullability

No type is nullable unless written with `?`. This applies to every type, including value types.

```java
Texture tex = loadDefault();     // never null
Texture? override = null;        // may be null
int? cachedIndex = null;         // value + presence flag
```

| Syntax | Meaning |
|---|---|
| `if (x != null) { ... }` | Flow typing: `x` is non-null inside the block (locals only) |
| `if (x is T name) { ... }` | Pattern test that binds a new name |
| `a ?? b` | `a` if non-null, else `b` |
| `a?.b` | `null` if `a` is null, else `a.b` (result is nullable) |
| `a!` | `a`, or panic if null |

> Implemented so far: nullable **class and interface** types only. Nullable value types (`int?`,
> `Vec3?`) and type parameters (`T?`, since `T` may be a struct) are errors for now.

### Rules

- **Members of a nullable value** can only be reached through `?.` or `!`, or through a narrowed
  local. `a.b` on any other nullable expression is an error.
- **Narrowing** (flow typing) applies to locals and parameters only, since nothing else can change
  them behind the code's back. A local is non-null where flow analysis proves it:
  - in the branch where `x != null` or `x == null` is known, including after `&&`, `||`, `!`, and
    `?:`, and after an `if` whose other branch cannot complete (`if (x == null) { return; }`);
  - after assigning it a non-null value, until it is assigned something nullable;
  - in a loop, only if the loop does not assign it (each iteration starts without narrowing the loop
    could undo), or after a condition in the loop narrows it again: `while (x != null) { x = x.next; }`.
- `x is T name` binds `name` (non-null, type `T`) where the test succeeded. It also narrows `x`.
  A name bound in an `if` or `while` condition is in scope for the whole statement, but definitely
  assigned only where the test is known to have succeeded.
- `a?.b` chains like Kotlin: `a?.b.c` is an error when `a?.b` is nullable; write `a?.b?.c`. The
  result must be a reference type (or `void` for a call), since `int?` does not exist yet. When `a`
  is null, a call's arguments are not evaluated.
- `a ?? b` requires a nullable `a`. Its type is `a`'s non-null type (or a supertype both convert to)
  when `b` is non-null, otherwise nullable. `b` is evaluated only when `a` is null.
- `!` and `?.` on a value that is not nullable are errors, so that redundant checks don't accumulate.
- `null` converts to any nullable type; `var x = null;` is an error. Any reference can be compared
  with `null`.
- Overloads cannot differ only in nullability (`f(Player)` and `f(Player?)`), since both are the
  same type at runtime.

## 2.6 Type inference

`var` declares a local with an inferred type: `var enemies = new Pool<Enemy>();`

## 2.7 Constants

- `const` — compile-time constant: `const float PI = 3.14159;`
- `final` — assigned exactly once, at runtime.

## 2.8 Enums

Enums can be plain (like Java) or carry data per variant (tagged unions).

```java
enum Direction { North, East, South, West }

enum AiState {
    Idle,
    Chasing(Handle<Enemy> target),
    Fleeing(Vec3 from, float timeLeft),
}
```

- Variants are separated by commas, with an optional trailing comma. An enum may have methods
  after its variants, separated from them by `;` as in Java. It has no fields, constructors, or
  destructor: data belongs to variants.
- An enum is a **value type**, like a struct: stored inline and copied on assignment. Copies are
  shallow, as for structs. It cannot contain itself by value, directly or through other value types.
- An enum value is **immutable**: there is no way to change the fields of a variant in place, only
  to assign a whole new value. Its methods are therefore always `readonly`, and can be called
  on `in` parameters.
- Enums may be generic (`enum Option<T> { None, Some(T value) }`), and may implement interfaces for
  use as generic bounds, as structs can (§2.12).
- A variant's fields can only be read with a pattern ([06-syntax.md](06-syntax.md#patterns)).
  `state.target` is an error.

### Creating values

```java
var d = Direction.North;
var s = AiState.Fleeing(pos, 2.5);
Direction next = East;                 // the enum may be left out where its type is known
var o = Option.Some(5);                // type arguments are inferred: Option<int>
Option<Item> none = None;              // ...or taken from the expected type
var n = Option<int>.None;              // ...or written
```

- `Enum.Variant` for a variant without data, `Enum.Variant(fields...)` for one with data; the
  parentheses are required exactly when the variant carries data.
- **Where a value's type is known**, the enum (and its type arguments) may be left out: in the
  initializer of a declaration with a written type, the value of an assignment, a `return` value, a
  field initializer, and the right side of `==`/`!=` with an enum on the left. The expected type
  also reaches into `?:` branches and switch expression cases. Elsewhere, including call
  arguments, write the enum: `f(Direction.East)`. A local, field, function, or type of the same
  name takes precedence over the variant.
- A generic enum written without type arguments takes them from the expected type if that is the
  same enum, and otherwise infers them from the fields' values, as for generic functions (§2.13).
  `Option.None` alone is an error, since nothing determines `T`.

### Equality, defaults, conversions

- `==` and `!=` compare the variant and then its fields, and are available when every field of
  every variant supports `==` (so never when a field's type is a type parameter or `String`).
  Enums are not ordered.
- **An enum's default value is its first variant, if that variant carries no data.** Otherwise the
  enum has no default value: a field of that type needs an initializer or, in a class, an assignment
  in every constructor (§2.10, "Default values" in §2.13).
- Enums without data convert to and from integers with a cast: `(int)Direction.East` is `1`, and
  `(Direction)i` panics if `i` is not the value of a variant. A variant's value is its position, or
  as in C, the value written for it or one more than the previous variant's
  ([07-interop.md](07-interop.md#75-enums-with-underlying-types)).

### `switch`

`switch` over an enum must be **exhaustive**; a missing case is a compile error.

```java
switch (ai.state) {
    case Idle                       -> wander();
    case Chasing(var t)             -> moveToward(t);
    case Fleeing(var from, var tl)  -> runFrom(from);
}
```

See [06-syntax.md](06-syntax.md#switch) for `switch` and patterns.

### Representation

Enums compile to a C++ struct containing a tag and a union (not `std::variant`). The tag is the
smallest unsigned integer that numbers the variants, or the underlying type when one is given. When
no variant can hold an object reference, the enum is trivially copyable; otherwise copying and
destroying it copy and destroy only the active variant's fields, and assigning it stores the new
value before releasing the old one, as for structs (§3.2).

Enums without data may declare an explicit integer type and values (`enum Format : uint { ... }`)
and may be marked `@Flags`; see [07-interop.md](07-interop.md#75-enums-with-underlying-types).

## 2.9 Structs

A `struct` is a **value type**.

```java
struct Transform {
    Vec3 position;
    Quat rotation;
    Vec3 scale = Vec3.ONE;
}
```

- Stored inline: on the stack, inside other structs, inside arrays, inside classes.
- **Always copied** on assignment and when passed by value. A copy is a field-by-field copy.
- **No destructors, no copy constructors, no move semantics.** Resource ownership belongs to classes.
- No inheritance. Structs may implement interfaces, but only for use as generic bounds (§2.12).

> ⚠️ **Struct copies are shallow.** If a struct contains a class reference (a `List`, a `String`,
> any class), the copy refers to the *same* object. Mutating the list through one copy is visible
> through the other. Use `.clone()` when a deep copy is wanted.

### Layout

Struct fields are laid out in declaration order with C-compatible alignment, so structs can be
passed directly to graphics APIs.

| Annotation | Effect |
|---|---|
| `@Align(n)` | Align the struct (or field) to `n` bytes |
| `@Pad(n)` | Insert `n` bytes of padding |
| `@Packed` | Remove all padding |

```java
@Align(16)
struct CameraUniforms {
    Mat4 view;
    Mat4 proj;
    Vec3 eye;
    @Pad(4)
}
```

## 2.10 Classes

A `class` is a **heap-allocated, reference-counted object** (see
[03-memory-model.md](03-memory-model.md#32-objects-classes)).

- Single inheritance with `extends`; any number of interfaces with `implements`.
- **C#-style virtual dispatch:** methods are non-virtual unless marked.

| Keyword | Meaning |
|---|---|
| `virtual` | Method may be overridden |
| `override` | Overrides a `virtual` or `abstract` method (required) |
| `abstract` | No body; subclass must override. Class cannot be instantiated |
| `final` (class) | Cannot be subclassed |
| `final` (method) | `override` that cannot be overridden further |

```java
abstract class Weapon {
    abstract void fire(World w);
    virtual float cooldown() { return 0.5; }
}

final class Shotgun extends Weapon {
    override void fire(World w) { ... }
    override float cooldown() { return 1.0; }
}
```

- Class references are non-null unless typed `T?`.
- Classes may have a destructor, declared `~ClassName() { ... }`, which runs deterministically when
  the last reference is released.
- Standard library collections and strings are classes: `List<T>`, `Map<K, V>`, `T[]`, `String`.

### Inheritance rules

- **Upcasts are implicit**: a class converts to its base classes and to the interfaces it
  implements. **Downcasts only go through `is`**: `if (weapon is Shotgun s) { ... }`. There is no
  unchecked cast.
- `override` must match the overridden method's parameter types (including nullability) and return
  type exactly. Return types are not covariant.
- **No hiding.** A method with the signature of an inherited non-private method must be an
  `override`; if the inherited method isn't `virtual`, that is an error. A field may not have the
  name of an inherited field.
- `super.method(...)` calls the base class's implementation without dispatch; it cannot call an
  abstract method.
- A non-abstract class must implement every inherited abstract method and every method of every
  interface it implements, directly or through a base class. An `abstract` class may leave them to
  its subclasses.
- Generic methods cannot be `virtual`, `override`, or `abstract` (they compile to C++ templates).
- A class that declares no constructor gets a public parameterless one. That is an error if a field
  needs a value (see construction below) or the base class has no parameterless constructor.
- Access: `private` members are visible only inside their class, and are not inherited for
  overriding. `protected` and package access are not restricted yet: every program is a single
  file, which is a single package.

### Construction

Construction follows Java's order, and C++'s rule for what the object is while it is built:

1. The base class constructor runs first. `super(...)` must be the first statement of the
   constructor; without it, the base's parameterless constructor is called. Its arguments cannot use
   `this`.
2. Field initializers run in declaration order. They cannot use `this`.
3. The constructor body runs.

A field *needs a value* if it has no initializer and its type has no default (a non-null reference,
a type parameter, or a struct containing one). In the body, **until every such field of the class
is assigned, `this` can only be used to assign fields and to read fields already assigned**: no
method calls on it, no passing it anywhere. Every such field must be assigned before the
constructor returns, on every path. Flow analysis checks both. The same rule applies to struct
constructors.

**While a base class's constructor or destructor runs, the object is an instance of that base
class**, as in C++: virtual calls dispatch to the base class's implementations, so they can never
see the derived class's fields before they are assigned or after they are destroyed. Calling an
abstract method at that point panics.

This is the simplest sound rule: each class can only observe its own fields (fully assigned before
`this` escapes) and its base's (completely constructed). Java and Kotlin dispatch to the derived
class during construction and can observe unassigned fields; Swift avoids that with a two-phase rule
that requires a derived class to assign its fields *before* calling `super`.

### Strings

`String` is an immutable, UTF-8, reference-counted `shared class` (it may cross threads; see
[05-concurrency.md](05-concurrency.md)). Use `StringBuilder` to build strings.

## 2.11 Arrays and spans

| Type | Lives | Size | Kind |
|---|---|---|---|
| `T[N]` | Inline (stack, struct, class) | Compile-time, fixed | Value |
| `T[]` | Heap | Set at creation, never changes | Class (refcounted) |
| `List<T>` | Heap | Growable | Class (refcounted) |
| `Span<T>` / `ReadOnlySpan<T>` | — (a view) | Whatever it views | Second-class reference |

```java
float[16] matrix;                              // inline, compiles to std::array
float[] heights = new float[width * height];   // heap, length fixed, default values
List<Enemy> spawned = new List<Enemy>();       // heap, growable
```

- All indexing is bounds-checked (unless `@Unchecked` / `unsafe`); a failed check panics with the
  index and the length.
- `T[]` stores its length and elements in a single allocation and **never reallocates**, so
  references into it never need growth locking.
- `T[N]`, `T[]`, and `List<T>` convert **implicitly** to `Span<T>` and `ReadOnlySpan<T>`.
- `Span<T>` follows the same rules as `ref` (cannot be a field, cannot be returned, cannot be
  captured by an escaping closure). Taking a span of a `List` borrows the list
  ([03-memory-model.md](03-memory-model.md#34-references)).

```java
float sum(ReadOnlySpan<float> values) { ... }

sum(matrix);    // T[N]
sum(heights);   // T[]
sum(spawned);   // List (borrowed for the duration of the call)
```

### Fixed arrays `T[N]`

- `N` is an integer literal. Like C, the first length is the outer one: `int[4][2]` is four
  `int[2]`s, indexed `a[i][j]` with `i < 4`.
- A fixed array is a value: assignment and passing by value copy every element. `==` compares
  the elements when their type supports `==`.
- `a.length` is `N`. An index that is a constant outside `0..N` is a compile-time error.
- Array literals create fixed arrays: `int[3] a = [1, 2, 3];`. The element type is the expected
  one, or else the first element type that every element converts to. An empty literal `[]`
  needs an expected type.
- A fixed array's default value has each element's default. Unlike other locals, a fixed-array
  local without an initializer holds that default rather than being unassigned, since writing out
  a literal of every element is impractical; this requires the element type to have a default.

### Heap arrays `T[]`

- `T[]` is the standard library class `Array<T>`, and `new T[n]` calls its constructor
  `Array(int length)`, which fills the array with default values. Only the compiler can check
  that `T` has one, so `new T[n]`, and `new Array<T>(n)` written out, are errors for a `T`
  without a default, such as a non-null class type. `new Array<T>(n, value)` fills it with copies
  of `value` instead.
- `T?[]` is an array of nullable elements; `T[]?` is a nullable reference to an array.
- `a.length` is a property. `new T[n]` panics if `n` is negative.
- Like any object, copying a `T[]` shares it.

> Implemented so far: one dimension per `new`; `new T[2][3]` is not supported yet.

### Spans

`Span<T>` and `ReadOnlySpan<T>` are standard library `ref struct`s
([03-memory-model.md](03-memory-model.md#spans)): a pointer and a length, with a bounds-checked
indexer, `length`, and `slice(start, length)`. A `ReadOnlySpan`'s indexer returns `in T`, so its
elements can be read but not changed.

Conversions to spans are implicit, from:

- a fixed array stored in a variable, field, or element. A temporary fixed array, such as one a
  function returned, has no storage to view. A read-only fixed array (an `in` parameter, or a
  field in a `readonly` method) converts only to `ReadOnlySpan`.
- a value of a type implementing the standard library's `Spannable<T>`, by calling its `asSpan()`,
  or `ReadOnlySpannable<T>`, by calling `asReadOnlySpan()`. `Array`, `List`, and `Span` implement
  them, and so can user types.

A generic function taking a span infers its element type from any of these:
`T first<T>(ReadOnlySpan<T> values)` accepts an `int[4]` or a `List<int>`.

### `Box<T>`

`Box<T>` is a standard library class holding a single value of type `T` on the heap. Use it to
heap-allocate a large value that isn't already inside a class:

```java
var grid = new Box<Tile[65536]>();
```

## 2.12 Interfaces

- **On classes:** as in Java. An interface-typed variable holds any implementing class and calls
  through a vtable.
- **On structs:** usable **only as generic bounds** (static dispatch, zero cost). A struct cannot
  be stored in an interface-typed variable, since that would require hidden boxing. Wrap it in a
  class if that is needed.

```java
interface Damageable extends Named {
    void damage(int amount);
}

class Crate implements Damageable { ... }
struct Counter implements Named { ... }
```

- Interfaces contain only method signatures: no fields, constructors, bodies, static methods, or
  type parameters on methods. An interface may `extend` other interfaces.
- A class or struct implements an interface method with a non-private method of the same name and
  exactly the same parameter and return types, declared in it or inherited from a base class. No
  `override` is needed (as in C#). Implementing an interface method does not make the method
  overridable: mark it `virtual` for that.
- An interface may not be listed twice, and a method may not be redeclared by an extending interface.

## 2.13 Generics

Java syntax, C++ template semantics: generics are **monomorphized** (one copy per instantiation,
zero overhead).

```java
T max<T extends Comparable<T>>(T a, T b) {
    return a.compareTo(b) > 0 ? a : b;
}
```

Unlike C++ templates, a generic body is **type-checked against its bounds** when it is declared,
not when it is instantiated. Calling a method not provided by a bound is a compile error in the
generic itself.

### Declaring and using generics

```java
struct Pair<A, B> {
    A first;
    B second;
}

T identity<T>(T value) { return value; }

var p = Pair<int, float>(1, 2.5);         // struct type arguments are always written
var s = Pair<String, bool> { first = "a", second = true };
var x = identity(5);                      // function type arguments are inferred...
var y = identity<long>(5);                // ...or written explicitly
```

- Type parameters go after the name, for structs, functions, and methods. A method's type
  parameters may not reuse its struct's type parameter names.
- **Function type arguments are inferred** from the arguments. When arguments disagree, the type the
  others implicitly convert to is chosen: `max(1, 2L)` infers `long`. Inside type arguments there are
  no conversions, so `Pair<int, int>` and `Pair<long, long>` never unify.
- **Struct type arguments are always written.** There is no inference for construction.
- When a generic and a non-generic overload are equally good, the non-generic one is chosen.
- `Name<...>` in an expression is read as type arguments when followed by `(`, `.` or `{`. As in C#,
  `f(a < b, c > (d))` is therefore a call to the generic `a<b, c>`.

### Bounds

`T extends A & B` requires every type argument for `T` to implement the interfaces `A` and `B`
(bounds must be interfaces). Inside the generic, `T` has the bounds' methods. A bound may mention the
type parameters, as in `T extends Comparable<T>`.

- Both structs and classes satisfy a bound by implementing the interface. Calls through a bound use
  static dispatch for structs. For classes they go through the interface.
- A nullable type such as `Player?` does not satisfy a bound, since its methods could not be called
  on null.
- A bound does not make `T` convert to the interface (`T` might be a struct).

### Unbounded type parameters

Without bounds, a type parameter `T` supports only what every type supports: declaring, copying,
passing (including by `ref`/`in`), and returning values. In particular:

- `==` is not available on `T`, and no function overload accepts a `T` unless it is generic itself.
- `T()` is an error: `T` has no default value (it could be a non-null class reference).
- A struct with a field of type `T` has no default value inside generic code: `Pair<T, int>()` is an
  error, while `Pair<T, int> { first = x }` is fine, since it sets every field that lacks a default.
  From non-generic code, `Pair<int, int>()` works because `int` has a default.

### Default values

A type is *defaultable* if it has a default value: primitives, `String`, nullable references
(`null`), structs whose fields each either have an initializer or a defaultable type, and enums
whose first variant carries no data (that variant is the default). Type
parameters and non-null class and interface references are not defaultable, so a struct with a
`Player` field and no initializer for it has no default value. A
struct's default constructor and `{ ... }` initializers require any field that is not set
explicitly to be defaultable.

### Recursive structs

A struct cannot contain itself by value, including through type arguments
(`struct Rec { Wrap<Rec> w; }`), and its field types cannot grow without end
(`struct Grow<T> { Grow<Pair<T, T>> next; }`).
