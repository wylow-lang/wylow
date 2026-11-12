<a id="expressions"></a>
# 13. Expressions

An expression computes a value or denotes a place. This chapter gives the order in which an
expression's parts are evaluated, and what each kind of expression means: names and literals,
construction, member access and calls, the operators, `is`, casts, `try`, string interpolation, and
constant expressions.

<a id="expressions.order"></a>
## 13.1 Evaluation order

The parts of an expression are evaluated left to right: the operands of an operator, the arguments
of a call, the elements of an array literal, and the parts of an interpolated string
([String interpolation](#expressions.interpolation)).

```java
int a() { std::print("a"); return 1; }
int b() { std::print("b"); return 2; }

void main() {
    int sum = a() + b();            // prints "a", then "b"
}
```

- In `obj.x + f()`, the field `obj.x` is read before `f` runs.
- In an assignment, the object holding the target is evaluated before the value: in `a.b.x = f()`,
  `a.b` is evaluated before `f`.
- An element target is found, its index and bounds check included, before the value:
  `list[i()] = f()` calls `i` before `f`, and `a[f()] += 1` calls `f` once.
- A call of a function value, or a call given one, may run a lambda that changes a captured local
  ([Captures](12-functions.md#functions.lambdas.captures)).
- The value of a `switch` is evaluated once, before any case is tried
  ([Switch statements](14-statements.md#statements.switch),
  [Switch expressions](#expressions.switch)).
- In `a?.b(args)`, the arguments are not evaluated when `a` is null ([Null operators](#expressions.null)).
- A unique value used in place, as the operand of an operator, is not moved
  ([Moves](17-memory.md#memory.moves)).

<a id="expressions.primary"></a>
## 13.2 Names, literals, and `this`

A primary expression is a name, a literal, `this`, `super`, a parenthesized expression, a construction
with `new`, an array literal, or a switch expression.

```ebnf
Primary = { Identifier "::" } Identifier [ TypeArguments ] | Literal | "this" | "super"
        | "(" Expression ")" | New | ArrayLiteral | SwitchExpression .
```

A name is resolved to the declarations in scope: a local, a parameter, a loop variable, a pattern
binding, a function, a type, an enum variant, or a value of a type the file sees. A name followed by
type arguments is read as type arguments when the matching `>` is followed by `(` or `.`
([Type arguments](11-generics.md#generics.arguments)).

- It is a compile-time error to use a type as a value, as in `var p = Point;`.
- It is a compile-time error to use a function or method as a value where a function value is not
  expected; it is called instead ([Function references](12-functions.md#functions.references)).
- It is a compile-time error to use a name that no declaration in scope provides.
- It is a compile-time error to use an instance field or method by its name alone where there is no
  `this`: in a static method, a method with a by-value receiver
  ([By-value receivers](07-structs.md#structs.methods.by-value)), a field initializer, the arguments
  of `this(...)` or `super(...)`, or a default value.
- `this` is the receiver of the instance method or constructor that holds it, and only a struct,
  class, or enum has one.
- It is a compile-time error to use `this` outside a struct, class, or enum, where no receiver
  exists, or in a method with a by-value receiver.
- `super` names the base class of the class whose method or constructor holds it. `super.m(...)`
  calls the method `m` of the base class on `this`, without dispatch to an override, and
  `super(...)` calls a base class constructor.
- It is a compile-time error to use `super` for anything but calling a base class constructor or
  method ([Inheritance](08-classes.md#classes.inheritance)).

**Example:**

```java
void main() {
    var p = Point;                  // error[WYL0279]
    std::print(undefined);          // error[WYL0281]
    std::print(this);               // error[WYL0283]
}
```

<a id="expressions.construction"></a>
## 13.3 Construction

```ebnf
New          = "new" Type ( ArgumentList | "[" Expression "]" ) .
ArrayLiteral = "[" [ Expression { "," Expression } [ "," ] ] "]" .
```

`new` appears only where memory is allocated: for a class object or a heap array. A struct value is
made by calling its constructor without `new`, and an enum value by naming a variant.

```java statements
var p = Point(1, 2);                // a struct value
var o = new Order();                // a class object
var a = new int[16];                // a heap array
var d = Direction.North;            // an enum value
```

- It is a compile-time error to create a class object without `new`, or a struct, enum, or
  interface object with `new`.
- It is a compile-time error to set fields in braces after a construction: a field is set by a named
  argument of the constructor ([Default values and named arguments](12-functions.md#functions.arguments)).
- A generic type is constructed with its type arguments written
  ([Type arguments](11-generics.md#generics.arguments)).
- `new T[n]` is an array of `n` elements, each the default value of `T`
  ([Arrays](04-types.md#types.arrays)). `new T[n]` panics if `n` is negative.
- An array literal `[e1, e2, ...]` makes a fixed array
  ([Fixed arrays](04-types.md#types.fixed-arrays)). A comma may follow the last element.
- `T()` is the default value of a defaultable struct ([The default value](07-structs.md#structs.constructors.default)).
- It is a compile-time error to construct a type parameter, as `T()` or `new T()`.

**Example:**

```java
class Order { }

struct Point { pub int x; pub int y; }

void main() {
    var o = Order();                // error[WYL0287]
    var p = new Point(1, 2);        // error[WYL0289]
    var q = Point(x: 1, y: 2) { };  // error[WYL0286]
}
```

<a id="expressions.members"></a>
## 13.4 Member access

`e.name` names a field or method of the value or place `e`.

- It is a compile-time error to name a member that the type does not have, or that is not visible
  where the access is written ([Visibility](03-programs.md#programs.visibility)).
- It is a compile-time error to use `super` for a field.
- It is a compile-time error to name a static member through a value: it is named through its type.
- A fixed array's only members are `length` and the members of the span it converts to
  ([Fixed arrays](04-types.md#types.fixed-arrays)).
- It is a compile-time error to name a field of an enum variant: a variant's fields are read with a
  pattern ([Variants](09-enums.md#enums.variants)).
- It is a compile-time error to use a method without calling it, where a value is expected.
- It is a compile-time error to access a member of a nullable value with `.`, except of a local or
  by-value parameter that narrowing has proved not null there
  ([Nullable types](04-types.md#types.nullable), [Null operators](#expressions.null)).
- A member of a type parameter is available when a bound provides it
  ([Bounds](11-generics.md#generics.bounds)).

**Example:**

```java
struct Point { pub int x; }

void main() {
    var p = Point(1);
    std::print(p.y);                // error[WYL0297]
    std::print(p.x());              // error[WYL0301]
}
```

<a id="expressions.calls"></a>
## 13.5 Calls

```ebnf
ArgumentList = "(" [ Argument { "," Argument } ] ")" .
Argument     = [ Identifier ":" ] [ "ref" ] Expression .
```

`f(args)` calls a function or a function value, and `e.m(args)` calls a method of `e`. The
arguments are evaluated left to right and converted to the parameters
([Parameter modes](12-functions.md#functions.parameters.modes)).

- It is a compile-time error to call something that is not a function: a value of another type, a
  field, or a method named without its parentheses.
- It is a compile-time error to call a method the type does not have.
- It is a compile-time error to call an instance method through its type, or a static method through
  a value.
- It is a compile-time error to call a function declared `unsafe` outside `unsafe` code
  ([Safety](20-unsafe.md#unsafe)).
- A call of a method on a place does not copy the place
  ([Copies](07-structs.md#structs.copies)). A call of a `mut` struct method on a temporary is a
  compile-time error ([`mut` methods](07-structs.md#structs.methods.mut)).
- A function value is called like a function, whether it is a local, a field, or the result of a
  call ([Function types](04-types.md#types.functions)).
- A call whose receiver may replace a stored function value holds the function value until the call
  returns ([Holds](17-memory.md#memory.holds)).

**Example:**

```java
struct Point {
    pub int x;

    pub int twice() { return x * 2; }
    pub static Point origin() { return Point(0); }
}

void main() {
    Point.origin();
    Point(1).twice();
    Point(1).origin();              // error[WYL0303]
}
```

<a id="expressions.indexing"></a>
## 13.6 Indexing

`a[i]` reads one element of a fixed array, an array, or a span. A value of a struct or class type
may be indexed through its indexers ([Indexers](12-functions.md#functions.indexers)).

- It is a compile-time error to use more than one index on a fixed array, an array, or a span.
- It is a compile-time error to index a value of any other type, other than through an indexer.
- It is a compile-time error to index a `String`: its bytes are read with `bytes()`
  ([`String`](04-types.md#types.string)).
- An index converts to `int` ([Where conversions apply](05-conversions.md#conversions.contexts)).
- An array, fixed array, or span index outside 0 to `length` − 1 panics. A fixed array's index that
  is a constant outside that range is a compile-time error
  ([Fixed arrays](04-types.md#types.fixed-arrays)).
- It is a compile-time error to index a value of a type parameter.

**Example:**

```java
void main() {
    int[3] values = [1, 2, 3];
    std::print(values[1, 2]);       // error[WYL0305]
    std::print(1[0]);               // error[WYL0306]
}
```

<a id="expressions.arithmetic"></a>
## 13.7 Arithmetic

`+`, `-`, `*`, `/`, and `%` apply to numbers; `-` also negates one. Unary `-` applies to the signed
numeric types, and `~` to the integer types. `+`, `-`, `*`, `/`, `%`, `&`, `|`, `^`, and `~` never
run a program's own code: an operator always means what it means for the built-in types.

<a id="expressions.arithmetic.operands"></a>
### 13.7.1 Operand and result types

A binary arithmetic or bitwise operator first converts both operands to one type:

- the operands' type, when they have the same type;
- otherwise, when one operand is a constant that converts to the other operand's type, the other
  operand's type: `byte light = 10; light - 1` is a `byte`, and `1 + 2u` is a `uint`
  ([Integer constants](05-conversions.md#conversions.constants));
- otherwise, the type that one operand converts to implicitly. Between two integer types that do
  not convert to each other, it is the smallest integer type that both convert to: `int + uint` is
  a `long`.

Arithmetic keeps its operands' type: `byte + byte` is a `byte`. A small type is not promoted to
`int`. The type of an operation never depends on where its result is stored.

- It is a compile-time error to apply an operator to operands that have no common type:
  `long + ulong`, or `int + float`.
- It is a compile-time error to apply unary `-` to an unsigned type, or `-` or `~` to `bool`,
  `char`, or a non-numeric type.
- `char` has no arithmetic of its own: a `char` operand of an arithmetic, bitwise, or shift operator
  is its code point as an `int` (`c - '0'` is an `int`), and it is a compile-time error to compare
  a `char` with a number ([`char`](04-types.md#types.primitives.char)).
- `&`, `|`, and `^` also apply to two `bool` values ([Logical operators](#expressions.logical)).
- A nullable operand is narrowed first; no operator accepts a nullable value
  ([Null operators](#expressions.null)).

**Example:**

```java
void main() {
    long a = 1;
    ulong b = 2;
    var c = a + b;                  // error[WYL0308]
    var d = -b;                     // error[WYL0307]
}
```

<a id="expressions.arithmetic.overflow"></a>
### 13.7.2 Overflow and division

- Integer arithmetic panics on overflow in a debug build, and wraps in a release build. For an
  unsigned type, a result below zero is an overflow. Floating-point arithmetic follows IEEE 754: a
  result too large is an infinity, and never a panic ([Build modes](01-introduction.md#introduction.build-modes)).
- Integer division by zero panics in every build, and so does the division of the minimum value of
  a signed type by −1. Floating-point division by zero gives an infinity or a NaN.
- `a.wrappingAdd(b)`, `a.wrappingSub(b)`, `a.wrappingMul(b)`, `a.checkedAdd(b)`, `a.checkedSub(b)`,
  and `a.checkedMul(b)` give a result that wraps, or a nullable that is null on overflow
  ([Methods of primitive types](04-types.md#types.primitives.methods)).

<a id="expressions.shifts"></a>
## 13.8 Shifts

`a << n` and `a >> n` shift the integer `a` by `n` bits. The result has `a`'s type, and `n` may
have any integer type. `>>` is arithmetic for a signed type and logical for an unsigned one.

- The count is masked to the width of `a`'s type: an `int` shifts by `n & 31`. A shift never panics.
- It is a compile-time error if the count is a constant outside 0 to the width of `a`'s type minus
  one.

**Example:**

```java
void main() {
    byte hi = 1;
    var packed = hi << 8;           // error[WYL0309]
    var widened = (int)hi << 8;
}
```

<a id="expressions.bitwise"></a>
## 13.9 Bitwise operators

`&`, `|`, and `^` apply to two integers, and to two `bool` values. `~` applies to an integer and
gives its bitwise complement. Their operands and result follow the rules of
[Operand and result types](#expressions.arithmetic.operands).

- `a & b`, `a | b`, and `a ^ b` on `bool` are not short-circuit operators
  ([Logical operators](#expressions.logical)).

<a id="expressions.comparison"></a>
## 13.10 Comparison and equality

`==` and `!=` apply to two numbers, two `char` values, two `bool` values, two values of an enum, two
values of a struct, two class or interface references, two `String` values, and a nullable of one of
these and `null` ([Nullable value types](#expressions.null)).

- Two numbers and two `char` values compare by value; `char` compares by code point.
- `==` on a struct is true when every field equals the field of the same name of the other value,
  and on an enum when the values have the same variant and equal fields
  ([Equality](07-structs.md#structs.equality), [Equality](09-enums.md#enums.equality)).
- `==` on a class or interface compares identity: both refer to the same object, or both are
  `null` ([Equality](08-classes.md#classes.equality)). `==` on a `String` compares contents
  ([`String`](04-types.md#types.string)).
- `!=` is the negation of `==`.
- `<`, `>`, `<=`, and `>=` apply to two numbers, and to two `char` values by code point. They do
  not apply to enums, structs, classes, interfaces, or `String`.
- It is a compile-time error to apply `==` or `!=` to a type that does not support them, such as a
  function type, or to two types when neither converts to the other.
- It is a compile-time error to compare a nullable value for ordering, and to compare a nullable
  value with a non-nullable one other than for `==` and `!=`
  ([Nullable value types](#expressions.null)).

**Example:**

```java
struct Callback { (fn() -> int)? run; }

void main() {
    var c = Callback(null);
    std::print(c == c);             // error[WYL0310]
    std::print(true < false);       // error[WYL0308]
}
```

<a id="expressions.logical"></a>
## 13.11 Logical operators

`&&`, `||`, and `!` apply to `bool`. `a && b` is false without evaluating `b` when `a` is false, and
`a || b` is true without evaluating `b` when `a` is true. A condition has type `bool`
([`bool`](04-types.md#types.primitives.bool)).

<a id="expressions.null"></a>
## 13.12 Null operators

A nullable type's value is `null` or a value of its type ([Nullable types](04-types.md#types.nullable)).
Three operators work with `null`:

| Form | Meaning |
|---|---|
| `a?.b` | `null` when `a` is null, otherwise `a.b`; the result is nullable |
| `a ?? b` | `a` when it is not null, otherwise `b` |
| `a!` | `a`, or a panic when it is null |

- `a?.b(args)` does not evaluate the arguments when `a` is null.
- `a?.b` chains like any nullable result: `a?.b.c` is a compile-time error when `a?.b` is nullable;
  write `a?.b?.c`. The result is `b`'s type made nullable, and `void` for a call that returns
  nothing.
- `??` requires a nullable left operand. Its type is the left operand's non-null type when the right
  operand is non-null, and a nullable type otherwise. The right operand is evaluated only when the
  left one is null.
- `!` panics when its operand is null.
- It is a compile-time error to apply `!`, `?.`, or `??` to a value whose type is not nullable.
- It is a compile-time error to apply `??` to a nullable non-escaping function
  ([Escaping and non-escaping](12-functions.md#functions.lambdas.escaping)).
- `a?.m()` calls a struct method on a copy, so it is a compile-time error when `m` is `mut` or an
  extension with a `this ref` receiver: the changes would be lost.
- `a!` and `a?.m()` work on a copy, so `a!.x = 1` and `a!.m()` are compile-time errors, as for any
  temporary ([`mut` methods](07-structs.md#structs.methods.mut)). The errors are the same when `a`'s
  type is not copyable.
- When `a`'s type is not copyable, `a!` behaves as `a` does, and panics when it is null: used by
  value or as a receiver taken by value it moves `a`, and as any other receiver or with `.` it is
  used in place ([Moves](17-memory.md#memory.moves)). `a?.b` uses `a` in place, unless `b` is a method
  that takes its receiver by value.
- `a ?? b`, and `a?.m()` with a method `m` that takes its receiver by value, move a non-copyable `a`,
  which is moved afterwards whether or not it was null. `b` and the arguments of `m` are moved only
  when they are evaluated.
- No operator other than `?.`, `??`, `!`, `==`, and `!=` accepts a nullable value: narrow it first
  ([Narrowing](16-flow.md#flow.narrowing)).
- A nullable value's `==` and `!=` compare it with `null`, with another nullable value, or with a
  value, when the value type supports them. Both sides become nullables of the type the values
  compare as, and `null` equals only `null`.
- `?.` on a `ref?` or `in?` reaches the member in the place it refers to, not in a copy
  ([Nullable references](17-memory.md#memory.nullable-references)).

**Example:**

```java
class Node { pub int value; }

void main() {
    Node? node = null;
    std::print(node.value);         // error[WYL0397], reached with '?.' or '!'
    std::print(node?.value ?? 0);
    std::print(node!.value);        // panics
    std::print((node ?? new Node()).value); // valid
    int n = 1;
    std::print(n!);                 // error[WYL0312]
}
```

<a id="expressions.conditional"></a>
## 13.13 The conditional operator

`c ? a : b` evaluates `c`, and then `a` when it is true or `b` when it is false. The condition has
type `bool`, and only the chosen branch is evaluated.

- The two branches have one type, the one the context expects, or else the type that both convert
  to; a nullable branch makes the result nullable.
- It is a compile-time error if the branches have no common type.
- It is a compile-time error if the branches are values of a `ref struct` type or references: the
  result would view or refer to whichever branch was chosen.

**Example:**

```java
struct Bag {
    int[2] items;

    mut ref? int at(int index) { return ref items[index]; }
}

void main() {
    mut Bag bag = Bag();
    var c = true ? bag.at(0) : null;    // error[WYL0321]
    var d = true ? 1 : "one";           // error[WYL0319]
}
```

<a id="expressions.switch"></a>
## 13.14 Switch expressions

A switch expression chooses one of several expressions:

```ebnf
SwitchExpression = "switch" "(" Expression ")" "{" { CaseLabel [ Guard ] "->" Expression ";" } "}" .
```

```java
enum State { Idle, Running(int n), Done }

String name(State state) {
    return switch (state) {
        case Idle            -> "idle";
        case Running(var n)  -> $"running {n}";
        case Done            -> "done";
    };
}
```

- The value is evaluated once, before any case is tried. It is a compile-time error for it to have
  no type of its own.
- Every case is an expression followed by `;`. It is a compile-time error for a case to be a block.
- It is a compile-time error for a case to produce no value.
- It is a compile-time error if the cases do not have one type: the expected type, or else the type
  that every other case converts to, as for `?:`.
- It is a compile-time error if the cases have incompatible types, or if no case produces a value.
- It is a compile-time error for a switch expression to produce a `ref struct` or a reference: the
  result would view or refer to one of its cases.
- A switch expression is exhaustive as a switch statement is
  ([Exhaustiveness](15-patterns.md#patterns.exhaustiveness)).

**Example:**

```java
enum State { Idle, Done }

String name(State state) {
    return switch (state) {
        case Idle -> { std::print(1); } // error[WYL0322]
        case Done -> "done";
    };
}
```

<a id="expressions.is"></a>
## 13.15 `is`

`x is pattern` is true when `pattern` matches the value of `x`
([Patterns](15-patterns.md#patterns)).

```java statements
var node = Option.Some(1);
if (node is Some(var value) && value > 0) { ... }
```

- A pattern that always matches cannot follow `is`: `x is _` and `x is var y` are compile-time
  errors.
- `null` is not a pattern of `is`; compare with `== null` instead.
- `is` cannot test a `void` value.
- A type pattern tests the type of a class or interface value only. It is a compile-time error to
  test a value of a value type for a type other than its own.
- Names bound by a pattern in an `if` or `while` condition are in scope for the whole statement, and
  are definitely assigned where the test succeeded
  ([Bindings](15-patterns.md#patterns.bindings), [Narrowing](16-flow.md#flow.narrowing)).

**Example:**

```java
class Animal { }
class Dog extends Animal { }

void main() {
    Animal a = new Dog();
    if (a is _) { }                 // error[WYL0331]
    if (a is null) { }              // error[WYL0329]
    int n = 1;
    if (n is int m) { }             // error[WYL0330]
}
```

<a id="expressions.casts"></a>
## 13.16 Casts

`(T) e` converts the value of `e` to `T` ([Casts](05-conversions.md#conversions.casts)).

```ebnf
Cast = "(" Type ")" UnaryExpression .
```

A cast to a named type is recognized as `(Name)` followed by something that can only start an
operand. `(Format)-1` is a subtraction; write `(Format)(-1)`. A cast to a pointer type, such as
`(byte*) p` or `(Point*) &value`, is recognized by its `*`, whatever follows it. A cast is valid between two numeric
types, between integers and `char`, between integers and enums without data, and wherever an
implicit conversion exists.

- It is a compile-time error to cast between any other types, such as `bool` and an integer, `char`
  and a floating-point type, or `String` and a number.
- It is a compile-time error to cast to a subclass or an interface that the source type does not
  implement; a value is tested for a derived class with `is`
  ([Kinds of patterns](15-patterns.md#patterns.kinds)).
- A cast from an integer to `char` panics when the value is not a Unicode scalar value, and a cast
  from an integer to an enum without data panics when no variant has the value.
- It is a compile-time error to cast a nullable value to its non-nullable type: narrow it first
  ([Nullable conversions](05-conversions.md#conversions.nullable)).

**Example:**

```java
void main() {
    int n = (int)true;              // error[WYL0083]
    byte b = (byte)300;             // 44
    char c = (char)65;              // 'A'
    int? maybe = 1;
    int m = (int)maybe;             // error[WYL0083]
}
```

<a id="expressions.try"></a>
## 13.17 `try`

`try expr` evaluates `expr`, which has a `Result` type
([`Result`](18-errors.md#errors.result)).

- When the result is `Ok(v)`, the value of `try` is `v`.
- When the result is `Err(e)`, the enclosing function returns `Err(e)` at that point.
- The operand is used by value on both paths: a value of a copyable type is copied, and any other
  value is moved ([Moves](17-memory.md#memory.moves)).
- `try` may be used anywhere in an expression, a `for` loop's update included. It returns when it is
  evaluated, so what was evaluated before it has happened and the rest is not evaluated. An operand
  that is not evaluated, such as the right side of `&&` when the left is false, does not return.
- In an assignment, the object holding the target is evaluated first, as always.
- The statement's temporaries are released by the return, last made first, before the function's
  locals ([Release](17-memory.md#memory.release)).
- It is a compile-time error if the operand of `try` does not have a `Result` type.
- It is a compile-time error to use `try` outside a function, method, or lambda whose return type
  is a `Result`.
- It is a compile-time error if the enclosing function's `Result` does not have the same error type
  as the operand's; the error is converted explicitly first.
- It is a compile-time error to use `try` in a lambda whose return type is inferred.

**Example:**

```java
enum ParseError { Bad }
enum IoError { Missing }

Result<int, ParseError> parse(String text) { ... }
Result<int, IoError> read(String path) { ... }

Result<int, ParseError> twice(String text) {
    return Ok(try parse(text) * 2);
}

Result<int, ParseError> load(String path) {
    return try read(path);              // error[WYL0335]
}
```

<a id="expressions.interpolation"></a>
## 13.18 String interpolation

A string literal prefixed with `$` is interpolated: each `{expression}` in it is replaced by the
expression's value, and the value of the literal is a new `String`. Only `$` strings interpolate, so
braces in an ordinary string mean nothing special.

```java statements
var order = new Order();
log($"order {order.id}: {order.quantity} items");
```

- `{{` and `}}` in a `$` string stand for `{` and `}`. A lone `}` is a compile-time error. Escapes
  work as in any string ([Escapes](02-lexical.md#lexical.literals.escapes)).
- The parts are evaluated left to right, each exactly once.
- It is a compile-time error to write a conditional `a ? b : c` in a hole without parentheses, since
  `:` starts a format specifier.
- An interpolated value is printable. The primitive types and `String` are, and are written as
  `print` writes them: a `char` as its character, and a nullable as its value or `null`. A
  floating-point value is the shortest text that reads back as the same value, and `inf`, `-inf`,
  and `nan` name the rest.
- A value of any other type is printable when its type implements `Printable`, and is written by
  its `toString()` ([`Printable`](a-library.md#library.printable)).
- It is a compile-time error to interpolate a value of a type that does not implement `Printable`.

**Example:**

```java
struct Point { pub int x; pub int y; }

void main() {
    var p = Point(1, 2);
    std::print($"{p}");             // error[WYL0336]
    std::print($"{p.x},{p.y}");
}
```

<a id="expressions.interpolation.format"></a>
### 13.18.1 Format specifiers and alignment

A hole is `{value,alignment:format}`, and both parts may be left out:
`$"{name,-12}|{score,6:D}|{ratio,8:F2}"`.

- The **alignment** is an integer literal, optionally negative. The value's text is padded with
  spaces to at least that many code points: on the left for a positive alignment, and on the right
  for a negative one. Any printable value may be aligned, including `null`.
- It is a compile-time error if the alignment is too large to write.
- The **format specifier** is a letter and an optional precision of up to two digits, checked
  against the value's type. A nullable value is formatted when it is not null.
- It is a compile-time error if the format specifier is not a letter with an optional precision of
  up to two digits.
- It is a compile-time error if the letter does not apply to the value's type.

| Specifier | Applies to | Writes | Precision | Example |
|---|---|---|---|---|
| `D`, `d` | integers | decimal | minimum digits, zero-padded after the sign | `{-42:D5}` is `-00042` |
| `X`, `x` | integers | hexadecimal, upper- or lowercase | minimum digits | `{255:x4}` is `00ff` |
| `B`, `b` | integers | binary | minimum digits | `{5:B8}` is `00000101` |
| `F`, `f` | `float`, `double` | fixed-point | digits after the point; default 2 | `{3.14159:F3}` is `3.142` |
| `E`, `e` | `float`, `double` | exponential, with the case of the `E` given | digits after the point; default 6 | `{1500.0:E2}` is `1.50E+003` |

- `X` and `B` write a negative value's two's-complement bits at the type's width: `{-1:X}` is
  `FFFFFFFF` for an `int`.
- `F` and `E` round to the nearest digit. Infinities and NaN are written as `print` writes them.
- A `Printable` type has no specifiers of its own; it may only be aligned.

**Example:**

```java
void main() {
    var a = 1;
    var b = 2.5;
    std::print($"{a:F2}");          // error[WYL0340]
    std::print($"{b:D3}");          // error[WYL0340]
    std::print($"{a:F123}");        // error[WYL0338]
}
```

<a id="expressions.constants"></a>
## 13.19 Constant expressions

A **constant expression** is a literal, `null`, an enum variant, or an operator applied to constant
expressions. A struct constructor given constant expressions is also a constant expression.

A parameter's default value is a constant expression
([Default values and named arguments](12-functions.md#functions.arguments)). An array length in a
type, an annotation's argument, and an enum variant's value are literals, as their sections give
([Fixed arrays](04-types.md#types.fixed-arrays), [Annotation syntax](22-annotations.md#annotations.syntax),
[Integer representations](09-enums.md#enums.repr)). An operator applied to constants is not an
integer constant, so `byte b = 1 + 2;` is an error ([Integer constants](05-conversions.md#conversions.constants)).

<a id="expressions.precedence"></a>
## 13.20 Operator precedence

```ebnf
Expression      = Lambda | Conditional .
Conditional     = Coalesce [ "?" Expression ":" Expression ] .
Coalesce        = Binary [ "??" Coalesce ] .
Binary          = UnaryExpression { BinaryOperator UnaryExpression | "is" Pattern } .
BinaryOperator  = "*" | "/" | "%" | "+" | "-" | "<<" | ">>" | "<" | "<=" | ">" | ">=" | "==" | "!="
                | "&" | "^" | "|" | "&&" | "||" .
UnaryExpression = ( "-" | "!" | "~" | "&" | "try" ) UnaryExpression | Cast | Postfix .
Postfix         = Primary { ArgumentList | "." Identifier [ TypeArguments ]
                          | "[" Argument { "," Argument } "]" | "?." Identifier | "!" } .
```

Operators group by these levels, tightest first. A cast and the unary operators apply to the operand
that follows them, and member access, calls, indexing, `?.`, and `!` apply to the operand before them,
tighter than any level here.

| Level | Operators | Groups |
|---|---|---|
| 1 | `*`, `/`, `%` | left to right |
| 2 | `+`, `-` | left to right |
| 3 | `<<`, `>>` | left to right |
| 4 | `<`, `<=`, `>`, `>=`, `is` | left to right |
| 5 | `==`, `!=` | left to right |
| 6 | `&` | left to right |
| 7 | `^` | left to right |
| 8 | `\|` | left to right |
| 9 | `&&` | left to right |
| 10 | `\|\|` | left to right |
| 11 | `??` | right to left |
| 12 | `? :` | right to left |

A lambda is not an operand ([Lambdas](12-functions.md#functions.lambdas)), and assignment is a statement
([Assignment](14-statements.md#statements.assignment)). Parentheses group anything.

**Example:**

```java statements
int a = 1 + 2 * 3;                  // 7
int b = 1 << 1 + 1;                 // 4
bool c = 1 << 2 < 5;                // true
bool d = 1 < 2 == 2 > 1;            // true
bool e = true || false && false;    // true
int f = false ? 1 : true ? 2 : 3;   // 2
```
