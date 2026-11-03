<a id="conversions"></a>
# 5. Conversions

A **conversion** gives a value of one type the type another expects. An **implicit conversion**
happens wherever a context expects a type ([Where conversions apply](#conversions.contexts)). An
**explicit conversion** is a cast ([Casts](#conversions.casts)). Every implicit conversion is also a
valid cast.

<a id="conversions.contexts"></a>
## 5.1 Where conversions apply

An expression is converted implicitly to the type that its context expects, in these contexts:

- the initializer of a variable, field, or parameter default that has a declared type;
- the value assigned by `=` or a compound assignment, to the type of the variable assigned;
- an argument, to the type of its parameter;
- the value of a `return`, to the return type;
- an element of an array literal, to the array's element type;
- an index, to `int`;
- a condition, including a `when` guard, to `bool`;
- the value a lambda returns, to the return type of its function type.

The operands of an operator are converted by the rules of the operator
([Operands](13-expressions.md#expressions.arithmetic.operands)).

- It is a compile-time error if an expression has no implicit conversion to the type its context
  expects.

Two types are the same only if they are built from the same declaration with the same type
arguments. No conversion applies inside type arguments, so there is none from `Array<int>` to
`Array<long>`, or from `List<Dog>` to `List<Animal>`.

**Example:**

```java
byte b = 300;                   // error[WYL0079]
float f = i;                    // error[WYL0079], 'i' is an int
long[] wide = new int[2];       // error[WYL0079]
if (count) { ... }              // error[WYL0079], not a bool
```

<a id="conversions.identity"></a>
## 5.2 Identity and upcasts

Every type converts to itself.

A class type converts to its base classes, and to every interface that it or a base class
implements. An interface type converts to the interfaces it extends. A nullable class or interface
type converts to a nullable type of its base classes and interfaces; a non-nullable one converts to
either. A struct or enum type converts to no interface, and a type parameter to none of its bounds
([Interfaces on structs](10-interfaces.md#interfaces.structs), [Bounds](11-generics.md#generics.bounds)).

No conversion goes from a base class or interface to a derived class. A value is tested for a
derived class with a pattern ([Kinds of patterns](15-patterns.md#patterns.kinds)).

**Example:**

```java
Animal a = Dog();               // Dog extends Animal
Animal? maybe = a;
Dog d = a;                      // error[WYL0079]
Named n = Point();              // error[WYL0079], 'Point' is a struct
```

<a id="conversions.numeric"></a>
## 5.3 Numeric conversions

A number converts implicitly to another numeric type only if every value of the source type is a value
of the target. For this rule `nint` and `nuint` hold 64 bits as sources and 32 bits as targets, so
that a conversion is valid on every target. These are the conversions:

| From | Implicitly to |
|---|---|
| `sbyte` | `short`, `int`, `long`, `nint`, `float`, `double` |
| `byte` | `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `nint`, `nuint`, `float`, `double` |
| `short` | `int`, `long`, `nint`, `float`, `double` |
| `ushort` | `int`, `uint`, `long`, `ulong`, `nint`, `nuint`, `float`, `double` |
| `int` | `long`, `nint`, `double` |
| `uint` | `long`, `ulong`, `nuint`, `double` |
| `long`, `ulong` | none |
| `nint` | `long` |
| `nuint` | `ulong` |
| `float` | `double` |
| `double` | none |
| `char` | `int`, `uint`, `long`, `ulong`, `nint`, `nuint` |

An integer converts to `float` only if the integer type is 16 bits or narrower, because a `float`
holds every integer only up to 2^24; `int` and `uint` convert to `double`, which holds every
integer up to 2^53, and `long` and `ulong` to neither. `char` converts to the code point it holds, and
to no floating-point type. No type converts implicitly to `char`, and no integer converts to or from
`bool`.

Two conversions in a row are not one conversion: `char` converts to `int`, and `int` to `double`,
but `char` does not convert to `double`.

**Example:**

```java
long l = someInt;
double d = someUint;
float f = someInt;              // error[WYL0079]
char c = 65;                    // error[WYL0079]
```

<a id="conversions.constants"></a>
## 5.4 Integer constants

An **integer constant** is an integer literal, optionally preceded by a unary `-`.

- It is a compile-time error if the value of an integer constant is outside the values of its type
  (the type its suffix gives, `int` for none), where the value of `-n` is its negation: `-2147483648`
  is an `int` constant.

An integer constant without a suffix converts implicitly to every integer type that holds its value
on every target, so `byte b = 200;` is valid and `byte b = 300;` is not; for `nint` and `nuint` the
value must be one that `int` or `uint` holds. A suffix chooses the type, so `int i = 1L;` is an error.
Any integer constant, with a suffix or not, also converts to `float` or `double` if the target holds
its value exactly: `float x = 0;` is valid and `float f = 16777217;` is not.

An integer constant does not convert to `char`.

**Example:**

```java
byte a = 200;
sbyte b = -128;
float c = 7;
int big = 3000000000;           // error[WYL0080]
uint negative = -1;             // error[WYL0079]
```

<a id="conversions.nullable"></a>
## 5.5 Nullable conversions

- `null` converts to every nullable type and to every pointer type.
- A value of type `T` converts to a nullable type `U?` if `T` converts to `U`. So `int` converts to
  `int?` and to `long?`, and an integer constant to `float?`.
- A nullable value type `T?` converts to a nullable value type `U?` if `T` converts to `U`; a null
  value stays null.

No nullable type converts to a non-nullable type. A nullable local variable or by-value parameter
that [narrowing](16-flow.md#flow.narrowing) has proved not null is used as its non-nullable type,
and `!` and `??` give the value of a nullable expression ([Null operators](13-expressions.md#expressions.null)).
Conversions of nullable class types are those of [Identity and upcasts](#conversions.identity).

**Example:**

```java
long? a = 5;                    // an int converts to long?
int? b = 5;
long? c = b;
Node m = null;                  // error[WYL0079]
Node definite = n.next;         // error[WYL0079], 'n.next' is a Node?
```

<a id="conversions.spans"></a>
## 5.6 Span conversions

A value converts implicitly to `ReadOnlySpan<T>` or `Span<T>` of the same element type `T` in
these cases:

- A fixed array with elements of type `T`, stored in a variable, field, or element, converts to
  `Span<T>` or `ReadOnlySpan<T>`.
- A value of a type that implements `Spannable<T>` converts to `Span<T>`, and one that implements
  `ReadOnlySpannable<T>` to `ReadOnlySpan<T>`. The conversion calls `asSpan()` or `asReadOnlySpan()`
  ([`Spannable`](a-library.md#library.spannable)). `Array<T>`, `List<T>`, and `Span<T>` implement
  them.

Conversions to spans have these limits.

- It is a compile-time error to convert a fixed array to a span if the array is a temporary, such
  as a function's result: it has no storage to view.
- It is a compile-time error to convert a fixed array that is read-only (an `in` parameter, or a
  field of a struct in a method that is not `mut`) to a `Span<T>`. It converts to
  `ReadOnlySpan<T>`.
- It is a compile-time error to convert a fixed array or a struct value that lies inside a nullable
  value type's variable to a span, unless that variable cannot be assigned again: assigning it
  `null` would destroy the elements the span views.
- The element type must be the same: `int[2]` does not convert to `Span<long>`.

These errors are all reported as a failed implicit conversion, with the reason attached.
Converting a list or other object to a span holds or borrows it while the span is used
([Borrows](17-memory.md#memory.borrows)).

**Example:**

```java
int sum(ReadOnlySpan<int> values) { ... }

sum(array);                     // an int[4] variable
sum(list);                      // a List<int>
Span<int> s = makeArray();      // error[WYL0079], a temporary
void f(in int[2] v) { Span<int> t = v; }   // error[WYL0079]
```

<a id="conversions.references"></a>
## 5.7 Reference conversions

`null` converts to `ref? T` and `in? T`. A `ref? T` converts to `in? T` of the same `T`. No other
conversion applies to a reference type, and none changes the type `T` that it refers to
([Nullable references](17-memory.md#memory.nullable-references)). How a place is passed to a
`ref`, `in`, `ref?`, or `in?` parameter is a rule of
[Parameter modes](12-functions.md#functions.parameters.modes).

<a id="conversions.functions"></a>
## 5.8 Function conversions

An escaping function type converts to its non-escaping form, and a `shared` function type to the
same type without `shared`. Nothing converts the other way. Function types whose parameters differ
in `escaping` are different types, and neither converts to the other
([Escaping and non-escaping](12-functions.md#functions.lambdas.escaping)).

A lambda, a function reference, and a `?:` or `switch` expression that chooses between them have no type
of their own: each converts to a function type that fits it ([Lambdas](12-functions.md#functions.lambdas.typing),
[Function references](12-functions.md#functions.references),
[Choices](12-functions.md#functions.choices)).

- It is a compile-time error if a lambda or a function reference is converted to a type that is not a
  function type.
- It is a compile-time error if a choice of lambdas is converted to a type that is not a function
  type.

**Example:**

```java
int n = () -> 1;                // error[WYL0081]
int m = up ? x -> x : x -> 1;   // error[WYL0082]
```

<a id="conversions.weak"></a>
## 5.9 Weak conversions

A value converts to `Weak<T>` if it converts to `T?`: a class or interface reference of type `T` or
a type that converts to it, a nullable one, or `null`. `Weak<T>` converts to no other type; its
target is read with `get()` ([Weak references](17-memory.md#memory.weak)).

**Example:**

```java
Weak<Node> parent = node;
parent = null;                  // refers to nothing
Node strong = parent;           // error[WYL0079]
```

<a id="conversions.casts"></a>
## 5.10 Casts

A cast `(T) e` converts the value of `e` to `T`. Its form is given in
[Casts](13-expressions.md#expressions.casts). A cast is valid between any two numeric types, between
integers and `char`, between integers and enums without data, and wherever an implicit conversion
exists.

- It is a compile-time error if a cast is applied between any other types, such as `bool` and an
  integer, `char` and a floating-point type, or `String` and a number.

The result of a cast between numbers is:

| Conversion | Result |
|---|---|
| Integer to another integer type | The value modulo 2^n for the width `n` of the target, as a value of the target type: `(byte)300` is 44, `(uint)-1` is 4294967295, `(sbyte)200` is −56 |
| Floating-point to integer | The value rounded toward zero, then limited to the target's range; NaN gives 0: `(int)-2.5` is `-2` |
| Integer to `float` or `double` | The nearest value, a tie going to the one whose last bit is 0: `(float)16777217` is 16777216.0 |
| `double` to `float` | The nearest value; one too large is an infinity |
| `char` to an integer type | The code point, as an integer conversion: `(byte)(char)300` is 44 |

The result of a cast to or from `nint` or `nuint` depends on its width
([Implementation-defined behavior](01-introduction.md#introduction.implementation-defined)).

A cast from an integer to `char` panics if the value is not a Unicode scalar value: above 0x10FFFF, or
from 0xD800 to 0xDFFF. A cast from an enum without data to an integer gives the value of its variant,
as a cast of that integer to the target type ([Integer representations](09-enums.md#enums.repr)); a
cast from an integer to an enum without data panics if no variant has the value.

**Example:**

```java
int a = (int)3.9;               // 3
byte b = (byte)300;             // 44
Direction d = (Direction)1;     // panics if no variant has the value 1
int c = (int)true;              // error[WYL0083]
```
