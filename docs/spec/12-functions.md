<a id="functions"></a>
# 12. Functions and methods

A function is a named piece of code that takes parameters and returns a value. A method is a
function declared in a type. This chapter gives how functions and methods are declared, how each
parameter passes its value, how a call selects one of several of the same name, how a method returns
a place, the two other call forms (extension methods and indexers), and function values: lambdas,
function references, and the choice between functions.

<a id="functions.declarations"></a>
## 12.1 Function declarations

```ebnf
FunctionDeclaration = { Annotation } [ Visibility ] [ "unsafe" ] Type Identifier
                      [ TypeParameters ] "(" [ Parameters ] ")" ( Block | ";" ) .
```

A function declared at the top level of a file is a **function**; one declared in a struct, class,
enum, or interface is a **method** ([Struct declarations](07-structs.md#structs.declarations),
[Class declarations](08-classes.md#classes.declarations)). `main` is an ordinary function with a
fixed signature ([The entry point](03-programs.md#programs.entry)).

- It is a compile-time error if a function is declared with a modifier other than `unsafe`:
  `static`, `virtual`, `override`, `abstract`, `final`, `mut`, or `ref` is not a function modifier.
- It is a compile-time error if a function outside an extern block has no body.
- A function is generic when it has type parameters ([Type parameters](11-generics.md#generics.parameters)).
- The functions of one name that a file sees are overloads of one function
  ([A module's names](03-programs.md#programs.names), [Overloading](#functions.overloading)).
- A function in an extern block is declared without a body, and C++ provides it
  ([Extern blocks](21-interop.md#interop.extern)).

**Example:**

```java
int distance(Point a, Point b) {
    int dx = a.x - b.x;
    int dy = a.y - b.y;
    return dx * dx + dy * dy;
}

static int count() { return 0; }        // error[WYL0209]

int pending(int x);                     // error[WYL0211]
```

<a id="functions.parameters"></a>
## 12.2 Parameters

```ebnf
Parameters    = ( Parameter | ReceiverParameter ) { "," Parameter } .
Parameter     = { Annotation } [ "mut" | ParameterMode ] [ "escaping" ] Type Identifier
                [ "=" Expression ] .
```

`ParameterMode` is given in [Function types](04-types.md#types.functions).

The parameter passed by value has the type written. A `mut` parameter is a by-value parameter whose
variable is mutable ([Mutability](06-variables.md#variables.mutability)). A `ref` or `in` parameter
is a reference to a place ([References](17-memory.md#memory.references)). A parameter declared
`escaping` holds a function value in the escaping form
([Escaping and non-escaping](#functions.lambdas.escaping)).

<a id="functions.parameters.modes"></a>
### 12.2.1 Parameter modes

| Mode | Passed | Argument |
|---|---|---|
| `T x` | by value | a value: a struct is copied, a unique struct is moved ([Copies](07-structs.md#structs.copies)) |
| `mut T x` | by value, into a mutable variable | a value |
| `ref T x` | a mutable reference to a place | a mutable place, written `ref place` |
| `in T x` | a read-only reference to a place | a place or a value, written without a keyword |

```java
void move(in Point by, ref Point position) {
    position = Point(position.x + by.x, position.y + by.y);
}

void main() {
    mut Point p = Point(1, 2);
    move(Point(1, 0), ref p);
}
```

- It is a compile-time error if a parameter is declared `mut` together with `ref` or `in`.
- It is a compile-time error if a `ref` or `in` parameter has a function type.
- A `ref` or `in` parameter of a class or interface type refers to the variable that holds the class
  reference: a `ref` parameter can assign the caller's variable, and an `in` parameter sees what that
  variable holds. A by-value parameter has a class reference of its own.
- It is a compile-time error if a `ref` argument is not a mutable place: it is a `mut` local or
  parameter, a field path rooted at one, an element, or a place a method returned. A place is
  read-only, and cannot be passed by `ref`, when it is rooted at an `in` parameter or variable, at a
  variable, parameter, or field that is not `mut`, or at `this` in a struct method that is not
  `mut`.
- It is a compile-time error if a `ref` argument does not have exactly the type of its parameter: a
  `ref` argument is not converted.
- The arguments of a call are evaluated left to right, and a `ref` argument is the place, not a
  snapshot of its value ([Evaluation order](13-expressions.md#expressions.order)).

`ref?` and `in?` parameters are in [Nullable references](17-memory.md#memory.nullable-references).

**Example:**

```java
void swap(ref int a, ref int b) { int t = a; a = b; b = t; }

void main() {
    mut int x = 1;
    swap(ref x, ref 2);                 // error[WYL0222]
    swap(ref x, 2);                     // error[WYL0224]
}
```

<a id="functions.arguments"></a>
## 12.3 Default values and named arguments

A parameter may declare a **default value**, which a call uses when it gives no argument for the
parameter. The arguments of a call may be given by the parameter's name.

```java
void scale(Point p, float by = 2, float limit = 100) { ... }

void main() {
    var p = Point();
    scale(p);
    scale(p, 3);
    scale(p, limit: 10);                // 'by' is 2
}
```

- It is a compile-time error if a default value is not a constant
  ([Constant expressions](13-expressions.md#expressions.constants)) or a struct constructor given
  constants.
- It is a compile-time error if a `ref` parameter has a default value. A `ref` argument must be a
  variable.
- It is a compile-time error if a parameter whose type involves a type parameter has a default value
  other than `null`.
- It is a compile-time error if a struct constructor gives every parameter a default value: `T()` is
  the struct's default value ([The default value](07-structs.md#structs.constructors.default)).
- It is a compile-time error if a default value depends on the parameter it belongs to.
- Positional arguments come before named ones. Named arguments are written in the order of the
  parameters, and may skip a parameter that has a default value.
- It is a compile-time error if a parameter is given more than one argument, if a positional
  argument follows a named one, if a named argument's name is not a parameter of the function, or if
  an argument is named in a different order than the parameters.
- A call passes, for each parameter, its argument or its default value, in the order the parameters
  are declared.
- A function type has no parameter names and no default values, so an argument to a function value
  is positional and every parameter takes one
  ([Function types](04-types.md#types.functions)).
- It is a compile-time error if a method that overrides or implements another declares default
  values: it takes the other method's defaults
  ([Virtual methods](08-classes.md#classes.overriding),
  [Implementing an interface](10-interfaces.md#interfaces.implementing)).
- It is a compile-time error if two methods that a class inherits give a different default value
  for the same parameter.

**Example:**

```java
class Customer { }

struct Order {
    pub int id;
    pub int quantity = 1;
    pub Customer customer;
}

void main() {
    Order(id: 7);                       // error[WYL0100]
    Order(id: 7, 3);                    // error[WYL0226]
    Order(7, id: 8);                    // error[WYL0229]
}
```

<a id="functions.overloading"></a>
## 12.4 Overloading

Functions and methods of one name may be **overloaded**: each has its own parameter types. A call
selects one of them by its arguments.

- It is a compile-time error if two functions or methods of one name have the same parameter types.
  Their return types do not distinguish them. Two generic functions whose parameter types differ
  only in the names of their type parameters have the same parameter types.
- Two top-level functions with the same parameter types may be overloads if the bounds of their type
  parameters differ ([Bounds](11-generics.md#generics.bounds)). Two methods may not.
- It is a compile-time error if two methods differ only in the nullability of a parameter whose type
  is a class, interface, or type parameter.
- It is a compile-time error if two methods differ only in whether a parameter is `ref` or `ref?`,
  or only in which parameters are `escaping`.
- A method that overrides or implements another is not an additional overload of it
  ([Virtual methods](08-classes.md#classes.overriding)).
- A by-value receiver is not one of a method's parameter types
  ([By-value receivers](07-structs.md#structs.methods.by-value)).

**Example:**

```java
void add(int x) { }
void add(long x) { }
void add(int x) { }                     // error[WYL0232]
```

<a id="functions.overloading.resolution"></a>
### 12.4.1 Overload resolution

A call is matched to an overload in these steps:

1. Only the overloads visible where the call is written are considered: one that is not visible never
   wins over one that is.
2. The overloads that can take the arguments by their number, by their names, and by the number of
   type arguments written are kept.
3. Each remaining overload's parameters are checked against the arguments. An argument converts
   implicitly to a parameter's type ([Conversions](05-conversions.md#conversions)), and a `ref`
   argument has exactly the parameter's type.
4. Each remaining overload whose type arguments, written or inferred, do not satisfy the bounds of
   its type parameters is not kept ([Bounds](11-generics.md#generics.bounds)).

- Among overloads that are equally good, one that leaves fewer parameters to their default values is
  chosen, and then a non-generic one over a generic one
  ([Inferring type arguments](11-generics.md#generics.arguments.inference)).
- It is a compile-time error if no overload takes the number of arguments or type arguments
  written, takes the names written, or accepts the arguments' types.
- It is a compile-time error if more than one overload remains equally good. Narrower bounds do not
  make an overload better.
- A lambda among the arguments narrows the candidates: an overload whose parameter takes another
  number of arguments is ruled out, and an overload that is left only after the other arguments are
  checked gives the lambda its parameter types
  ([Typing lambdas](#functions.lambdas.typing)).

**Example:**

```java
void show(int x) { }
void show(String s) { }

void main() {
    show(1);                            // show(int)
    show(true);                         // error[WYL0236]
    show();                             // error[WYL0233]
}
```

<a id="functions.methods"></a>
## 12.5 Instance and static methods

```ebnf
Method     = { Annotation } [ Visibility ] { Modifier } ReturnType Identifier [ TypeParameters ]
             "(" [ Parameters ] ")" [ WhereClause ] ( Block | ";" ) .
Modifier   = "static" | "mut" | "virtual" | "override" | "abstract" | "final" | "unsafe" .
ReturnType = [ ParameterMode ] Type .
```

Which modifiers a method may have is a rule of the type that declares it
([Struct declarations](07-structs.md#structs.declarations), [Virtual methods](08-classes.md#classes.overriding),
[Interfaces](10-interfaces.md#interfaces.declarations), [Enum methods](09-enums.md#enums.methods)).

A method declared in a type is an **instance method** unless it is declared `static`. An instance
method has a receiver, `this`, which is the value or place it is called on
([Methods](07-structs.md#structs.methods), [Class declarations](08-classes.md#classes.declarations)),
or a parameter that takes it by value ([By-value receivers](07-structs.md#structs.methods.by-value)).
A static method has no `this`, and is called through its type: `Point.origin()`.

- It is a compile-time error if a static method is declared `virtual`, `override`, `abstract`,
  `mut`, or `final`.
- It is a compile-time error to call an instance method through its type, or a static method
  through a value ([Calls](13-expressions.md#expressions.calls)).

**Example:**

```java
struct Point {
    pub int x;

    pub static Point origin() { return Point(0); }
    pub int twice() { return x * 2; }
}

void main() {
    Point.origin();
    Point(3).twice();
}
```

<a id="functions.ref-returns"></a>
## 12.6 Returning references

A method may return a `ref T` or an `in T` ([References](17-memory.md#memory.references)), written
`return ref place;`. The returned reference refers into the method's receiver, or into an object
reached through the receiver. A reference into the receiver stays valid as long as the receiver
does. A reference into an object reached through the receiver holds or borrows that object, so it
stays valid as long as the caller uses it.

```java
struct Buffer {
    int[16] data;

    pub mut ref int at(int index) { return ref data[index]; }
    pub in int peek(int index) { return ref data[index]; }
}

class Node {
    pub mut int value;
}

struct Cursor {
    Node node;

    pub mut ref int current() { return ref node.value; }    // holds the node
}
```

- It is a compile-time error to declare a function, a static method, or a constructor with a
  `ref` or `in` return type: only an instance method returns a reference.
- It is a compile-time error if the return type of a method is `ref void` or `in void`.
- It is a compile-time error if a method that returns `ref` or `in` uses `return` without `ref`.
  A method that returns `ref?` or `in?` may also use `return null;`.
- It is a compile-time error if `return ref` is used in a method that does not return `ref` or `in`.
- A place is **in the receiver** if it is `this`, a field or element of `this`, a place a method of
  `this` returned, or, in `unsafe` code, the target of a pointer.
- A place is **reached through the receiver** if it is a field or element of a class object that
  the receiver refers to, directly or through other objects, or a place that a method of such an
  object returned.
- It is a compile-time error if the place returned is neither in the receiver nor reached through
  it. A place in a local or a parameter is neither.
- A returned reference to a place reached through the receiver holds and borrows what the same
  reference would hold and borrow if the caller had taken it: the class object that the place is
  in, or the container whose method returned the place ([Holds](17-memory.md#memory.holds),
  [Borrows](17-memory.md#memory.borrows)). The hold or borrow lasts for as long as the caller uses
  the reference: for the scope of the variable that binds it, or until the statement ends.
- A method that returns `ref` or `in` may be `virtual` or `abstract`, and an interface may declare
  one. An override or implementation returns a place in its own receiver or reached through it.
- It is a compile-time error if a struct method that returns `ref` is not `mut`: a `ref` return
  refers to mutable storage. A class method is never `mut`, and its `mut` fields are mutable
  ([`mut` methods](07-structs.md#structs.methods.mut)).
- An instance method returns only a view of its receiver: of its fields or elements, or a span its
  methods return ([`ref struct`s](07-structs.md#structs.ref)).
- The result is a place for as long as the receiver is, or, if the place is reached through the
  receiver, for as long as the caller uses it. An `in` result of a call on a temporary may
  be read but not referred to
  ([`mut` methods](07-structs.md#structs.methods.mut)).

**Example:**

```java
struct Buffer {
    int[16] data;

    pub static ref int first() { ... }                      // error[WYL0244]
    pub mut ref int at(int index) { return data[index]; }   // error[WYL0240]
    mut ref int other(ref Buffer o) { return ref o.data[0]; }   // error[WYL0241]
}
```

<a id="functions.extensions"></a>
## 12.7 Extension methods

A function whose first parameter is marked `this` is an **extension method**. It is called like a
method of the receiver's type, and may also be called as a function. A method marks its first
parameter the same way to take its receiver by value
([By-value receivers](07-structs.md#structs.methods.by-value)).

```ebnf
ReceiverParameter = "this" [ "ref" | "in" ] Type Identifier .
```

```java unchecked
bool any<T>(this ReadOnlySpan<T> items, fn(in T) -> bool test) {
    for (in T item : items) {
        if (test(item)) {
            return true;
        }
    }
    return false;
}

if (orders.any(o -> o.unpaid)) { ... }
```

- The receiver takes a parameter mode: `this T` passes by value, as any by-value parameter does,
  `this in T` passes a read-only reference, and `this ref T` passes a mutable place. A `this ref`
  receiver requires a mutable place, as a `mut` struct method does, but the call writes no `ref`.
- It is a compile-time error to declare `this ref` or `this in` on a function type.
- A member of the receiver's type wins over an extension of the same name: the extensions of a type
  are considered only when it has no member of the name that is visible where the call is
  ([Visibility](03-programs.md#programs.visibility)). A member that the call cannot see does not
  hide an extension. Among extensions, overload resolution chooses as for functions
  ([Overload resolution](#functions.overloading.resolution)).
- The receiver converts to the `this` parameter's type by identity, by an upcast, and by the span
  conversions ([Span conversions](05-conversions.md#conversions.spans)). No other conversion
  applies, and type arguments are inferred from the receiver as from any argument.
- A nullable receiver needs `?.` or a narrowing, unless the receiver parameter's type is itself
  nullable.
- A `this ref` receiver parameter of a non-nullable type does not take a nullable variable, even
  where a narrowing applies, because nothing refers into a nullable value
  ([References](17-memory.md#memory.references)).
- An extension is an ordinary function: it sees the receiver's members that its file sees, it is
  called without dispatch, and it can be called as `any(orders, test)`.
- An extension is found where it is visible and its name is imported, or in its own module.
- A call `x.name(...)` takes its candidates from the first place that has an extension named
  `name` that accepts the receiver. The places are searched in the order in which a file looks up a
  name ([Imports](03-programs.md#programs.imports)). An extension accepts the receiver if the
  receiver converts to the extension's `this` parameter, and the type arguments that the receiver
  alone determines satisfy their bounds. A place whose functions named `name` include no such extension does
  not hide the places after it.
- A call written as a function call, `name(x, ...)`, finds `name` as any other use of a name does.

**Example:**

```java unchecked
// orders/count.wyl
import std::iteration::*;

int count(Order o) { ... }       // the module's own 'count', which has no 'this' parameter

int total(List<Order> orders) {
    return orders.iterator().count();   // calls std::iteration's 'count'
}
```

<a id="functions.indexers"></a>
## 12.8 Indexers

There is no operator overloading: an operator always means what it means for the built-in types.
The one exception is indexing. A struct or class declares an **indexer**, an instance method named
`operator []`, whose result `a[i]` is:

```ebnf
Indexer = { Annotation } [ Visibility ] { Modifier } ReturnType
          "operator" "[" "]" [ "=" ] "(" [ Parameters ] ")" ( Block | ";" ) .
```

```java
struct Mat4 {
    float[16] m;

    pub mut ref float operator [](int row, int col) { return ref m[row * 4 + col]; }
}
```

- An indexer may return a value, a `ref`, or an `in`, and take any number of parameters.
- It is a compile-time error to declare any operator other than `operator []` and `operator []=`,
  or to declare an indexer with any other spelling.
- A call `a[i, j]` evaluates the receiver and then each index, left to right, and calls the
  indexer ([Evaluation order](13-expressions.md#expressions.order)).
- The bounds checks of the built-in indexers are in [Indexing](13-expressions.md#expressions.indexing).

<a id="functions.indexers.setters"></a>
### 12.8.1 `operator []=`

Where an element has no storage to refer to, such as an element packed into part of a byte, a type
declares `operator []=` to assign it. It takes the indices and then the assigned value, and returns
`void`.

```java
struct Nibbles {
    byte[2048] bytes;

    pub byte operator [](int i) { return bytes[i >> 1] >> ((i & 1) * 4) & 0xF; }
    pub mut void operator []=(int i, byte value) { ... }
}
```

- Each `operator []=` pairs with the `operator []` whose index parameters have the same types and
  modes, which returns a value or an `in`, and whose element type is its value parameter's type.
- It is a compile-time error if `operator []=` returns anything but `void`, is `static`, is given
  default values, or takes a value parameter that is `ref`.
- It is a compile-time error if an `operator []=` has no matching `operator []`, or if its value
  parameter's type is not the element type of that `operator []`.
- It is a compile-time error if a type declares both `operator []=` and an `operator []` that
  returns `ref`, inherited or not: an assignment would have two meanings.
- An assignment resolves its indices against the `operator []` overloads as a call does, then calls
  the paired `operator []=`. The value converts to the value parameter as an argument does.
- The receiver, then each index, is evaluated once, before the value. A compound assignment reads
  the element with `operator []` after the indices and before the value. A by-value or `in` index
  is evaluated into a temporary that both calls take.
- An element assigned through `operator []=` is not a place: `ref grid[i]`, passing `grid[i]` by
  `ref`, and returning it with `return ref` are compile-time errors.
- `operator []=` is an instance method, may be `virtual` or declared in an interface, and may be
  less visible than its `operator []`. On a struct it needs `mut` to change the struct, and then a
  mutable place, not a temporary.

<a id="functions.lambdas"></a>
## 12.9 Lambdas

A **lambda** is a function value written where it is used. Its parameters are either all untyped,
taking their types and modes from the function type it is converted to, or all typed:

```ebnf
Lambda          = ( Identifier | "(" [ LambdaParameter { "," LambdaParameter } ] ")" ) "->"
                  ( Expression | Block ) .
LambdaParameter = { Annotation } [ "mut" | ParameterMode ] [ "escaping" ] [ Type ] Identifier .
```

A lambda is never an operand: its body extends as far as an expression can, so `x -> x + 1` is a
lambda returning `x + 1`. `return` in a lambda returns from the lambda; `break` and `continue`
cannot leave it.

- It is a compile-time error if some of a lambda's parameters have types and others do not.
- It is a compile-time error if an untyped lambda parameter is declared `escaping`: it takes the
  form from the lambda's function type.

**Example:**

```java statements
fn(int, int) -> int add = (a, b) -> a + b;
fn(int) -> int twice = x -> x * 2;
```

<a id="functions.lambdas.typing"></a>
### 12.9.1 Typing lambdas

A lambda has no type of its own: it takes one from where it is used, which is a declared variable,
field, parameter, return type, or argument of a call.

- It is a compile-time error if a lambda is used where no function type is expected, as in
  `var f = x -> x;`.
- It is a compile-time error if a lambda's number of parameters differs from the function type's,
  or a typed parameter's type or mode differs from it.
- Passed to an overloaded function, a lambda rules out the overloads whose parameter takes another
  number of arguments or other types. If more than one overload remains, the call is a compile-time
  error.
- A generic function's type arguments are inferred first from its other arguments and from the
  types its lambdas declare. Those give each lambda its parameter types, and a type argument still
  unknown comes from what the lambdas return, each returned value counting like an argument
  ([Inferring type arguments](11-generics.md#generics.arguments.inference)).
- Lambdas are typed in order, each once its parameter types are known, so one lambda's result can
  type the next one's parameters. Where lambdas disagree, the type that all of them convert to is
  inferred.
- It is a compile-time error if a type argument that a lambda's parameters were bound with is later
  given a different type by another lambda's result.
- Inferring from a lambda's body needs the parameter types of the overload being called, so it
  happens only when one overload is left after the other arguments are checked. A lambda returning
  nothing infers `Unit` for a type parameter that is its whole result type, and nothing otherwise
  ([Inferring type arguments](11-generics.md#generics.arguments.inference)).
- A lambda whose function type returns `Unit` may leave out its return: an expression body with no
  value is a statement, and `return;` and the end of the body return `Unit()`
  ([`return`](14-statements.md#statements.return)).
- It is a compile-time error to use `try` in a lambda whose return type is being inferred.
- A lambda can use a local only if it is definitely assigned where the lambda is created. A null
  check outside a lambda does not narrow inside it. A local that any lambda assigns is never
  narrowed ([Narrowing](16-flow.md#flow.narrowing), [Lambdas](16-flow.md#flow.lambdas)).
- A function type's parameters have no names, so an argument to a function value is positional and
  every parameter takes one.

<a id="functions.lambdas.escaping"></a>
### 12.9.2 Escaping and non-escaping

A function-typed parameter, local, or field of a `ref struct` is **non-escaping** by default: the
function may call it and pass it to other non-escaping parameters, but may not store it, return it,
or pass it to an escaping parameter. One declared `escaping` is **escaping**. Every other position
holds an escaping function: a field of any other type, a return type, a type argument, an array
element, or a function type's return type.

| | Non-escaping | Escaping |
|---|---|---|
| Where | parameters, locals, and fields of a `ref struct` | other fields, return values, `escaping` parameters, locals, and fields, containers |
| Captures | by reference: may capture `ref`s and spans, and may change captured locals | by copy: a captured local cannot be `mut` |

`escaping` comes after any `mut`, `ref`, or `in`. It is written only where a function could be either
form: on a parameter, a local, a function type's own parameter, or a field of a `ref struct`.

- It is a compile-time error if `escaping` is written on anything other than a parameter, a local,
  a function type's parameter, or a field of a `ref struct`.
- It is a compile-time error if `mut` follows `escaping`.
- It is a compile-time error if a non-escaping function parameter or local is declared `mut` or
  assigned after its declaration: it keeps the function it was given.
- It is a compile-time error if a non-escaping parameter, local, or field is stored in an escaping
  field, a container, or an escaping parameter, or is returned. A constructor of a `ref struct`
  stores a parameter in a non-escaping field by the rules of [`ref struct`s](07-structs.md#structs.ref).
- Escaping is part of the function type. Each function type has a non-escaping and an escaping
  form, written the same way; the form written on a parameter, a local, or a function type's own
  parameter is non-escaping unless declared `escaping`. An escaping value converts to the
  non-escaping form; nothing converts the other way.
- A lambda is escaping when its target is escaping. A lambda passed as an argument takes the form of
  the parameter that overload resolution picks.
- Type arguments are always escaping, so a type argument inferred from a function is its escaping
  form.
- A parameter of nullable function type is non-escaping unless declared `escaping`. Every other
  nullable function type is escaping, including the type of a field of a `ref struct`.
- It is a compile-time error if a local of nullable function type is not escaping. Only a parameter
  can hold a nullable non-escaping function.
- It is a compile-time error to apply `??` to a nullable non-escaping function.
- An `escaping` parameter of an override or an interface implementation matches the method it
  implements, and overloads cannot differ only in which parameters are `escaping`.

**Example:**

```java
void forEach(Order[] orders, fn(Order) -> void visit) { ... }

void onEvent(List<fn() -> void> handlers, escaping fn() -> void handler) { handlers.add(handler); }

fn() -> void make() {
    mut int count = 0;
    return () -> std::print(count);     // error[WYL0273]: 'count' is 'mut'
}
```

<a id="functions.lambdas.captures"></a>
### 12.9.3 Captures

A lambda may use the locals and parameters in scope where it is created. An escaping lambda copies
them when it is created; a non-escaping lambda refers to them in place.

- It is a compile-time error if an escaping lambda captures a local that is `mut`.
- It is a compile-time error if a lambda assigns a local that is not `mut`.
- A local declared without a value and assigned once may be captured, since it is assigned before
  the lambda is created ([Variables assigned once](06-variables.md#variables.assign-once)).
- Inside an escaping lambda, the copies are read-only.
- A for-each or range loop's variable is a new variable in each iteration, so it may be captured.
  The counter of a `for` loop that is `mut` may not.
- An escaping lambda **moves** a unique local or by-value parameter it uses
  ([Moves](17-memory.md#memory.moves)), which cannot be used after the lambda is made. Inside the
  lambda it is read-only. A non-escaping lambda refers to it in place.
- It is a compile-time error for a lambda to move a local or parameter it captures: the lambda may
  run more than once.
- It is a compile-time error if an escaping lambda captures a `ref` or `in` variable or parameter, a
  span or other `ref struct`, a non-escaping function, or `this` in a struct's method or
  constructor. A captured class reference, `this` included, keeps its object alive, so a closure can
  be part of a cycle ([Cycles](17-memory.md#memory.cycles)).

<a id="functions.references"></a>
## 12.10 Function references

A free function or static method used where a function type is expected is a **function
reference**: the lambda that calls it with its arguments. The function type's parameters select the
overload as a call's arguments would, and the result converts to the function type's return type, or
is discarded when that is `void`.

```java
void sort(List<Order> orders, fn(Order, Order) -> int compare) { ... }
int byOrder(Order a, Order b) { return a.id - b.id; }

void main() {
    var orders = new List<Order>();
    sort(orders, byOrder);              // (a, b) -> byOrder(a, b)
}
```

- It is a compile-time error to use an instance method as a value.
- It is a compile-time error to use a function or static method as a value where no function type
  is expected.
- An overloaded function or static method can be used as a value only where the expected function
  type selects its overload. It is a compile-time error otherwise.
- Otherwise a function or static method used as a value is a lambda: it is escaping when its target
  is, a generic function's type argument is inferred from what it returns, and `?:` may choose it
  ([Choosing between functions](#functions.choices)).
- It is a compile-time error to give named arguments to a function value: a function type's
  parameters have no names.

**Example:**

```java
class Order {
    pub int id;

    pub int key(int x) { return id + x; }

    pub static int twice(int x) { return x * 2; }

    void test() {
        fn(int) -> int f = Order.twice;
        fn(int) -> int g = key;         // error[WYL0275]
    }
}
```

<a id="functions.choices"></a>
## 12.11 Choosing between functions

A `?:` expression and a switch expression may choose between functions wherever a function can be
used: `fn(int) -> int f = up ? x -> x + 1 : x -> x - 1;`, or `sort(orders, up ? ascending :
descending)`. The choice is made once, when the expression is evaluated
([The conditional operator](13-expressions.md#expressions.conditional),
[Switch expressions](13-expressions.md#expressions.switch)).

- Passed as an argument, a choice whose options include lambdas waits for its function type as a
  lambda does: overload resolution and generic inference treat each option as the argument.
- Anywhere else, a lambda option without a target type takes another option's function type.
- It is a compile-time error if a lambda that a switch expression of non-escaping type chooses uses
  a name its case declares: the lambda is made outside the case. An escaping lambda is made in its
  case, and may use them.
- A choice of non-escaping type makes its lambdas and refers to its non-escaping locals where the
  choice is evaluated. An escaping option, such as a field or a call, is evaluated only when it is
  chosen.

**Example:**

```java
enum Mode { Add(int n) }

fn(int) -> int pick(bool add) {
    return add ? x -> x + 1 : x -> x - 1;
}

void bad(Mode mode) {
    fn(int) -> int f = switch (mode) {
        case Add(var n) -> x -> x + n;  // error[WYL0278]
    };
}
```
