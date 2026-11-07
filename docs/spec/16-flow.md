<a id="flow"></a>
# 16. Flow rules

The rules in this chapter relate the order of a program's statements to what a local holds: it is
assigned before it is read, a local that is not `mut` receives one value, a nullable local is
narrowed where a check shows it is not null, and a moved value is not used again. They also give
what a constructor assigns and when a function returns a value.

<a id="flow.definite-assignment"></a>
## 16.1 Definite assignment

A local variable declared without an initializer holds no value until an assignment gives it one. A
parameter and a local declared with an initializer hold a value from their declaration.

- It is a compile-time error to use a local where it might not have been assigned: to read it, to
  call a method on it, to use a field or element of it, to take a `ref` or `in` reference to it, or
  to pass it to a function. A use in a lambda follows [Lambdas](#flow.lambdas).

The check follows every path through the program:

- An assignment assigns the local if it comes before the use on every path to that use. Where
  paths meet after an `if` or a `switch`, the local is assigned only if it is assigned on every
  path into the meeting point.
- A `for` or `while` body may run no times, and each iteration is a path of its own: an assignment
  in a body does not assign the local after the loop, and an assignment in one iteration does not
  count in the next. A `loop` body runs at least once, and what follows the loop is reached only
  through a `break`.
- A `switch` runs exactly one case, so the local is assigned after it when it is assigned in every
  case.

Declaring a local does not create a value, so a struct's field initializers do not run for a local
declared without an initializer. A fixed array local whose element type has a default value is the
exception: it holds its default value ([Default values](04-types.md#types.defaults)).

**Example:**

```java
void report(bool found) {
    int count;
    if (found) {
        count = 1;
    }
    print(count);                   // error[WYL0394]
    int total;
    if (found) {
        total = 1;
    } else {
        total = 2;
    }
    print(total);
}
```

<a id="flow.assign-once"></a>
## 16.2 Single assignment

A local variable declared without an initializer and not `mut` is **assigned once**: it receives a
value at most once.

- It is a compile-time error to assign such a local where it might already have been assigned: a
  second time on one path, after a branch or loop that may have assigned it, or in a loop whose
  earlier iteration may have assigned it.
- A local declared with an initializer is assigned by its declaration. It is a compile-time error
  to assign it again unless it is `mut` ([Mutability](06-variables.md#variables.mutability)).
- A `mut` local may be assigned any number of times. It holds a value from its first assignment
  ([Definite assignment](#flow.definite-assignment)).

**Example:**

```java
void report(bool found) {
    int count;
    count = 1;
    count = 2;                      // error[WYL0395]

    int other;
    if (found) {
        other = 1;
    }
    other = 2;                      // error[WYL0395]

    int each;
    while (found) {
        each = 1;                   // error[WYL0395]
    }
}
```

<a id="flow.narrowing"></a>
## 16.3 Narrowing

A local variable or by-value parameter of a nullable type is **narrowed** where the order of the
program shows that it is not null. Where it is narrowed, it is used as its non-null type: members
are reached with `.`, it is passed where the non-null type is expected, and operators accept it.

- Narrowing applies to a local variable and a by-value parameter. A `ref` or `in` variable or
  parameter is not narrowed: another reference to the same variable can set it to null after the
  check. A field, an element, and any other expression are not narrowed; a check of one holds only
  in the check. A `ref?` or `in?` is narrowed as its own rules give
  ([Nullable references](17-memory.md#memory.nullable-references)).
- A null test narrows where its result shows that the local is not null: in the branch where
  `x != null` is true or `x == null` is false, and through `&&`, `||`, `!`, and `?:` where the
  condition's value implies the test. An `if` whose other branch cannot complete narrows after it:
  `if (x == null) { return; }`.
- Assigning a non-null value narrows the local until it is assigned a value that may be null.
- A loop's condition narrows in the body. A local narrowed before a loop is narrowed in the body
  only until the loop assigns it, and the condition narrows again each time it is tested.
- A `ref` argument ends the narrowing of a local, since the callee can assign it null.
- A lambda that may assign the local ends its narrowing from the point the lambda is made, and a
  null check outside a lambda does not narrow inside it ([Lambdas](#flow.lambdas)).
- A test `x is Type name` narrows `x` where it succeeded, and binds `name` to a non-null value of
  the type ([Bindings](15-patterns.md#patterns.bindings)).
- It is a compile-time error to use a local as its non-null type where narrowing does not show that
  it is not null.
- It is a compile-time error for a `case null` to test a local that narrowing shows is not null
  there ([`case null`](15-patterns.md#patterns.cases.null)).

A narrowed local that is `mut` is a variable: its fields and elements can be assigned and its `mut`
methods called, which changes the local. While the local can be assigned null, nothing may keep
referring into its value ([References](17-memory.md#memory.references)); a local that is not `mut`
never changes, so a reference into its value may be kept.

**Example:**

```java
class Node { pub int value; }

void show(Node? node, int? count) {
    print(node.value);              // error[WYL0397]
    if (node != null) {
        print(node.value);
    }
    if (count != null) {
        print(count + 1);
    }
    if (node == null) {
        return;
    }
    print(node.value);
}
```

<a id="flow.moves"></a>
## 16.4 Use after move

A value of a unique type is moved where a copyable value would be copied
([Moves](17-memory.md#memory.moves)). After the move, the local or by-value parameter it was moved
from holds no value.

- It is a compile-time error to use a local or by-value parameter on a path where its value may
  have been moved: to read it, to call a method on it, to use a field or element of it, to take a
  reference to it, to pass it, or to call a lambda that uses it.
- An assignment gives it a value again, and it may be used after that.
- It is a compile-time error if a loop moves a local declared outside it and does not assign it again
  before the next iteration.
- Only a whole local or by-value parameter is moved. A field, an element, and what a reference
  refers to are not moved out of ([Moves](17-memory.md#memory.moves)).
- A moving binding moves the matched local or parameter on the path where its case is chosen or its
  `is` test is true ([Moving bindings](15-patterns.md#patterns.bindings.moves)).
- A moved variable holds no value, which is not the same as null. It is a compile-time error to test
  it against null.
- A moved variable is not released ([Moves](17-memory.md#memory.moves)).

**Example:**

```java
unique struct Buffer {
    pub int length;
}

void main() {
    mut Buffer data = Buffer(4);
    Buffer kept = data;             // 'data' is moved into 'kept'
    print(data.length);             // error[WYL0440]: 'data' is used after being moved
    data = Buffer(8);
    print(data.length);
}
```

<a id="flow.constructors"></a>
## 16.5 Fields in constructors

A field **needs a value** if it has no initializer and its type has no initial value
([Order of construction](08-classes.md#classes.construction)). In a constructor, before every such
field is assigned:

- It is a compile-time error to return while a field still needs a value.
- It is a compile-time error to read a field that might not have been assigned.
- It is a compile-time error to use `this` for anything but assigning a field or reading a field
  that is assigned: no method is called on it, and it is not passed.

An assignment on every path before the constructor returns assigns the field. After `this(...)`
returns, every field is assigned, since the constructor it calls had to assign them; the called
constructor is checked in the same way. A struct constructor that assigns `this` as a whole assigns
every field ([Constructors](07-structs.md#structs.constructors)).

A lambda made in a class constructor may read the fields that are assigned where it is made.

**Example:**

```java
class Node { }

class Link {
    Node next;

    Link(bool found) {
        print(next == null);        // error[WYL0399]
        use(this);                  // error[WYL0400]
        if (found) {
            next = new Node();
            return;
        }
    }                               // error[WYL0398]
}

void use(Link link) { }
```

<a id="flow.returns"></a>
## 16.6 Returns and reachability

A function, method, or lambda whose return type is not `void` returns a value on every path through
its body.

- It is a compile-time error if a path reaches the end of the body without returning a value, where
  the body's type is not `void`.
- A `return` without a value ends a function or lambda that returns `void`, and a constructor. A
  struct constructor's `return;` returns the value that `this` holds
  ([Constructors](07-structs.md#structs.constructors)).
- A path that ends in `panic(...)`, or in a call that never returns, returns no value, so the rule
  above does not apply to it ([Panics](18-errors.md#errors.panics)).

A statement that can never run is allowed and does nothing. A statement can never run after a
`return`, `break`, `continue`, or `panic(...)`, or where every path above it ends in one of those.

**Example:**

```java
int pick(bool found) {
    if (found) {
        return 1;
    }
}                                   // error[WYL0401]

void stop() {
    panic("stop");
    print("unreachable");           // allowed, and does nothing
}
```

<a id="flow.lambdas"></a>
## 16.7 Lambdas

A lambda's body is checked like a function body, with its parameters and the names it captures
assigned ([Definite assignment](#flow.definite-assignment),
[Returns and reachability](#flow.returns)).

- It is a compile-time error to use a local in a lambda where the local might not have been
  assigned where the lambda is made. The lambda may run after the point where it is made, so an
  assignment inside it does not assign the local for the code after it.
- A null check outside a lambda does not narrow a local inside it. Check the local inside the
  lambda, or copy it to a local first.
- A local that a lambda may assign is not narrowed after the lambda is made, since the lambda may
  run during a later call. A null check before the lambda is made still narrows until then.
- An escaping lambda copies what it captures, and a non-escaping lambda refers to it in place
  ([Captures](12-functions.md#functions.lambdas.captures)). A move in either form follows
  [Use after move](#flow.moves).

**Example:**

```java
void call(fn() -> void f) { f(); }

void run() {
    int count;
    call(() -> print(count));       // error[WYL0394]
    int? maybe = 5;
    if (maybe != null) {
        call(() -> print(maybe + 1));   // error[WYL0397]
    }
    mut int? later = 5;
    call(() -> { later = null; });
    if (later != null) {
        print(later + 1);           // error[WYL0397]
    }
}
```
