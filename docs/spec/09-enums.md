<a id="enums"></a>
# 9. Enums

An enum is a value type with a fixed set of variants, each of which may carry data. This chapter gives how
an enum is declared, how its values are made, compared, and converted to integers, and the attributes
that give an enum without data an integer representation.

<a id="enums.declarations"></a>
## 9.1 Enum declarations

```ebnf
EnumDeclaration = { Annotation } [ Visibility ] [ "unique" ] "enum" Identifier [ TypeParameters ]
                  [ "implements" TypeList ] "{" [ Variants ] [ ";" { EnumMethod } ] "}" .
Variants        = Variant { "," Variant } [ "," ] .
Variant         = Identifier [ "=" IntegerLiteral ] | Identifier "(" VariantField { "," VariantField } ")" .
VariantField    = Type Identifier .
```

An enum is a **value type**: a value is stored in its variable, field, or element, and copied where a value
is copied, shallowly, as a struct's is ([Copies](07-structs.md#structs.copies)). A value of an enum is one of
its variants, with the values of the variant's fields. An enum can be generic
([Chapter 11](11-generics.md#generics)), and can implement interfaces
([Interfaces of structs and enums](10-interfaces.md#interfaces.structs)).

- The variants are separated by commas, with an optional comma after the last. The methods follow the
  variants, after a `;`.
- It is a compile-time error if an enum declares `extends`, a constructor, a field, or a destructor, or a
  variant after the first method.
- It is a compile-time error if an enum contains itself by value
  ([Recursive structs](07-structs.md#structs.recursive)).

**Example:**

```java
enum Direction { North, East, South, West }

enum Reply<T> {
    Pending,
    Ready(T value),
    Failed(String reason);

    pub bool isPending() { return this is Pending; }
}
```

<a id="enums.variants"></a>
## 9.2 Variants

A variant has a name, and fields if it is declared with a parenthesized list. A value of the enum is made by
naming a variant:

- `Enum.Variant` for a variant without fields, and `Enum.Variant(values)` for a variant with fields, with one
  value for each field, in order.
- A value is read by a pattern. The fields of a variant are not members of the enum: `state.target` is an
  error ([Patterns](15-patterns.md#patterns.kinds)).

An enum value is **immutable**. No field of a variant is changed in place; the variable that holds the value is
assigned a new one.

- It is a compile-time error if two variants of an enum have the same name.
- It is a compile-time error if a variant has a visibility: it is as visible as its enum.
- It is a compile-time error if a field of a variant is declared `mut` or has a default value.
- It is a compile-time error to write a variant that carries data without its values, or one that carries
  none with parentheses, or with a number of values other than the number of its fields.
- It is a compile-time error to write type arguments after a variant: they belong to the enum,
  as in `Reply<int>.Ready(1)`.

**Example:**

```java
enum Shape {
    Circle(int radius),
    Empty
}

var a = Shape.Circle(2);
var b = Shape.Empty;
var c = Shape.Circle;                   // error[WYL0157]
```

<a id="enums.variants.implicit"></a>
### 9.2.1 Leaving out the enum

Where the type of a value is known, the name of the enum, and its type arguments, may be left out:
`Direction next = East;`. The type is known in:

- the initializer of a declaration with a written type, and the value assigned to a variable, field, or
  element of a known type;
- a `return` value, and a field initializer;
- the right side of `==` or `!=` when the left side is an enum;
- the branches of a `?:` and the cases of a `switch` expression, when the expression has a known type;
- an argument of a call, when every overload that takes that many arguments has the same parameter type there,
  after the type arguments of the receiver are substituted. This covers functions, methods, constructors,
  `super(...)`, and the fields of a variant: `list.add(Ok(3))` and `Ok(Ok(x))` returned as a
  `Result<Result<int, E>, E>`. If the overloads differ, or the parameter's type uses the type parameters of
  the function being called, which are inferred from the arguments, there is no known type there.

A local variable, field, function, or type with the name of a variant takes precedence over the variant.

A generic enum written without type arguments takes them from the known type if that is the same enum.
Otherwise they are inferred from the values of the variant, as for a generic function
([Inferring type arguments](11-generics.md#generics.arguments.inference)). It is a compile-time error
if a type argument is determined by neither.

**Example:**

```java
enum Option<T> { None, Some(T value) }

Option<int> a = None;                   // from the declared type
var b = Option.Some(1);                 // Option<int>, from the value
var c = Option<int>.None;               // written
var d = Option.None;                    // error[WYL0160]
```

<a id="enums.methods"></a>
## 9.3 Methods

The methods of an enum follow its variants. A method is called on an enum value, and `this` is that value.
Because an enum value is immutable, the methods can be called on an `in` parameter and on a variable that
is not `mut`.

- It is a compile-time error if a method of an enum is declared `mut`.
- It is a compile-time error if a member of an enum is declared `virtual`, `override`, `abstract`, or
  `final`.

<a id="enums.unique"></a>
## 9.4 Unique enums

An enum that has a variant field of a unique type is declared `unique enum`, and is moved rather than
copied, as a unique struct is ([Unique structs](07-structs.md#structs.unique)).

- It is a compile-time error if a variant field has a unique type and the enum is not declared `unique`.
- A generic enum is unique when its type arguments make one of its fields unique.
- A unique enum has no destructor. Releasing a value of it releases the fields of its variant, which runs the
  destructors of the fields that have one.

<a id="enums.equality"></a>
## 9.5 Equality

`==` and `!=` are defined on an enum type when every field of every variant supports them. Two values are
equal when they are the same variant and their fields are equal. It is a compile-time error to apply them to
an enum with a field of a type that does not support them, such as a function type or a type parameter. An
enum is not ordered: `<`, `>`, `<=`, and `>=` are not defined
([Comparison](13-expressions.md#expressions.comparison)).

<a id="enums.default"></a>
## 9.6 The default value

The default value of an enum is its first variant, if that variant carries no data. An enum whose first variant
carries data has no default value ([Default values](04-types.md#types.defaults)): a field of that type has an
initializer, or is assigned in every constructor of its class
([Order of construction](08-classes.md#classes.construction)).

`T()` is not an expression for an enum.

<a id="enums.repr"></a>
## 9.7 Integer representations

An enum whose variants carry no data has an **integer type** and gives each variant an integer **value**.
Without `@Repr`, the integer type is `int`.

```ebnf
Repr = "@Repr" "(" IntegerType ")" .
```

`@Repr(T)` before an enum declaration gives the enum the integer type `T`, one of the integer types
([Integers](04-types.md#types.primitives.integers)). The enum does not convert to `T` implicitly.

- The first variant without a value has the value 0, and any other has the value one more than the variant
  before it. A variant has the value written for it, an integer literal, with `= value`.
- It is a compile-time error if a value, written or counted, is not a value of the integer type.
- It is a compile-time error if two variants have the same value.
- It is a compile-time error if a variant's value is not an integer literal.
- It is a compile-time error if an enum whose variants carry data has `@Repr`, or a variant with a value.
- It is a compile-time error if `@Repr` is given more than once, has other than one argument, or has a
  type that is not an integer type.
- It is a compile-time error to give the integer type with `:` after the name.

A cast ([Casts](05-conversions.md#conversions.casts)) converts between such an enum and an integer type:
`(int)Direction.East` is the value of the variant `East` as an `int`. `(Direction)i` panics if no variant of
`Direction` has the value `i`. In `unsafe` code, `bitcast<Direction>(i)` converts without that check
([Pointers](20-unsafe.md#unsafe.pointers)). `Direction.tryFrom(i)` returns the variant with the
value `i`, or `null`: a `Direction?`.

**Example:**

```java
@Repr(uint)
enum Format {
    Undefined = 0,
    Rgba = 37,
    Bgra,                               // 38
}

uint code = (uint)Format.Bgra;
Format f = (Format)code;
```

<a id="enums.flags"></a>
## 9.8 Flags

`@Flags` on an enum whose variants carry no data makes the enum a set of bits. Every combination of the bits
of the variants' values is a value of the enum.

- `|`, `&`, and `^` apply to two values of the enum, and `~` to one, and give a value of the enum, bit by bit.
- `a.has(flag)` tests whether `flag` is set in `a`.
- A cast from an integer to a `@Flags` enum does not panic: the value of the enum is any combination of bits.

**Example:**

```java
@Flags
@Repr(ulong)
enum WindowFlags {
    Fullscreen = 0x1,
    Resizable  = 0x20,
}

var f = WindowFlags.Fullscreen | WindowFlags.Resizable;
bool r = f.has(WindowFlags.Resizable);  // true
```
