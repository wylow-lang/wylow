<a id="statements"></a>
# 14. Statements

A statement does something and produces no value. This chapter gives blocks and scopes, declarations
and expression statements, assignment, the conditional and loop statements, `switch`, the jumps,
`return`, and `defer`.

```ebnf
Statement      = Block | Declaration | ExpressionStatement | Assignment | If | While | DoWhile
               | For | ForEach | Switch | Break | Continue | Return | Defer .
Block          = "{" { Statement } "}" .
```

<a id="statements.blocks"></a>
## 14.1 Blocks and scopes

A **block** is a sequence of statements between `{` and `}`. A local variable is in scope from its
declaration to the end of the block that holds it, and a variable of an inner block hides nothing of
the same name in an outer one ([Local variables](06-variables.md#variables.locals)).

- The body of `if`, `else`, `while`, `do`, `for`, and `for`-each is a block.
- `else if` is a block whose one statement is another `if`.
- It is a compile-time error if a body of one of these statements is not enclosed in braces.
- A statement after one that cannot complete — after `return`, `break`, `continue`, or a call that
  never returns — is allowed and does nothing ([Reachability](16-flow.md#flow.returns)).

**Example:**

```java
void main() {
    if (true) print("one");         // error[WYL0341]
    if (true) { print("one"); }
}
```

<a id="statements.declarations"></a>
## 14.2 Declaration statements

A declaration statement declares a local variable ([Local variables](06-variables.md#variables.locals)).
It is a statement, so it cannot appear in the initialization or update of a `for` loop except in the
initialization, which declares the loop's variable
([`while`, `do`, and `for`](#statements.loops)).

- It is a compile-time error to declare a variable in the update part of a `for` loop.

<a id="statements.expressions"></a>
## 14.3 Expression statements

An expression statement evaluates an expression for its effect.

- It is a compile-time error for an expression statement to be anything but a call, a construction, a
  `try` whose value is one of these, or a `?.` call: an expression whose value is not used has no
  effect.
- It is a compile-time error to use a call that returns a `Result` as a statement: the result is
  handled or discarded with `_ =` ([Results must be used](18-errors.md#errors.unused)).

<a id="statements.expressions.discard"></a>
### 14.3.1 Discarding a value

`_ = expr;` evaluates `expr` and ignores its value.

- It is a compile-time error to discard the value of a call that returns `void`.
- `_ = expr;` is a statement, not an expression.

**Example:**

```java
int readValue() { return 1; }
void log(String text) { }

void main() {
    readValue();                    // error[WYL0343]
    1 + 2;                          // error[WYL0343]
    _ = readValue();
    _ = log("x");                   // error[WYL0344]
}
```

<a id="statements.assignment"></a>
## 14.4 Assignment

Assignment sets a place to a value. `=`, the compound assignments (`+=`, `-=`, `*=`, `/=`, `%=`,
`&=`, `|=`, `^=`, `<<=`, `>>=`), and `++` and `--` are statements, not expressions.

```java
mut int count = 0;
count = 1;
count += 2;
count++;
++count;
```

- It is a compile-time error to use an assignment, a compound assignment, `++`, or `--` inside an
  expression: `a = b = c`, `f(x = 1)`, and `a[i++]` are errors.
- `++` and `--` may be written before or after their operand, with the same meaning. They apply to a
  numeric variable.
- It is a compile-time error to apply `++` or `--` to a value of a type that is not numeric.
- The target of an assignment is a mutable place
  ([Mutability](06-variables.md#variables.mutability)).
- It is a compile-time error to assign to a field or element of a temporary value, to an element of
  a temporary array, or to any other expression that is not a place.
- It is a compile-time error to assign to a read-only place.
- The value assigned converts to the type of the target
  ([Where conversions apply](05-conversions.md#conversions.contexts)).
- A compound assignment works in the variable's type: `b += 1` is valid for a `byte b`, and `b += i`
  for an `int i` is a compile-time error, since `b + i` is an `int`
  ([Operand and result types](13-expressions.md#expressions.arithmetic.operands)).
- A compound assignment reads the target after its object and index are evaluated, and before the
  value ([Evaluation order](13-expressions.md#expressions.order)).

**Example:**

```java
void main() {
    mut int a = 1;
    mut int b = 2;
    a = b = 3;                      // error[WYL0345]
    var c = a = 1;                  // error[WYL0345]
    make().x = 1;                   // error[WYL0348]
    1 = 2;                          // error[WYL0350]
}

Point make() { return Point(0); }
struct Point { pub int x; }
```

<a id="statements.if"></a>
## 14.5 `if`

`if (condition) { ... } else { ... }` runs the first block when the condition is true, and the
`else` block otherwise. The condition has type `bool`, and an `else` may be another `if`.

```java
if (order.paid) {
    ship(order);
} else if (order.cancelled) {
    refund(order);
} else {
    wait(order);
}
```

- A local is narrowed in the branch where the condition proves it not null
  ([Narrowing](16-flow.md#flow.narrowing)).

<a id="statements.loops"></a>
## 14.6 `while`, `do`, and `for`

```java
while (condition) { ... }

do { ... } while (condition);

for (init; condition; update) { ... }
```

- The condition of each loop has type `bool`.
- A `for` loop's initialization is a declaration or an expression statement, its condition may be
  left out, and its update is an expression statement. A loop with no condition runs until a jump
  ends it.
- A `while` loop's condition is evaluated before each iteration, and a `do` loop's after each.
- `break` ends the loop, and `continue` begins the next iteration
  ([`break` and `continue`](#statements.jumps)).
- Every variable declared by the initialization is in scope in the condition, the update, and the
  body.
- It is a compile-time error for the update of a `for` loop to declare a variable.

**Example:**

```java
void main() {
    mut int i = 0;
    while (i < 3) {
        i++;
    }
    for (mut int j = 0; j < 3; j++) {
        print(j);
    }
}
```

<a id="statements.for-each"></a>
## 14.7 For-each

A for-each loop visits each element of a value:

```ebnf
ForEach      = "for" "(" [ "mut" ] [ "ref" | "in" ] ( "var" | Type ) Identifier
               [ "," [ "mut" ] [ "ref" | "in" ] ( "var" | Type ) Identifier ] ":" Expression ")" Block .
```

```java
for (Order order : orders) { ... }
for (ref Order order : orders) { order.ship(); }
for (in Order order : orders) { ... }
```

- A for-each loops over a range ([Ranges](#statements.for-each.ranges)), a fixed array, a span, a
  value that converts to a span, such as an `Array` or a `List`
  ([Span conversions](05-conversions.md#conversions.spans)), or the characters of a `String`, whose
  index is a byte offset ([`String`](04-types.md#types.string)).
- `T x` copies each element, `ref T x` refers to it, and `in T x` refers to it read-only. `var x`
  copies with the element's type.
- It is a compile-time error to loop `ref` over a read-only value: an `in` parameter, a variable or
  field that is not `mut`, a `ReadOnlySpan`, or a temporary fixed array. It is a compile-time error
  to loop `ref` over a temporary array.
- A loop variable is a new variable in each iteration, and is immutable unless declared `mut`. A
  copy that is changed does not change the element.
- It is a compile-time error to declare a loop variable `ref?` or `in?`.
- The loop holds what it iterates for its whole duration, so a list is borrowed until the loop ends
  ([Borrows](17-memory.md#memory.borrows)). A fixed array is referred to, not copied, unless it is a
  temporary.
- A loop over a value that converts to a span iterates a `ReadOnlySpan` unless its variable is `ref`,
  so a read-only loop works on a read-only source.
- It is a compile-time error to loop over the elements of a temporary value.
- It is a compile-time error to loop over elements inside a nullable value: narrow it first.
- It is a compile-time error to loop over a value of a type that is not one of these.

**Example:**

```java
void main() {
    int[3] values = [1, 2, 3];
    for (int value : values) { }
    for (int value : 3) { }         // error[WYL0353]
}
```

<a id="statements.for-each.ranges"></a>
### 14.7.1 Ranges

A range is `start..end` or `start..=end` and loops over the integers from `start` up to, or through,
`end`:

```java
for (int i : 0..n) { ... }          // 0, 1, ..., n - 1
for (int i : 0..=n) { ... }         // 0, 1, ..., n
```

- The bounds are integers, evaluated once, start first. The variable has their common type
  ([Operand and result types](13-expressions.md#expressions.arithmetic.operands)), or its declared
  integer type, and it is a copy: assigning it does not change the iteration.
- It is a compile-time error if a bound is not an integer, or the loop variable's type is not an
  integer type.
- A range whose end is before its start runs no iterations. `a..=b` stops without overflowing when
  `b` is the type's maximum value.
- A range is valid only in a for-each.
- It is a compile-time error to give a range an index.
- It is a compile-time error to bind the values of a range with `ref` or `in`.

**Example:**

```java
void main() {
    for (int i, int value : 0..3) { }   // error[WYL0359]
    for (ref int i : 0..3) { }          // error[WYL0360]
    for (int i : 0.0..3.0) { }          // error[WYL0361]
}
```

<a id="statements.for-each.indices"></a>
### 14.7.2 Indices and keys

A for-each may declare two variables: the first is the element's index, and the second is the
element itself.

```java
for (int i, ref Order order : orders) { ... }
```

- The index is a copy of the position, of type `int`.
- It is a compile-time error to declare the index with a type other than `int`, or with `ref` or
  `in`.
- The index of a for-each over a `String` is a byte offset.

**Example:**

```java
void main() {
    int[3] values = [1, 2, 3];
    for (long i, int value : values) { }    // error[WYL0364]
    for (ref int i, int value : values) { } // error[WYL0363]
}
```

<a id="statements.switch"></a>
## 14.8 Switch statements

A switch statement selects one of several blocks:

```java
switch (state) {
    case Idle -> wander();
    case Running(var target) -> {
        moveToward(target);
        attack(target);
    }
    case Done -> { }
}
```

- The value is evaluated once, before any case is tried. It is a compile-time error for it to have
  no type of its own.
- It is a compile-time error to switch on a `void` value, on `null`, or on a nullable class or
  interface value that no `case null` handles.
- A case has an arrow: `case Pattern -> statement`. There is no fallthrough.
- `case A, B -> ...` matches either pattern, and such a case binds no names
  ([Cases](15-patterns.md#patterns.cases)).
- The body of a case is a block or a single statement other than a declaration.
- It is a compile-time error to declare a variable directly in a case; use a block.
- It is a compile-time error to write `:` instead of `->`.
- It is a compile-time error if `default` is not the last case.
- Every switch is exhaustive ([Exhaustiveness](15-patterns.md#patterns.exhaustiveness)).
- `break` and `continue` inside a case apply to the enclosing loop; a switch is not a loop and
  needs no `break` ([`break` and `continue`](#statements.jumps)).
- A switch expression is in [Switch expressions](13-expressions.md#expressions.switch).

**Example:**

```java
enum State { Idle, Done }

void main() {
    var state = State.Idle;
    switch (state) {
        case Idle: { }              // error[WYL0366]
        case Done -> { }
    }
    switch (state) {
        default -> { }
        case Idle -> { }            // error[WYL0370]
    }
}
```

<a id="statements.jumps"></a>
## 14.9 `break` and `continue`

`break;` ends the innermost loop, and `continue;` begins its next iteration. Inside a switch, both
apply to the enclosing loop.

- It is a compile-time error to use `break` or `continue` outside a loop.

**Example:**

```java
void main() {
    break;                          // error[WYL0371]
}
```

<a id="statements.return"></a>
## 14.10 `return`

`return;` ends a function that returns nothing, and `return expr;` returns a value converted to the
function's return type. `return ref place;` returns a reference
([Returning references](12-functions.md#functions.ref-returns)).

- It is a compile-time error to return a value from a function, method, constructor, destructor, or
  lambda that returns `void`.
- It is a compile-time error to return a value from a constructor or a destructor.
- It is a compile-time error to return no value from a function that returns a value, or to return
  a value of a type that does not convert to the return type.
- A `return` in a lambda returns from the lambda ([Lambdas](12-functions.md#functions.lambdas)).
- What a `return` releases is in [Release](17-memory.md#memory.release).

**Example:**

```java
void log(String text) { return text; }      // error[WYL0374]

int value() {
    return;                                 // error[WYL0372]
}
```

<a id="statements.defer"></a>
## 14.11 `defer`

`defer statement;` runs its statement when the enclosing block exits, whether it completes normally
or through `return`, `break`, or `continue`. Several deferred statements of one block run in reverse
order.

```java
void send(Buffer buffer) {
    buffer.begin();
    defer buffer.end();
    ...
}
```

- The deferred statement is not run when a panic ends the program.
- A deferred statement is in the scope of the block that defers it.
- A deferred statement that cannot complete ends the enclosing function or loop as it would
  anywhere else.
