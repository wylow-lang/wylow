<a id="patterns"></a>
# 15. Patterns

A pattern tests a value and may bind parts of it to names. Patterns appear after `is` in an
expression, and in the cases of a `switch` ([`is`](13-expressions.md#expressions.is),
[Switch statements](14-statements.md#statements.switch),
[Switch expressions](13-expressions.md#expressions.switch)). This chapter gives the kinds of
patterns, what matching does, bindings, reference bindings, cases and guards, and how a switch is
checked for exhaustiveness.

<a id="patterns.kinds"></a>
## 15.1 Kinds of patterns

A pattern either always matches or tests the value. These are the patterns:

| Pattern | Matches |
|---|---|
| `_` | Anything |
| `var name` | Anything, and binds it to `name` |
| `Variant`, `Enum.Variant` | That variant, whatever its fields |
| `Variant(p1, p2)` | That variant, when each field matches its pattern; every field is listed |
| `Struct(p1, p2)` | A struct value, when each field, in declaration order, matches its pattern; every field is listed |
| `null` | Null: a switch's nullable value, or a nullable field, in a case |
| `Type name`, `Type` | A class or interface value that is not null and is a `Type`. For a value type, only its own type |
| `42`, `-1`, `'a'`, `true` | An integer, `char`, or `bool` equal to the literal |

- The qualifier in `Enum.Variant` is the matched value's enum. Type arguments may be left out, since
  the value determines them: `case Result.Ok(var value)`.
- An integer pattern is a constant of the matched value's integer type. An unsuffixed literal
  converts to it where it fits, so matching a `byte` writes `case 200`, and matching a `long` with a
  value past the maximum of an `int` writes `case 2147483648L`
  ([Integer constants](05-conversions.md#conversions.constants)).
- A lone name is a variant when it matches an enum, and otherwise a type.
- A struct pattern deconstructs a struct value. The value is always of the pattern's own type, so
  the pattern matches when its field patterns do; nested, it tests the fields of a field
  (`Moved(Point(var x, _, _))`). On a nullable value it also tests that the value is not null. Every
  field of the struct is listed, every field is visible where the pattern is written, and the type
  arguments may be left out.
- `null` is a pattern only in a case, alone or inside another pattern, and only on a type that can
  be null. A whole `is` pattern cannot be `null`, since `x == null` says that; `x is Moved(null)` is
  valid.
- Floating-point and string literals are not patterns.
- It is a compile-time error for a type pattern to name a function type
  ([Function types](04-types.md#types.functions)); a nullable function is checked with `== null`.

- It is a compile-time error to name a variant that the matched enum does not have.
- It is a compile-time error to give parentheses to a variant that carries no data, or to list a
  number of fields other than the variant's.
- It is a compile-time error to write a literal pattern of a type that is not an integer, `char`, or
  `bool`.

**Example:**

```java
enum Shape { Point, Circle(float radius) }

class Animal { }
class Dog extends Animal { }

void main() {
    var s = Shape.Circle(2);
    if (s is Shape.Point()) { }     // error[WYL0378]
    if (s is Shape.Circle(1, 2)) { }// error[WYL0379]
    if (s is Shape.Circle(1.5)) { } // error[WYL0376]
}
```

<a id="patterns.matching"></a>
## 15.2 Matching

A switch evaluates its value once, before any case is tried, and tries each case in order against
the value as it is then. A case matches when its pattern matches, and then its guard, if it has one,
is true. An `is` expression is true when its pattern matches.

- A switch matches its value where it is, without a copy of its own. A guard that changes the value,
  or calls code that does, changes what the later cases test. Each case tests the value as it is
  when that case is tried, including whether it is null.
- The value of a switch has a type of its own: `null` alone and a `void` call have none, and both
  are compile-time errors.
- If the value is a temporary, it lives until the switch ends, or for a switch expression, until the
  switch's value is produced.
- It is a compile-time error to match a value of a `ref struct` type against patterns.
- It is a compile-time error for a pattern never to match a value of the type it is tested against:
  a struct or variant pattern of another type, a literal of another type, or a type pattern whose
  type is not the value type's own.

**Example:**

```java
enum State { Idle, Running(int speed) }
enum Other { A, B }

void main() {
    var state = State.Idle;
    switch (state) {
        case Other.A -> { }          // error[WYL0380]
        default -> { }
    }
}
```

<a id="patterns.bindings"></a>
## 15.3 Bindings

`var name` and a type pattern with a name bind the matched value, or a part of it, to a name. The
name is a variable of the pattern's type and is a copy of the value it binds. A pattern binding
cannot be declared `mut` ([Mutability](06-variables.md#variables.mutability)).

- A name bound in a case is in scope in the case's guard and body, and its value is definitely
  assigned there.
- A name bound by an `is` in an `if` or `while` condition is in scope for the whole statement, and
  is definitely assigned where the test succeeded ([Narrowing](16-flow.md#flow.narrowing)).
- The names are bound left to right. Every bound value is copied out of the matched value before an
  earlier binding's local is released, since a destructor it runs may change the matched value.
- It is a compile-time error for a case with several patterns to bind a name: the name would have a
  different value for each pattern.

**Example:**

```java
enum Shape { Circle(int radius), Rect(int w, int h) }

void main() {
    var shape = Shape.Circle(2);
    switch (shape) {
        case Circle(var r), Rect(var w, var h) -> { }   // error[WYL0382]
    }
}
```

<a id="patterns.references"></a>
## 15.4 Reference bindings

A field pattern may bind a reference to the field instead of a copy: `in var name` is a read-only
reference, and `ref var name` is a mutable one. A type may be written before the name, as in
`in Point name`.

- A field of a unique type is bound by reference: a binding by value would copy it.
- A reference binding refers into the matched value, so the matched value is a place that stays put
  while the binding is in scope: a local or parameter, or a field or element path rooted at one,
  that nothing in the binding's scope assigns, a lambda that may run there included. A value in an
  object, which any call might replace, is copied into a local first.
- `ref` needs a mutable place. A variant's fields are not changed in place, so a variant field is
  bound with `in` only; `ref` binds a field of a struct pattern
  ([Variants](09-enums.md#enums.variants)).
- A reference binding is a second-class reference
  ([Second-class values](17-memory.md#memory.second-class), [References](17-memory.md#memory.references)).
- `is ref` and `is in` test a `ref?` or `in?` reference: they are true when it refers to a place,
  and bind that place.
- It is a compile-time error to use `ref` or `in` as a pattern of a case: a reference binding is
  written only after `is`.
- It is a compile-time error to use `is ref` or `is in` on a value that is not a nullable reference.
- It is a compile-time error if the pattern's type is not the type the reference refers to.
- It is a compile-time error to move a unique field out of the matched value with a pattern.

**Example:**

```java
struct Container {
    float value;

    mut ref? float at(int index) { return ref value; }
}

void main() {
    mut Container c = Container(1);
    int n = 1;
    if (n is ref int place) { }         // error[WYL0383]
    if (c.at(0) is ref int wrong) { }   // error[WYL0384], it refers to a 'float'
}
```

<a id="patterns.cases"></a>
## 15.5 Cases

A case is written `case Pattern -> statement` for a switch statement, and `case Pattern ->
expression;` for a switch expression. There is no fallthrough.

- `case A, B -> ...` matches either pattern. A case with several patterns matches when any of them
  does, and binds no names.
- The cases are tried in order. `default` matches every value that the cases above it do not, and it
  is a compile-time error for it not to be the last case.
- A guard is written `when condition` after the pattern of a case.
- A case of a switch expression produces a value
  ([Switch expressions](13-expressions.md#expressions.switch)).
- `break` and `continue` inside a case apply to the enclosing loop
  ([`break` and `continue`](14-statements.md#statements.jumps)).

<a id="patterns.cases.guards"></a>
### 15.5.1 Guards

`case Pattern when condition -> ...` matches when the pattern matches and then the condition, a
`bool`, is true.

- The guard runs after the case's names are bound, so the guard and the case see the same values.
- The names the pattern binds are in scope in the guard. The case is to its guard as an `if`'s body
  is to its condition: names an `is` in the guard binds are in scope in the guard, and what the
  guard narrows stays narrowed.
- In `case A, B when c`, the guard applies to the whole case.
- It is a compile-time error for a case with `default` or `null` to have a guard.
- A guarded case covers no value: it never counts toward exhaustiveness, and it never makes a later
  case unreachable. It is a compile-time error if the cases above already match every value it
  matches.
- It is a compile-time error for the guard's condition not to have type `bool`.

**Example:**

```java
enum Shape { Circle(int radius), Rect(int w, int h) }

void main() {
    var s = Shape.Circle(2);
    switch (s) {
        case Circle(var r) when r > 1 -> { }
        default when true -> { }     // error[WYL0386]
    }
}
```

<a id="patterns.cases.null"></a>
### 15.5.2 `case null`

A switch with a `case null` takes its value as it is, nullable or not.

- Without a `case null`, a nullable value is narrowed before the switch, or the switch is a
  compile-time error. `default` does not match null; write `case null, default ->` for both.
- `case null` matches only null, and may be written anywhere among the cases.
- Only `null` may be listed with `default`: it is a compile-time error to write `case 1, default`.
- The other cases' patterns match the non-null value, so `case Dog d`, `case var v` (a `v` of the
  non-null type), and `case Circle(var r)` match as they would on a narrowed value.
- A case's test narrows the switched local in that case's body, as an `if`'s would. To name the
  value, bind it.
- It is a compile-time error for `case null` to test a value whose type is not nullable, or a local
  that narrowing knows is not null there.
- A `ref?` or `in?` reference is tested with `is ref`, not with `case null`
  ([Nullable references](17-memory.md#memory.nullable-references)).

**Example:**

```java
class Node { }

void main() {
    Node? node = null;
    switch (node) {
        case null -> { }
        case var n -> { }
    }
    int n = 1;
    switch (n) {
        case null -> { }             // error[WYL0388]
        default -> { }
    }
}
```

<a id="patterns.exhaustiveness"></a>
## 15.6 Exhaustiveness and unreachable cases

Every switch is exhaustive: its cases match every value of the switched type, or it ends with
`default`.

- Over an enum, listing every variant is enough. Over `bool`, listing both values is enough. Over an
  integer or a class, a `default` or a pattern that always matches is needed. A struct value is
  covered by a struct pattern whose field patterns all always match.
- A nullable value is null or a value of its type, and it is a compile-time error if the cases do
  not cover both. A switched field of a nullable type is treated the same way.
- It is a compile-time error if the cases do not cover every value. The error names a value that no
  case handles, such as `Pair(South, _)` or `Moved(null)`.
- It is a compile-time error if `default` is not the last case.
- It is a compile-time error for a case to be unreachable: one whose values the cases above it all
  match, including a `default` after cases that already cover every value.
- A type test on a class counts as possibly failing, so it never makes a later case unreachable
  unless it always matches.

**Example:**

```java
enum Direction { North, East, South, West }

void main() {
    var d = Direction.North;
    switch (d) {                    // error[WYL0393]
        case North, East -> { }
        case South -> { }
    }
}
```
