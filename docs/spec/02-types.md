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

`char` is not a number: it has no arithmetic of its own, and in arithmetic it is its code point,
an `int` (§2.4).

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

An integer literal too large for its type is an error, also in hex: `0xFFFFFFFF` needs a `u` suffix.
A literal still converts to narrower types where it fits (§2.3), so there is no suffix for `byte` or
`short`. A character literal holds exactly one code point.

String and character literals accept these escapes:

| Escape | Character |
|---|---|
| `\n`, `\t`, `\r` | Newline, tab, carriage return |
| `\0` | The code point 0 |
| `\\`, `\"`, `\'` | Backslash, double quote, single quote |
| `\u{1F600}` | The code point with that hex value, written with 1 to 6 digits |

A string prefixed with `$` interpolates values: `$"hp: {health}"` ([string interpolation](06-syntax.md#string-interpolation)).

Any other escape is an error, as is a `\u{...}` that is not a Unicode scalar value (above
`0x10FFFF`, or a surrogate `0xD800`–`0xDFFF`), the same values a cast to `char` rejects. Unlike
Java and C#'s `é`, the braces reach every code point directly, so no character needs a
surrogate pair. There is no `\x` escape for raw bytes, since a `String` is always valid UTF-8.

## 2.3 Conversions

**Implicit conversions are allowed only when lossless**, which means every value of the source type
is exactly a value of the target type:

| From | Implicitly to |
|---|---|
| `sbyte` | `short`, `int`, `long`, `nint` |
| `byte` | `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `nint`, `nuint` |
| `short` | `int`, `long`, `nint` |
| `ushort` | `int`, `uint`, `long`, `ulong`, `nint`, `nuint` |
| `int` | `long`, `nint` |
| `uint` | `long`, `ulong`, `nuint` |
| `nint` | `long` |
| `nuint` | `ulong` |
| `char` | `int`, `uint`, `long`, `ulong`, `nint`, `nuint` |
| 8- and 16-bit integers | `float`, `double` |
| `int`, `uint` | `double` |
| `float` | `double` |

- `int → float` is *not* implicit: a `float` holds integers exactly only up to 2^24. Neither are
  `long` and `ulong` to `double`.
- `nint` and `nuint` convert only where the conversion is lossless on both 32- and 64-bit targets,
  so code that compiles on one compiles on the other.
- `char` converts to its code point, which fits in every integer type of 32 bits or more. Nothing
  converts implicitly to `char`. Since a `char` is not a number, it doesn't convert to `double`,
  although `int` does: this is the one place where two implicit conversions in a row reach a type
  that one can't.

**Integer constants** (an integer literal, optionally negated) without a suffix convert implicitly to
any integer type that holds their value, so `byte b = 200;` works and `byte b = 300;` is an error. A
suffix chooses the type, so `int i = 1L;` is an error. For `nint` and `nuint`, the value must fit in
`int` or `uint`, so the conversion holds on every target. Any integer constant also converts to
`float` or `double` when the value is exactly representable, so `float x = 0;` and `velocity * 2`
work. `float f = 16777217;` is an error (2^24 + 1 has no exact `float`).

**All other conversions require a cast** `(T)x`, and every cast has defined behavior:

| Conversion | Behavior |
|---|---|
| Integer narrowing | Wraps (`(byte)300 == 44`) |
| Signed ↔ unsigned (same size) | Reinterprets bits |
| Float → integer | Saturates to the target range; NaN → 0 |
| Double → float | Rounds to nearest |
| `char` → integer below 32 bits | The code point, then as integer narrowing |
| Integer → `char` | Panics if the value is not a Unicode scalar value (above `0x10FFFF`, or a surrogate `0xD800`–`0xDFFF`) |
| Enum without data → integer | The variant's value, then as integer narrowing |
| Integer → enum without data | Panics if no variant has the value |

Only integers cast to `char`; there is no cast between `char` and `bool` or floating-point types.

A cast to a named type, such as `(Format)i`, is recognized as in Java: `(Name)` followed by something
that can only start an operand. `(Format)-1` is therefore a subtraction; write `(Format)(-1)`.

**Checked conversions** return a nullable: `int.tryFrom(someLong)` → `int?`.

> Not implemented yet: `tryFrom`, and the `wrapping` and `checked` operations below, since
> primitive types have no methods yet.

## 2.4 Arithmetic

### Operand and result types

**Arithmetic keeps its operands' type**: `byte + byte` is a `byte`, as in Rust. Small types are not
promoted to `int` as in C, Java, and C#.

- A binary operator on two numbers first converts both operands to one type:
  - the operands' type, if they have the same type;
  - otherwise, if one operand is a constant that converts to the other operand's type (§2.3), the
    other operand's type: `light - 1` with a `byte light` is a `byte`, and `1 + 2u` is a `uint`;
  - otherwise, the type one operand converts to implicitly. Between two integer types that don't
    convert to each other, it is the smallest integer type both convert to: `int + uint` is a `long`.
  - If there is none, the operation is an error, and one side needs a cast: `long + ulong`,
    `int + float` (an `int` converts to `double`, but mixed math should not silently become `double`).
- Arithmetic (`+ - * / %`) and bitwise (`& | ^`) operators produce that type, and comparisons
  compare in it.
- `++`, `--`, and compound assignment work in the variable's type. `b += 1` is fine for a `byte b`;
  `b += i` with an `int i` is an error, since `b + i` is an `int`.
- Unary `-` is an error on unsigned types. `~` works on every integer type.

Promotion would make `byte c = a + b;` an error, and the cast everyone would write to fix it,
`(byte)(a + b)`, wraps silently, which would hide the overflow that debug builds otherwise catch.
Keeping the type keeps every integer operation checked, and generic code, compound assignment, and
`++` need no special cases.

The cost is that an intermediate result can overflow even where the final one would fit:
`(a + b) / 2` with two `byte`s overflows when `a + b` exceeds 255, also when the result is stored in
an `int`. Widen one operand first: `((int)a + b) / 2`. The type never depends on where the result is
stored. Debug builds panic in this case rather than producing a wrong value.

### Shifts

- `a << n` and `a >> n` have `a`'s type; `n` may be any integer type. `>>` is arithmetic for signed
  types and logical for unsigned ones.
- The count is masked to the width of `a`'s type, as in Java and C#: an `int` shifts by `n & 31`.
  Shifts never panic.
- A constant count outside `0` to width − 1 is an error. This catches the classic packing mistake
  `hi << 8 | lo` with a `byte hi`, which would shift every bit out (and, masked, not shift at all):
  write `(int)hi << 8 | lo`.

### `char`

`char` supports `==`, `!=`, and the ordering comparisons (by code point) with another `char`, and
character literals in `switch` and patterns. It has no arithmetic of its own: an arithmetic,
bitwise, or shift operator converts a `char` operand to its code point as an `int`, so `c - '0'` is
an `int`, and `(char)(c + 1)` is the next character, checked by the cast. Unary `-` and `~`, `++`,
and `--` are errors, and `c == 65` is an error (compare with `'A'`).

### Overflow

- Arithmetic operators panic on overflow in **debug** builds and wrap in **release** builds. For
  unsigned types, a result below zero is an overflow.
- Division by zero panics in every build, as does `MIN / -1` for signed types.
- Explicit operations are available when the behavior matters:
  - `a.wrappingAdd(b)`, `wrappingSub`, `wrappingMul`: always wrap (hashing, RNG).
  - `a.checkedAdd(b)` etc.: return a nullable of `a`'s type (`int?` for `int`), null on overflow.

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

### Rules

- **Members of a nullable value** can only be reached through `?.` or `!`, or through a narrowed
  local. `a.b` on any other nullable expression is an error.
- **Narrowing** (flow typing) applies to locals and by-value parameters only, since nothing else can
  change them behind the code's back. A `ref` or `in` parameter or variable is not narrowed: another
  reference to the same variable could set it to null after the check. A local is non-null where
  flow analysis proves it:
  - in the branch where `x != null` or `x == null` is known, including after `&&`, `||`, `!`, and
    `?:`, and after an `if` whose other branch cannot complete (`if (x == null) { return; }`);
  - after assigning it a non-null value, until it is assigned something nullable;
  - in a loop, only if the loop does not assign it (each iteration starts without narrowing the loop
    could undo), or after a condition in the loop narrows it again: `while (x != null) { x = x.next; }`.
  - passing it by `ref` ends its narrowing, since the callee may assign it null.
- `x is T name` binds `name` (non-null, type `T`) where the test succeeded. It also narrows `x`.
  A name bound in an `if` or `while` condition is in scope for the whole statement, but definitely
  assigned only where the test is known to have succeeded.
- `a?.b` chains like Kotlin: `a?.b.c` is an error when `a?.b` is nullable; write `a?.b?.c`. The
  result is `b`'s type made nullable (`void` for a call returning nothing), so `node?.count` is an
  `int?`. When `a` is null, a call's arguments are not evaluated.
- `a ?? b` requires a nullable `a`. Its type is `a`'s non-null type (or a supertype both convert to)
  when `b` is non-null, otherwise nullable. `b` is evaluated only when `a` is null.
- `!` and `?.` on a value that is not nullable are errors, so that redundant checks don't accumulate.
- `null` converts to any nullable type; `var x = null;` is an error. Any reference and any nullable
  value can be compared with `null`.
- Overloads cannot differ only in nullability (`f(Player)` and `f(Player?)`): for references both
  are the same type at runtime, and in generic code `f(T)` and `f(T?)` could be too.
- A `ref` to a nullable variable is `ref T?`; a reference that may refer to nothing is `ref? T`
  ([03-memory-model.md](03-memory-model.md#nullable-references-ref-t)).

### Nullable value types

`int?`, `Vec3?`, `Direction?`, and `int[4]?` are values like the type they make nullable, stored
inline with a presence flag (a C++ `vpl::Optional<T>`; `int?` costs an `int` and a `bool`). Any type
can be made nullable except a pointer, which can already be null, and a `ref struct`, which can't be
stored. Nullable values follow the rules above, and also:

- **Conversions**: a value converts implicitly to a nullable of any type it converts to (`int` to
  `long?`, an integer constant to `float?`), and a nullable value to a nullable of a wider type
  (`int?` to `long?`), which stays null if it was null.
- **Default**: a nullable type's default value is `null`, so it is always defaultable (§2.13).
- **Operators**: `==` and `!=` compare a nullable value with `null`, with another nullable value,
  or with a value, when the value type supports `==`: both sides become nullables of the type the
  values compare as, and `null` equals only `null`. No other operator accepts a nullable value (no
  lifted `+` or `<` as in C#): narrow it first, or use `!` or `??`. Where an operator, a condition,
  a `switch`, or `++` needs a value, a nullable local is narrowed as for member access, and flow
  analysis checks that it was checked.
- `x is int n` tests that `x` is not null and binds its value. The type must be exactly the value
  type: an `int?` can never be a `long`.
- `print` accepts the nullable primitive types and prints `null` for null.
- **Changing it in place**: a narrowed `mut` local's value is a variable, so `if (v != null) {
  v.x = 1; v.move(); }` changes `v`. Nothing may keep referring into that value, since assigning `v` null
  destroys it: a `ref` or `in` variable, a span, or a `for` loop over its elements is an error
  (copy the value to a variable first), as is passing `v` by `ref` in the same call or assignment
  that uses its value by reference (`v.moveWith(ref v)`), or passing it a lambda that assigns `v`
  (`apply(v.x, () -> { v = null; })`, with an `in` parameter). A lambda made earlier ends the
  narrowing where it is made, and one made later can't run before then. A `ref` variable can't refer to a nullable
  local itself either, since the local could then change after a null check. A narrowed local that
  isn't `mut` is never assigned again, so `in` variables, spans, and loops can refer into its value.
- `v!` and `v?.m()` work on a copy: `v!.x = 1` is an error, as for any temporary, and `v?.m()` is an
  error when `m` is a `mut` struct method, since its changes would be lost.
- A struct cannot contain itself through a nullable (`struct Link { public { Link? next; } }`), since the
  value would be stored inline; use a class.

### Nullable type parameters

`T?` is allowed for any type parameter. It is a nullable value or a nullable reference, whichever
`T` stands for, so `T? first<T>(List<T> items)` returns an `int?` from a `List<int>` and a `Node?`
from a `List<Node>`. When `T` is itself nullable, `T?` is the same type as `T` (as in Kotlin):
`first` on a `List<int?>` returns an `int?`, which is null both for an empty list and for a null
first element. A call infers `T` from a `T?` parameter through the value type of its argument, so
`orElse(x, 0)` with an `int? x` infers `int`.

## 2.6 Type inference

`var` declares a local with an inferred type: `var enemies = new Pool<Enemy>();`

## 2.7 Mutability and constants

Variables, parameters, class fields, and struct methods are **immutable unless declared `mut`**,
as in Rust. Most never change, so the marker goes on the exception.

```java
int lives = 3;                         // can't be assigned again
mut int score = 0;
score += 10;
mut var target = enemies.first();
```

- **A variable that isn't `mut` always holds the same value, but an object it refers to can still
  change.** `List<int> list = new List<int>(); list.add(1);` is fine; `list = other;` is not.
  Whether an object's own fields can change depends on their declarations (below).
- **A value follows its variable.** A struct, enum, or fixed array is stored in its variable, so when
  the variable isn't `mut`, nothing in it can change. Its fields and elements can't be assigned, it
  can't be passed by `ref` or bound to a `ref` variable, its `mut` methods can't be called,
  and a fixed array converts only to `ReadOnlySpan`. These are the rules of an `in` parameter
  ([06-syntax.md](06-syntax.md#parameter-modes)). For the same reason, struct fields take no `mut`.
- **Class fields** are immutable unless declared `mut`. The class's own constructors may assign
  them through `this` any number of times; nothing else can: not a method, not a subclass's
  constructor, and not a lambda created in a constructor. A struct stored in a field follows the
  field, so `body.position.x = 1` needs `position` to be `mut`.
- **A variable declared without a value is assigned once, later.** Assigning it where it might
  already have been assigned, including in a loop or a lambda, is an error unless it is `mut`:

  ```java
  String label;
  if (hp > 0) {
      label = "alive";
  } else {
      label = "dead";
  }
  ```
- **`mut` goes first**, after any annotations and modifiers: `mut int count;`, `static mut int frames;`,
  `void aim(mut Vec3 dir)`, `for (mut int i = 0; i < n; i++)`, `for (mut Vec3 p : points)`,
  `(mut x) -> ...`. It is not allowed on:
  - `ref` and `in` parameters and variables, which always refer to the same place. Through a `ref`,
    the place can be changed, since it had to be mutable to be referred to;
  - non-escaping function parameters and variables, which can't be reassigned (§6.7);
  - struct fields and variant fields, which follow their value;
  - pattern bindings, which are copies; copy one to a `mut` variable to change it.

  A `ref struct` variable may be `mut`, which allows calling its `mut` methods, but it
  still can't be reassigned ([03-memory-model.md](03-memory-model.md#spans)).
- **A struct method can't change `this` unless it is `mut`**: `mut void normalize()`. Only `mut`
  methods need a mutable place to be called on ([06-syntax.md](06-syntax.md#mut-struct-methods)).
- `mut` is not part of a function's signature: an override may declare its parameters `mut` or not
  regardless of the method it overrides.
- `const` declares a compile-time constant: `const float PI = 3.14159;`.

> Not implemented yet: a warning for a `mut` variable that is never changed.

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
  to assign a whole new value. Its methods therefore can't be `mut`, and can be called
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
  field initializer, the right side of `==`/`!=` with an enum on the left, and a call argument
  (below). The expected type also reaches into `?:` branches and switch expression cases.
  Elsewhere, write the enum: `Direction.East`. A local, field, function, or type of the same name
  takes precedence over the variant.
- **A call argument's expected type is its parameter's type** when every overload taking that many
  arguments has the same type there, after substituting the receiver's type arguments. This covers
  functions, methods, constructors, `super(...)`, and a variant's fields, so `f(East)`,
  `list.add(Ok(3))`, and `Ok(Ok(x))` returned as a `Result<Result<int, E>, E>` need no type
  arguments. Where the overloads differ, or the parameter's type involves the called function's own
  type parameters (which are inferred from the arguments), there is no expected type, so
  `unwrap<T>(Result<T, E> r)` is called as `unwrap(Result<int, String>.Ok(1))`.
- A generic enum written without type arguments takes them from the expected type if that is the
  same enum, and otherwise infers them from the fields' values, as for generic functions (§2.13).
  `Option.None` alone is an error, since nothing determines `T`.

### Equality, defaults, conversions

- `==` and `!=` compare the variant and then its fields, and are available when every field of
  every variant supports `==` (so never when a field's type is a type parameter or a function).
  Enums are not ordered.
- **An enum's default value is its first variant, if that variant carries no data.** Otherwise the
  enum has no default value: a field of that type needs an initializer or, in a class, an assignment
  in every constructor (§2.10, "Default values" in §2.13).
- Enums without data convert to and from integers with a cast: `(int)Direction.East` is `1`, and
  `(Direction)i` panics if `i` is not the value of a variant. A variant's value is its position, or
  as in C, the value written for it or one more than the previous variant's
  ([07-interop.md](07-interop.md#75-enums-with-integer-representations)).

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
smallest unsigned integer that numbers the variants, or the `@Repr` type when one is given. When
no variant can hold an object reference, the enum is trivially copyable; otherwise copying and
destroying it copy and destroy only the active variant's fields, and assigning it stores the new
value before releasing the old one, as for structs (§3.2).

Enums without data may declare an explicit integer type with `@Repr(uint)` and give variants values,
and may be marked `@Flags`; see [07-interop.md](07-interop.md#75-enums-with-integer-representations).

## 2.9 Structs

A `struct` is a **value type**.

```java
struct Transform {
    public {
        Vec3 position;
        Quat rotation;
        Vec3 scale = Vec3.ONE;
    }
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
    public {
        Mat4 view;
        Mat4 proj;
        Vec3 eye;
        @Pad(4)
    }
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
    public {
        abstract void fire(World w);
        virtual float cooldown() { return 0.5; }
    }
}

final class Shotgun extends Weapon {
    public {
        override void fire(World w) { ... }
        override float cooldown() { return 1.0; }
    }
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

   > Implemented so far: an argument for a `ref`, `in`, `ref?`, or `in?` parameter or a `ref
   > struct` one must be `null`, a parameter of the constructor, or a field of one (`super(p.pos,
   > view)`), which need nothing held or computed before the base constructor runs.
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
[05-concurrency.md](05-concurrency.md)).

Strings are joined by [interpolation](06-syntax.md#string-interpolation), `$"{a}{b}"`, which
builds the result in one allocation. There is no `+` on strings, so there is one way to do it.
`StringBuilder` builds a string up piece by piece, as in a loop.

A literal refers to static text and never allocates; a string built at run time, as by
[interpolation](06-syntax.md#string-interpolation), is one counted allocation shared by its copies.

`==` and `!=` compare contents ([equality](06-syntax.md#equality)).

A string is stored as UTF-8 bytes, and **offsets and lengths count bytes**, never characters: a
`char` (a code point) takes 1 to 4 bytes. Byte offsets are what make finding and slicing O(1) per
step, and the names say which unit they mean, so no `length` can be mistaken for a character count:

```java
String word = "héllo";
word.byteLength()           // 6: 'é' takes two bytes, read in O(1)
word.charCount()            // 5: counted by reading the whole string
word.isEmpty()              // false
word.indexOf("l")           // 3, an int?: null when not found
word.slice(1, 2)            // "é": 2 bytes from offset 1, copied into a new string
word.startsWith("hé")       // also endsWith and contains
"a".compareTo("b")          // negative, zero, or positive
```

- `slice(start, length)` takes a byte offset and length like `Span.slice`, and panics if the
  range is out of bounds or would split a character, so a string is always valid UTF-8. A slice
  of the whole string is the string itself; any other is copied, keeping every string
  null-terminated ([07-interop.md](07-interop.md#strings)).
- `indexOf` returns the byte offset of the first occurrence, which is where `slice` can start.
  An empty string occurs at offset 0, and every string contains it.
- `compareTo` orders by byte, which for UTF-8 is the order of the code points. It doesn't apply
  locale rules or Unicode normalization: `"é"` written as one code point and as `e` plus a
  combining accent are different strings.
- `String` has no indexer, since neither a byte nor a code point is what `s[i]` would obviously
  mean. `s.bytes()` is a `ReadOnlySpan<byte>` of the UTF-8 bytes, without a copy: `s.bytes()[0]`,
  `for (byte b : s.bytes())`. Like any span (§2.11) it holds what it views, here the string, so the
  bytes stay valid even if the variable it came from is reassigned. A method can't return a view
  of a string field, which could be replaced while the caller uses it, as for an object field.
- A for-each loop over a string decodes its characters, and its index is each one's byte offset,
  which `slice` takes. The loop reads the string it started with, even if the variable is
  reassigned in the body. The characters are decoded copies, so they can't be borrowed with `ref`
  or `in`.

```java
for (char c : name) { ... }
for (int at, char c : path) {
    if (c == '/') { lastSlash = at; }
}
```

The standard library declares these methods in `String.vpl`, implemented by the runtime.

> Implemented so far: literals, interpolation, `print`, `==`, and the methods above. The count is
> not atomic yet, since there are no threads. `StringBuilder` is not implemented.

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
- `T[]` is an object holding its length and a pointer to its elements, which are allocated
  separately (two allocations; the elements are stored inline, not as references to boxes as in
  Java). It **never reallocates**, so references into it only need to keep the array alive.
- `T[N]`, `T[]`, and `List<T>` convert **implicitly** to `Span<T>` and `ReadOnlySpan<T>`.
- `Span<T>` follows the same rules as `ref` (cannot be a field, cannot be captured by an escaping
  closure, and can be returned only by a method viewing its receiver). Taking a span of a `List`
  borrows the list ([03-memory-model.md](03-memory-model.md#34-references)).

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
- `a.length` is a field, which can't be assigned. `new T[n]` panics if `n` is negative.
- Like any object, copying a `T[]` shares it.

> Implemented so far: one dimension per `new`; `new T[2][3]` is not supported yet.

### Lists `List<T>`

- `List<T>` is a standard library class: a pointer to its elements, its count, and its capacity.
  Adding past the capacity moves the elements to new storage, 4 elements at first and then twice
  the old capacity.
- The capacity never shrinks by itself: `clear` and `removeAt` keep the storage for reuse, which
  suits lists refilled every frame. `reserve(n)` grows the capacity to at least `n`, and `trim()`
  shrinks it to exactly `count()`, freeing the storage entirely when the list is empty. Unlike C++'s
  `shrink_to_fit`, `trim` always does so.
- While a list is borrowed, changing its length or capacity panics
  ([03-memory-model.md](03-memory-model.md#borrow-counters-on-containers)).
- `count()` is the number of elements. `first()` and `last()` are copies of the first and last
  elements, and `removeLast()` removes and returns the last one, as a stack's pop; all three are
  null when the list is empty.

### Spans

`Span<T>` and `ReadOnlySpan<T>` are standard library `ref struct`s
([03-memory-model.md](03-memory-model.md#spans)): a pointer and a length, with a bounds-checked
indexer, `length()`, and `slice(start, length)`. A `ReadOnlySpan`'s indexer returns `in T`, so its
elements can be read but not changed.

Conversions to spans are implicit, from:

- a fixed array stored in a variable, field, or element. A temporary fixed array, such as one a
  function returned, has no storage to view. A read-only fixed array (an `in` parameter, or a
  field in a struct method that isn't `mut`) converts only to `ReadOnlySpan`.
- a value of a type implementing the standard library's `Spannable<T>`, by calling its `asSpan()`,
  or `ReadOnlySpannable<T>`, by calling `asReadOnlySpan()`. `Array`, `List`, and `Span` implement
  them, and so can user types.

A generic function taking a span infers its element type from any of these:
`T first<T>(ReadOnlySpan<T> values)` accepts an `int[4]` or a `List<int>`.

### Queries

The standard library has LINQ-style queries, as extension methods on `ReadOnlySpan<T>`
([06-syntax.md](06-syntax.md#extension-methods)), so they work on fixed arrays, `T[]`, `List`, and
spans. There is no query syntax, only calls:

```java
var weak = mobs.where(m -> m.health < 5).select(m -> m.position).toList();
int alive = mobs.count(m -> m.health > 0);
Mob? target = mobs.where(m -> m.isHostile).first();
```

- **Adapters are lazy**: `where`, `select`, `take`, and `skip` return a `ref struct` that does
  nothing until a terminal operation or a `for` loop consumes it, and that holds its lambdas
  without allocating ([06-syntax.md](06-syntax.md#lambdas-held-by-ref-structs)). Like a span, it
  borrows the list it reads for as long as it exists, and it can't be stored; call `toList()` to
  keep a result.
- **Terminal operations** run the query: `toList()`, `count()`, `any(f)`, `all(f)`, `sum()`, and
  ones that return null when there is no answer: `first()`, `first(f)`, and `min()` and `max()`
  for `T extends Comparable<T>`.
- A chain compiles to one loop with the lambdas inlined, like a hand-written `for`.

Lazy adapters need two changes to the `ref struct` rules
([03-memory-model.md](03-memory-model.md#spans)): a `ref struct` may have fields of `ref struct`
type, as an adapter holds its source, and a generic `ref struct` may take a `ref struct` type
argument, as `Select<Where<...>>` does. Both stay second-class, so neither can outlive what it
views.

> Not implemented yet. Terminal operations come first, since they need only non-escaping lambdas
> and extension methods; adapters need lambdas held by `ref struct`s and the two rule changes.

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
- An interface method may be declared `mut`, as in `mut void reset();`, which lets a struct
  implement it with a `mut` method. A struct can't implement an interface method that isn't `mut`
  with one that is; a class may use any method, since calling a class's method never changes the
  reference it is called through. A generic body can call an interface method that isn't `mut` on
  any value, including one that isn't `mut` (§2.7) or is an `in` parameter; calling a `mut` one on a
  type parameter's value needs a mutable place, since the type argument may be a struct.

## 2.13 Generics

Java syntax, C++ template semantics: generics are **monomorphized** (one copy per instantiation,
zero overhead).

```java
interface Comparable<T> {
    int compareTo(T other);
}

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
    public {
        A first;
        B second;
    }
}

T identity<T>(T value) { return value; }

var p = Pair<int, float>(1, 2.5);         // struct type arguments are always written
var s = Pair<String, bool>(second: true, first: "a");
var x = identity(5);                      // function type arguments are inferred...
var y = identity<long>(5);                // ...or written explicitly
```

- Type parameters go after the name, for structs, functions, and methods. A method's type
  parameters may not reuse its struct's type parameter names.
- **Function type arguments are inferred** from the arguments. When arguments disagree, the type the
  others implicitly convert to is chosen: `max(1, 2L)` infers `long`. As for operators (§2.4), an
  integer constant takes another argument's type when that type holds it: `max(b, 1)` with a
  `byte b` infers `byte`. Inside type arguments there are
  no conversions, so `Pair<int, int>` and `Pair<long, long>` never unify.
- **Struct type arguments are always written.** There is no inference for construction.
- When a generic and a non-generic overload are equally good, the non-generic one is chosen.
- `Name<...>` in an expression is read as type arguments when followed by `(` or `.`. As in C#,
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
- Two standard library interfaces are satisfied by built-in types without implementing them:
  primitives and `String` are `Printable` ([06-syntax.md](06-syntax.md#string-interpolation)),
  and the numbers, `char`, and `String` are `Comparable` of themselves.

```java
interface Comparable<T> {
    int compareTo(T other);            // negative, zero, or positive
}

T maxOf<T extends Comparable<T>>(T a, T b) { return a.compareTo(b) >= 0 ? a : b; }

maxOf(3, 7);                // 7
maxOf("pear", "apple");     // "pear", by code point (§2.10, Strings)
5.compareTo(3);             // 1: the built-in types have a compareTo outside generic code too
```

`float` and `double` compare in a total order, as Java's `Double.compare`, so sorting never
depends on where NaNs are: `-0` sorts before `0`, and NaN after every other value, including
infinity, and the same as any other NaN. This differs from the operators, under which `-0 == 0`,
and NaN is unordered and unequal even to itself.

### Unbounded type parameters

Without bounds, a type parameter `T` supports only what every type supports: declaring, copying,
passing (including by `ref`/`in`), and returning values. In particular:

- `==` is not available on `T`, and no function overload accepts a `T` unless it is generic itself.
- `T()` is an error: `T` has no default value (it could be a non-null class reference).
- A struct with a field of type `T` has no default value inside generic code: `Pair<T, int>()` is an
  error, while `Pair<T, int>(first: x)` is fine, since it passes every field that lacks a default.
  From non-generic code, `Pair<int, int>()` works because `int` has a default.

### Default values

A type is *defaultable* if it has a default value: primitives, `String`, nullable types (`null`), structs whose fields each either have an initializer or a defaultable type, and enums
whose first variant carries no data (that variant is the default). Type
parameters and non-null class and interface references are not defaultable, so a struct with a
`Player` field and no initializer for it has no default value. A struct's memberwise constructor
requires an argument for each such field, and `T()` exists only when there are none (§6.3).

### Recursive structs

A struct cannot contain itself by value, including through type arguments
(`struct Rec { public { Wrap<Rec> w; } }`), and its field types cannot grow without end
(`struct Grow<T> { public { Grow<Pair<T, T>> next; } }`).
