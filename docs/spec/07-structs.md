<a id="structs"></a>
# 7. Structs

A struct is a value type declared by the program. This chapter gives how a struct is declared, built,
copied, and compared, and the two kinds of struct that are not copied freely: the unique struct, which
has one owner, and the `ref struct`, which views storage it does not own.

<a id="structs.declarations"></a>
## 7.1 Struct declarations

```ebnf
StructDeclaration = { Annotation } [ Visibility ] [ "unique" | "ref" | "extern" ] "struct"
                    ( Identifier | PrimitiveName )
                    [ TypeParameters ] [ "implements" TypeList ] "{" { StructMember } "}" .
StructMember      = StructField | StructConstructor | Method | Indexer | Destructor .
```

A struct is a **value type**: a value of a struct type is stored in its variable, field, or element, and
is copied where a value is copied ([Copies](#structs.copies)). A struct has fields, constructors, and
methods, and can be generic ([Chapter 11](11-generics.md#generics)). A `unique` struct is not copied
but moved ([Unique structs](#structs.unique)), and a `ref struct` is a view of storage
([`ref struct`s](#structs.ref)).

- It is a compile-time error if a struct declares `extends`: a struct has no base type. A struct lists
  the interfaces it implements after `implements` ([Interfaces of structs and enums](10-interfaces.md#interfaces.structs)).
- It is a compile-time error if a member of a struct is declared `virtual`, `override`, `abstract`, or
  `final`.
- It is a compile-time error if two members of a struct have the same name and are not both methods or
  both constructors: a field and a method, for example. Methods of one name are overloads
  ([Overloading](12-functions.md#functions.overloading)).
- A struct has no copy constructor. A destructor is declared only in a `unique` struct
  ([Destructors](#structs.unique.destructors)).

**Example:**

```java
struct Point {
    pub int x;
    pub int y;

    pub int sum() { return x + y; }
}

struct Marker extends Point { }         // error[WYL0095]
```

<a id="structs.fields"></a>
## 7.2 Fields

```ebnf
StructField = { Annotation } [ Visibility ] Type Identifier { "," Identifier } [ "=" Expression ] ";" .
```

A field has a type and optionally an initializer, an expression that runs at the start of each
construction ([The memberwise constructor](#structs.constructors.memberwise)). A field declared
with a list of names declares one field for each.

- It is a compile-time error if two fields of a struct have the same name.
- It is a compile-time error if a field of a struct is declared `mut`. The fields of a struct value can
  be assigned wherever the struct value is a mutable place
  ([Mutability](06-variables.md#variables.mutability)).
- It is a compile-time error if a field's type is a `ref` or `in` type
  ([References](17-memory.md#memory.references)).
- It is a compile-time error if a field's initializer uses `this`, another field, or an instance method
  of the struct.

**Example:**

```java
struct Point {
    pub int x;
    pub int x;                          // error[WYL0099]
}
```

<a id="structs.constructors"></a>
## 7.3 Constructors

```ebnf
StructConstructor = { Annotation } [ Visibility ] [ "unsafe" ] Identifier "(" [ Parameters ] ")"
                    [ WhereClause ] Block .
```

A struct value is made by calling a constructor, written as a call to the struct's name: `Point(1, 2)`,
without `new`. A generic struct is written with its type arguments:
`Pair<int, float>(1, 2.5f)` ([Type arguments](11-generics.md#generics.arguments)). The arguments of a
constructor call are evaluated in the order written
([Evaluation order](13-expressions.md#expressions.order)).

A constructor declared in a struct starts from empty storage: the field initializers run, and then
the body runs with `this` as the value being built. A `return;` ends the construction, and the value
is what `this` holds then. A field's **initial value** is the value empty storage holds for its type:
a nullable value's initial value is null, and any other type with a default value has that value as
its initial value. A type that runs a destructor has no initial value, and neither does a struct or
fixed array that contains such a type.

- A constructor whose first statement is `this(...)` calls another constructor of the struct and starts
  from the value that constructor made. It is a compile-time error if the arguments of `this(...)` use
  `this`. It is a compile-time error if a chain of `this(...)` calls comes back to a constructor that
  is already in it.
- `this` may be assigned as a whole: `this = Point(a, a);`.
- A field of the struct that has no initializer and whose type has no initial value is assigned in
  every constructor that does not start with `this(...)`, before `this` is used
  ([Constructor flow](16-flow.md#flow.constructors)).

Declaring a constructor removes the memberwise constructor.

**Example:**

```java
struct Range {
    pub int low;
    pub int high;

    pub Range(int high) { this(0, high); }
    pub Range(int low, int high) { this.low = low; this.high = high; }
}
```

<a id="structs.constructors.memberwise"></a>
### 7.3.1 The memberwise constructor

A struct that declares no constructor has a **memberwise constructor**: it takes one parameter for each
field, in declaration order, with the field's name and type. A parameter that has an argument is
set to it; a parameter without one takes the field's initializer, or else the field's initial value.
A call may name its arguments and leave out the ones that have defaults
([Named arguments](12-functions.md#functions.arguments)).

- A field with no initializer whose type has no initial value (a class or interface type, a type
  parameter, a type with no default value, or a type that runs a destructor) is a required argument.
  It is a compile-time error if a call to the memberwise constructor gives no argument for one.
- After the arguments are evaluated, the fields are set in declaration order. Initializers are
  evaluated at that point, for the fields that need them.
- The memberwise constructor is as visible as the least visible field of the struct
  ([Visibility](03-programs.md#programs.visibility)). A `pub` struct with a field that is not visible
  somewhere can be named there, and built there only by another constructor of the struct that is
  visible.

**Example:**

```java
class Customer { }

struct Order {
    pub int id;
    pub int quantity = 1;
    pub Customer customer;
}

void main() {
    var c = new Customer();
    var a = Order(7, customer: c);      // quantity is 1
    var b = Order(id: 7);               // error[WYL0100]: 'customer' has no value
}
```

<a id="structs.constructors.default"></a>
### 7.3.2 The default value

`T()` is the **default value** of a struct type `T` that is defaultable
([Default values](04-types.md#types.defaults)): each field holds its initializer, or its type's default
value. Every struct with such fields has it, whether or not it declares a constructor, and it
cannot be declared.

- It is a compile-time error if a struct declares a constructor with no parameters.
- It is a compile-time error to write `T()` for a struct that is not defaultable. The struct's other
  constructors, and the memberwise constructor with every required argument given, remain.
- Fixed arrays of a struct, and the fields of an enclosing struct that have no initializer, hold the
  default value.

**Example:**

```java
class Customer { }

struct Invoice {
    pub int total;
    pub Customer customer;
}

void main() {
    var c = new Customer();
    var a = Invoice();                  // error[WYL0102]: 'Customer' has no default value
    var b = Invoice(customer: c);       // valid
}
```

<a id="structs.methods"></a>
## 7.4 Methods

A method of a struct is written as any method is ([Instance and static methods](12-functions.md#functions.methods)).
A method of a struct is called on a struct value or place, and `this` is that value. In a method that
is not `mut`, `this` is read-only: its fields cannot be assigned, and its `mut` methods cannot be
called. A `static` method has no `this`. A method is called on a place without copying it, unless it
takes its receiver by value ([By-value receivers](#structs.methods.by-value)).

<a id="structs.methods.mut"></a>
### 7.4.1 `mut` methods

A struct method declared `mut` can change `this`: in its body, `this` is a mutable place, and the
method is called on one.

- It is a compile-time error if a `mut` method is called on a place that is read-only: a variable that
  is not `mut`, an `in` parameter or variable, an element of a `ReadOnlySpan`, or a field of one of
  these ([Mutability](06-variables.md#variables.mutability)).
- A temporary struct or fixed array is a value, not a variable: a call or constructor result, a `?:` of
  two values, or `v!`. It is a compile-time error to call a `mut` method on a temporary, to assign its
  fields or elements, or to pass it by `ref`. A write through a temporary that reaches an object is
  valid: `makeList().add(1)`.
- It is a compile-time error to declare a class method `mut`. A class method does not change the
  reference it is called through, and the fields of an object that can change are the `mut` ones
  ([Fields](08-classes.md#classes.fields)). Enum methods are not `mut` either
  ([Methods](09-enums.md#enums.methods)). A `mut` method of an interface is
  [declared there](10-interfaces.md#interfaces.mut).

**Example:**

```java
struct Counter {
    pub int n;
    pub mut void bump() { n++; }
}

Counter make() { return Counter(0); }

void main() {
    Counter a = Counter(0);
    a.bump();                           // error[WYL0105]: 'a' is not 'mut'
    make().bump();                      // error[WYL0104]
    mut Counter b = Counter(0);
    b.bump();
}
```

<a id="structs.methods.by-value"></a>
### 7.4.2 By-value receivers

A struct or enum method whose first parameter is written `this T name` takes its receiver by value,
as a **by-value receiver**. `T` is the type that declares the method, with that type's own type
parameters as its type arguments. In the body, `name` is a by-value parameter that is not `mut`: it
may be moved from, and it is released when the method ends if it holds a value then
([Moves](17-memory.md#memory.moves)). The method has no `this`, and reaches the receiver's members
through `name`.

The method is called as an instance method is, and the receiver is not among the call's arguments. It
is passed as a by-value argument is, at its turn: a value of a copyable type is copied, and any other
value is moved. A field, an element, or what a reference refers to is therefore not a receiver if its
type is not copyable ([Moves](17-memory.md#memory.moves)).

- It is a compile-time error to declare a by-value receiver on a method of a class or an interface,
  on a constructor, or on an indexer.
- It is a compile-time error to write `this ref` or `this in` on a method.
- It is a compile-time error if `T` is any other type than the one described above.
- It is a compile-time error if a method with a by-value receiver is `mut` or `static`.
- It is a compile-time error for a method with a by-value receiver to return a `ref`, `in`, `ref?`, or
  `in?`, or a `ref struct`.

**Example:**

```java unchecked
unique enum Lookup {
    Found(Buffer buffer),
    Missing;

    pub Buffer orElse(this Lookup lookup, Buffer fallback) {
        return switch (lookup) {
            case Found(var buffer) -> buffer;
            case Missing -> fallback;
        };
    }
}

void main() {
    var lookup = Lookup.Found(Buffer(4));
    Buffer kept = lookup.orElse(Buffer(0));
    lookup.orElse(Buffer(0));               // error: 'lookup' is used after being moved
}
```

<a id="structs.copies"></a>
## 7.5 Copies

A copy of a struct value is a new value whose fields are copies of the fields of the original. A copy is
shallow: a field that refers to an object refers to the same object in the copy, so a change to the
object is visible through both. A `String` is a value that is never changed, so a copy of it cannot be
told from the original.

A copy is made where a copyable struct value is:

- the initializer of a variable, or assigned to a variable, field, or element;
- passed to a parameter that is not `ref` or `in`, or returned;
- read out of a container, an array, or a field by value: `Order o = orders[i];`;
- the variable of a `for` loop over elements that is not `ref` or `in`
  ([For-each](14-statements.md#statements.for-each));
- captured by an escaping lambda ([Captures](12-functions.md#functions.lambdas.captures)).

A method called on a place, a `ref` or `in` parameter or variable, and a `ref` or `in` loop variable
refer to the value without copying it.

A struct that is `unique` is moved where the list says a copy is made ([Unique structs](#structs.unique)).

<a id="structs.unique"></a>
## 7.6 Unique structs

A struct declared `unique` has no copies: it is **moved**. At any time a value of a unique struct is
held by one variable, field, or element, its **owner**.

- A unique struct is not copyable ([Copyable types](04-types.md#types.copyable)).
- Wherever [Copies](#structs.copies) lists a copy, a unique struct is moved instead ([Moves](17-memory.md#memory.moves)).
  It is a compile-time error to use a variable after its value is moved out
  ([Moves](16-flow.md#flow.moves)). A `ref` or `in` parameter, a `ref` or `in` variable, and a call of a
  method that does not take its receiver by value refer to the value in place and do not move it.
- It is a compile-time error if a struct has a field of a unique type and is not declared `unique`; the
  same holds for an enum with a variant field of one ([Unique enums](09-enums.md#enums.unique)).
- A fixed array `T[N]` and a nullable type `T?` with a unique `T` are unique types. A generic struct
  or enum is unique for the type arguments that make one of its fields unique: `Pair<Buffer, int>`
  is unique when `Buffer` is. A class is never unique.

<a id="structs.unique.destructors"></a>
### 7.6.1 Destructors and `discard`

```ebnf
Discard = "discard" Identifier ";" .
```

A unique struct may declare one destructor ([Destructors](08-classes.md#classes.destructors) gives its
form), named `~` and the struct's name, with no parameters and no modifiers. It is a compile-time error to declare one in a struct that is not `unique`.

A value's destructor runs when the value is released
([Destruction](17-memory.md#memory.release.destruction)): when its owner leaves scope holding it, when
it is overwritten, when its container is destroyed or removes it, and when the value that contains it
is released. It does not run for a value that was moved. After the body, the fields are released
([Order of destruction](17-memory.md#memory.release.destruction)), and a field whose type has a
destructor runs it.

- In the destructor, `this` is a `ref`: the body may assign fields and call `mut` methods. It is a
  compile-time error to move out of `this`.
- The value is in use for the whole body ([Holds](17-memory.md#memory.holds)): replacing or releasing the
  whole value in the body panics.
- A panic in a destructor ends the program as any panic does.
- A static cannot have a unique type ([Statics](06-variables.md#variables.statics)), so no destructor
  runs at the end of the program.

`discard name;` ends the lifetime of a by-value parameter, a by-value receiver among them, without
running its destructor's body; its fields are released as usual.

- It is a compile-time error if `name` is not a by-value parameter of a unique struct type that has a
  destructor.
- It is a compile-time error if the function that contains `discard` is not declared in the file of the
  type. It may be a function, a static method, an extension method whose receiver is `this T` for
  that type, or a method of the type with a by-value receiver.
- A pattern does not move a field out of a value whose type has a destructor
  ([Moving bindings](15-patterns.md#patterns.bindings.moves)).
- After `discard name;` the parameter is moved. On a path that does not discard it, the parameter is
  released when the function ends, and its destructor runs.

**Example:**

```java unchecked
unique struct Handle {
    pub int id;
    ~Handle() { release(id); }
}

Result<Unit, Error> close(Handle h) {
    if (!flush(h.id)) {
        return Err(Error.Flush);            // 'h' is released here: the destructor runs
    }
    discard h;                              // the destructor does not run
    return Ok(Unit());
}
```

<a id="structs.ref"></a>
## 7.7 `ref struct`s

A struct declared `ref struct` is a **view**: it refers to storage it does not own, such as the elements
of an array. `Span<T>` and `ReadOnlySpan<T>` are `ref struct`s ([Spans](04-types.md#types.spans)). A
`ref struct` is a second-class value ([Second-class values](17-memory.md#memory.second-class)).

A variable or parameter of a `ref struct` type may be `mut`; it is still bound where it is declared.

- It is a compile-time error if a `ref struct` is the type of a field, a variant field, an element of
  an array, the target of a pointer, or a type argument.
- It is a compile-time error if a `ref struct` variable is declared without an initializer, or is
  assigned after its declaration: it views one thing for its whole scope.
- It is a compile-time error if a `ref struct` is the type of a `?` ([Nullable types](04-types.md#types.nullable)),
  the result of a `?:` or `switch` expression, or a value matched against a pattern.
- It is a compile-time error if a function other than an instance method returns a `ref struct`. An
  instance method returns only a view of its receiver: of its fields or elements, or a span that its
  methods return.
- It is a compile-time error if a `ref struct` variable views a temporary struct, which is gone after its
  statement. A temporary object is held by the view.
- What a `ref struct` views is held, or borrowed, while the view is used
  ([Holds](17-memory.md#memory.holds), [Borrows](17-memory.md#memory.borrows)).

**Example:**

```java
ref struct Cursor {
    pub int position;
}

struct Reader {
    pub Cursor cursor;                  // error[WYL0418]
}
```

<a id="structs.equality"></a>
## 7.8 Equality

`==` and `!=` are defined on a struct type when every field's type supports them. `a == b` is true
when every field of `a` equals the field of `b` of the same name, and the operators cannot be
redefined. It is a compile-time error to apply them to a struct with a field of a type that does not
support them, such as a function type or a type parameter ([Comparison](13-expressions.md#expressions.comparison)).

<a id="structs.layout"></a>
## 7.9 Layout

The fields of a struct are laid out in declaration order, each aligned as the C++ type of the same
width is, with padding between them as needed. The alignment of a type is
implementation-defined ([Implementation-defined behavior](01-introduction.md#introduction.implementation-defined)).

| Annotation | On | Effect |
|---|---|---|
| `@Align(n)` | a struct or field | aligns it to `n` bytes, a power of two |
| `@Packed` | a struct | no padding between fields |

Padding that a program needs to name is declared as a field: `byte[4] pad;`.

<a id="structs.recursive"></a>
## 7.10 Recursive structs

A struct or enum contains the types of its fields, of its variants' fields, and what those contain:
the elements of a fixed array, the value of a nullable value type, and the type arguments of a generic
struct, according to where they are used as fields.

- It is a compile-time error if a struct or enum contains itself by value. A class is not contained
  by value, so a struct may have a field of a class type that has a field of the struct.
- It is a compile-time error if the types a struct's fields contain nest without end:
  `struct Grow<T> { pub Grow<Pair<T, T>> next; }`
  ([Recursive instantiation](11-generics.md#generics.recursion)).

**Example:**

```java
struct Node {                           // error[WYL0106]: a nullable value is stored inline
    pub int value;
    pub Node? next;
}
```
