<a id="variables"></a>
# 6. Variables and constants

A variable holds one value ([Terms](01-introduction.md#introduction.terms)). This chapter gives how a
local variable is declared and named, which variables can change, what a `ref` or `in` variable is, and
how a program keeps a value for its whole run.

<a id="variables.locals"></a>
## 6.1 Local variables

A **local variable** is declared in a block by a declaration statement:

```ebnf
LocalDeclaration = { Annotation } [ "mut" | ReferenceMode ] [ "escaping" ]
                   ( "var" | Type ) Identifier [ "=" Expression ] ";" .
ReferenceMode    = ( "ref" | "in" ) [ "?" ] .
```

A local is in scope from its declaration to the end of the block that holds it. Parameters, loop
variables, pattern bindings, and the parameters of a lambda are variables with the same rules of
naming. The variable has the declared type; with `var`, the type of its initializer
([Inferred types](#variables.locals.var)). A local declared without an initializer is assigned
before it is read ([Definite assignment](16-flow.md#flow.definite-assignment)).

- It is a compile-time error if a local variable, parameter, loop variable, pattern binding, or lambda
  parameter has the name of another one that is in scope in the same function, including the
  functions that enclose a lambda. A variable that is out of scope can be declared again, and a
  field or a function of the same name is not in conflict.

**Example:**

```java
void f(int n) {
    int n = 2;                  // error[WYL0084]
    { int a = 1; }
    int a = 2;                  // valid: the first 'a' is out of scope
}
```

<a id="variables.locals.var"></a>
### 6.1.1 Inferred types

`var` in place of a type declares a local whose type is the type of its initializer. A function value
keeps the form of its initializer ([Escaping and non-escaping](12-functions.md#functions.lambdas.escaping)).

- It is a compile-time error if a `var` declaration has no initializer.
- It is a compile-time error if the initializer of a `var` declaration is a call that returns `void`.
- It is a compile-time error if the initializer of a `var` declaration is `null`: no type can be
  taken from it.
- It is a compile-time error if the initializer of a `var` declaration is a call that returns `ref?`
  or `in?`, as `list.get(i)` does: a reference that may refer to nothing is not a value to store.
  `ref? var` declares a reference to it instead ([Nullable references](17-memory.md#memory.nullable-references)).

**Example:**

```java
struct Row {
    float[4] cells;

    mut ref? float at(int i) { ... }
}

void main() {
    var orders = new List<Order>();
    var total = 0;                  // an int
    var v;                          // error[WYL0085]
    var w = log("x");               // error[WYL0086], 'log' returns void
    var x = null;                   // error[WYL0087]
    mut Row row = Row();
    var copy = row.at(0);           // error[WYL0088]
}
```

<a id="variables.mutability"></a>
## 6.2 Mutability

A variable, parameter, loop variable, or lambda parameter is **mutable** if it is declared `mut`, and
is otherwise **immutable**. `mut` comes after any annotations and modifiers and before the rest of the
declaration: `mut int count;`, `pub mut int total;`, `void aim(mut Point dir)`,
`for (mut int i = 0; i < n; i++)`, `(mut x) -> ...`. It is not a part of a type or of a function's
signature.

A place is **mutable** or **read-only**:

- The place of a mutable variable is mutable, and the place of an immutable one is read-only. The
  place of a `ref` variable or parameter is mutable, and that of an `in` variable or parameter is
  read-only ([`ref` and `in` variables](#variables.references)).
- **A value follows its variable.** A struct, enum, or fixed array is stored in its variable, so its
  fields and elements are mutable exactly when the place holding it is. Nothing in an immutable
  one can change: its fields and elements cannot be assigned, it cannot be passed by `ref` or bound
  to a `ref` variable, its `mut` methods cannot be called, and a fixed array in it converts only to
  `ReadOnlySpan`. These are the rules of an `in` parameter
  ([Parameter modes](12-functions.md#functions.parameters.modes)).
- **An object does not follow the variable that refers to it.** A variable that is not `mut` always
  holds the same reference, and the object it refers to changes in the ways its own fields allow:
  `list.add(1)` is valid for an immutable `list`, and `list = other` is not. A field of an object
  is mutable if it is declared `mut`, or in a constructor of its class
  ([Fields](08-classes.md#classes.fields)); a struct stored in a field follows the field.
- A temporary is read-only ([Temporaries](17-memory.md#memory.release.temporaries)).

Where a rule needs a mutable place, it says so: assignment ([Assignment](14-statements.md#statements.assignment)),
a `ref` argument or variable ([Parameter modes](12-functions.md#functions.parameters.modes),
[References](17-memory.md#memory.references)), and a call of a `mut` method
([`mut` methods](07-structs.md#structs.methods.mut)).

`mut` is not allowed on a `ref` or `in` variable ([`ref` and `in` variables](#variables.references)),
and not on a struct field, a variant field, a pattern binding, or a non-escaping function variable or
parameter ([Escaping and non-escaping](12-functions.md#functions.lambdas.escaping)). A `ref struct` variable may be `mut`; it still cannot be assigned again
([`ref struct`s](07-structs.md#structs.ref)). A pattern binding is a copy: it is copied to a `mut`
variable to be changed.

**Example:**

```java
struct Counter { int n; mut void bump() { n++; } }

void main() {
    Counter c = Counter();
    c.n = 1;                    // error[WYL0351]: 'c' is not 'mut'
    mut Counter d = Counter();
    d.bump();
    List<int> list = new List<int>();
    list.add(1);                // valid: the object changes, the variable does not
}
```

<a id="variables.assign-once"></a>
## 6.3 Variables assigned once

A local variable that is declared without an initializer and is not `mut` is **assigned once**: it
receives its value in a later assignment, and only one. Which assignments are valid is a rule of flow
analysis ([Variables assigned once](16-flow.md#flow.assign-once)).

**Example:**

```java statements
int count = 1;
String label;
if (count > 0) {
    label = "some";
} else {
    label = "none";
}
```

<a id="variables.references"></a>
## 6.4 `ref` and `in` variables

A `ref` or `in` variable, written `ref T name = place;` or `in T name = place;`, refers to the place
it is initialized with, for its whole scope; a `ref?` or `in?` variable may instead refer to nothing
([Nullable references](17-memory.md#memory.nullable-references)). Assigning to a `ref` variable assigns to the
place; a `ref` variable gives a place that is mutable, an `in` variable one that is read-only. A
reference is not a value that is stored: what it may refer to, where it may be, and how long what it
refers to stays valid are the rules of [References](17-memory.md#memory.references).

- It is a compile-time error if a `ref` or `in` variable has no initializer.
- It is a compile-time error if a `ref` or `in` variable is declared `mut`: it refers to one place
  for its whole scope.
- It is a compile-time error if a `ref` or `in` variable is declared `escaping`: it refers to a place
  rather than holding a function.
- It is a compile-time error if a `ref?` or `in?` variable is declared with `var` and initialized with
  `null`: no type can be taken from it.

**Example:**

```java statements
ref int first = values[0];
first = 5;                          // assigns values[0]
ref int r;                          // error[WYL0091]
mut ref int s = first;              // error[WYL0089]
ref escaping int e = first;         // error[WYL0090]
ref? var q = null;                  // error[WYL0092]
```

<a id="variables.constants"></a>
## 6.5 Constants

The language has no declaration of a constant. `const` is a reserved word
([Keywords](02-lexical.md#lexical.keywords)). A value fixed for the whole run of a program is a
[static](#variables.statics).

<a id="variables.statics"></a>
## 6.6 Statics

A **static** is a field declared `static` in a type.

- It is a compile-time error if a variable is declared at the top level of a file.

**Every static is read-only, and its type is sendable and copyable** ([Sendable
types](19-concurrency.md#concurrency.sendable), [Copyable types](04-types.md#types.copyable)). There
is no mutable static. The object a static refers to can change through its own operations, as an
`Atomic<int>`'s value does; any other value that changes is held in an object that the program passes
to what uses it.

- It is a compile-time error if the type of a static is not sendable or not copyable.
- It is a compile-time error if a generic type declares a static field. Its static methods are
  allowed.

**Initialization.** Every static is initialized before `main` runs, on the main thread, exactly once.

- The statics of the program's package, and of every package it depends on, directly or not,
  `std` included, are initialized. A package's statics are initialized after those of every package
  it depends on; packages neither depends on are taken in order of name. Within a package, files are
  taken in order of their path under `src/`, and within a file, statics in the order they are
  written.
- An initializer that reads a static that is not yet initialized initializes that one first.
- Reading a static whose initializer is still running, directly or through the functions it calls,
  panics.
- Starting a thread before `main` begins panics.
- A panic in an initializer ends the program before `main` begins ([Panics](19-concurrency.md#concurrency.panics)).

A static is never destroyed, whether the program ends by `main` returning or by a panic
([Running a program](03-programs.md#programs.execution)).

**Example:**

```java unchecked
class Audio {
    static int SAMPLE_RATE = 48_000;
    static Frozen<float> SINE_TABLE = makeSineTable();      // computed once, before 'main'
}

int counter = 0;                // error[WYL0093]
```
