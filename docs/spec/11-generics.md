<a id="generics"></a>
# 11. Generics

A generic declaration has type parameters, which stand for types given where it is used. This chapter gives how
type parameters are declared and given arguments, how the arguments of a call are inferred, what bounds a
parameter may have, and what the code of a generic declaration may do with a value of a type parameter.

<a id="generics.parameters"></a>
## 11.1 Type parameters

```ebnf
TypeParameters = "<" TypeParameter { "," TypeParameter } ">" .
TypeParameter  = Identifier [ "extends" Bound { "&" Bound } ] .
WhereClause    = "where" BoundParameter { "," BoundParameter } .
BoundParameter = Identifier "extends" Bound { "&" Bound } .
Bound          = Type | "copyable" | "sendable" .
```

A struct, class, enum, interface, function, or method declares type parameters after its name. A type
parameter is a type in the whole declaration. Its bounds are given in [Bounds](#generics.bounds).

```java
struct Pair<A, B> {
    pub A first;
    pub B second;
}

T identity<T>(T value) { return value; }
```

- It is a compile-time error if two type parameters of a declaration have the same name.
- It is a compile-time error if a type parameter of a method has the name of a type parameter of its type.
- It is a compile-time error if a method that is `virtual`, `override`, or `abstract` has type parameters, or
  a method of an interface has any.
- It is a compile-time error to construct a type parameter, as `T()` or `new T()`: the type argument is any type,
  and a constructor of it is not known.

**Example:**

```java
struct Box<T> {
    pub T value;

    pub void each<T>(T other) { }       // error[WYL0193]
}
```

<a id="generics.arguments"></a>
## 11.2 Type arguments

A generic type is used with a **type argument** for each of its type parameters, written in angle brackets
after its name: `Pair<int, float>`. A generic function or method may be called with type arguments written
after its name, `identity<long>(5)`.

- A generic type is written with all its type arguments wherever a type is written, and wherever a struct,
  enum, or class is constructed: there is no inference for a construction. `Pair<int, float>(1, 2.5f)` and
  `new Box<int>()`.
- Type arguments follow the name of a call or of a construction, or of the type of an enum value:
  `Reply<int>.Pending`. The type arguments of a generic enum belong to the enum, not its variant.
- It is a compile-time error to write type arguments for a type that has no type parameters, or the wrong
  number of them.
- It is a compile-time error to write type arguments on a name that is not called or constructed, or on a
  member that is not called.
- Two instances of a generic type are the same type when their type arguments are the same types. There are no
  conversions between type arguments: `Pair<int, int>` and `Pair<long, long>` are not related.
- A type argument satisfies the bounds of its parameter ([Bounds](#generics.bounds)).

In an expression, `Name<` is read as the start of type arguments when the matching `>` is followed by `(`
or `.` ([Primary expressions](13-expressions.md#expressions.primary)).

**Example:**

```java statements
var p = Pair<int, float>(1, 2.5f);
var q = Pair(1, 2.5f);                  // error[WYL0200]: no type arguments
var r = Option.Some<int>(1);            // error[WYL0197]: they belong to the enum
```

<a id="generics.arguments.inference"></a>
### 11.2.1 Inferring type arguments

A call of a generic function or method that gives no type arguments **infers** them from the arguments.

- The type argument for a parameter is determined from each argument whose parameter type mentions it:
  from the type of a value, from the element type of a span a value converts to, from the value type of a
  nullable type for `T?`, and from the result type of a lambda
  ([Lambdas](12-functions.md#functions.lambdas.typing)).
- A bound determines the type parameters it mentions once the parameter it bounds has a type: with
  `<I extends Iterator<T>, T>`, an argument of a type that implements `Iterator<int>` determines `I`,
  and through the bound determines `int` for `T`. The bound determines nothing if the arguments give
  the bounded parameter more than one type, or if the bounded parameter's type implements the
  bound's interface with more than one list of type arguments.
- When the arguments give different types for a parameter, it is the type that the others convert to implicitly
  ([Numeric conversions](05-conversions.md#conversions.numeric)): `max(1, 2L)` infers `long`. An integer
  constant takes the type of another argument when that type holds it
  ([Integer constants](05-conversions.md#conversions.constants)): with `byte b`, `max(b, 1)` infers `byte`.
  Within type arguments there are no conversions.
- When a generic and a non-generic overload are equally good, the non-generic one is chosen
  ([Overload resolution](12-functions.md#functions.overloading.resolution)).
- A lambda that returns no value determines `Unit` for a type parameter that is its whole result type:
  with `T run<T>(fn() -> T f)`, `run(() -> { })` infers `Unit`.
- It is a compile-time error if a type argument is determined by no argument, or by a call that returns no
  value, or by `null`, or by a lambda that returns no value where the lambda's result type is not the type
  parameter itself.
- It is a compile-time error if the arguments determine two types for a type argument and neither converts to
  the other.
- It is a compile-time error if the lambda's result type determines the type argument and more than one
  overload takes the arguments.

The type arguments of a generic enum written without them are inferred in the same way
([Leaving out the enum](09-enums.md#enums.variants.implicit)).

**Example:**

```java
T make<T>() { ... }
void same<T>(T a, T b) { }

void main() {
    var a = make();                     // error[WYL0201]: nothing determines 'T'
    same(1, "a");                       // error[WYL0204]: 'int' or 'String'
    var b = make<int>();
}
```

<a id="generics.bounds"></a>
## 11.3 Bounds

`T extends A & B` requires each type argument for `T` to satisfy `A` and `B`. A bound is an interface or one of
the words `copyable` and `sendable`. A bound may mention the type parameters: `T extends Comparable<T>`.

Inside the declaration, a value of type `T` has the methods of the interfaces among the bounds, and the
properties the other bounds name.

- A type satisfies an interface bound if it implements the interface: a class, struct, or enum by declaring
  it ([Implementing an interface](10-interfaces.md#interfaces.implementing)). The primitive types and `String`
  implement `Printable`, `Hashable`, and, except `bool`, `Comparable` ([Printable](a-library.md#library.printable),
  [Comparable](a-library.md#library.comparable)).
- A nullable type does not satisfy a bound: a method of the type could not be called on `null`.
- A bound does not make `T` convert to the interface: `T` may be a struct, which does not
  ([Interfaces of structs and enums](10-interfaces.md#interfaces.structs)).
- `copyable` and `sendable` are **type properties**. A type satisfies `copyable` if it is copyable
  ([Copyable types](04-types.md#types.copyable)), and `sendable` if it is sendable
  ([Sendable types](19-concurrency.md#concurrency.sendable)). A type parameter has a property only if a
  bound names it. The words have this meaning in a bound only; elsewhere they are identifiers.
- It is a compile-time error if a bound is a type other than an interface or one of the two words.
- It is a compile-time error if a type argument does not satisfy a bound of its parameter.

A member of a generic type may add bounds to its type's type parameters with a **`where` clause**
after its parameter list. It is a contextual keyword: it has this meaning only here, and it is an
ordinary identifier everywhere else.

```java
struct Box<T> {
    T value;

    T copy() where T extends copyable {
        return value;
    }
}
```

- A clause names a type parameter of the type the member belongs to. It is written like a type
  parameter's own bounds.
- In the member's signature and body, the parameter has the bounds: `copyable` makes its values
  copyable, and an interface among them provides its methods. A type written in the signature, such
  as the return type, may therefore take the parameter as an argument that needs the bounds.
- At every use, the type's type arguments must satisfy the member's clause. It is a compile-time
  error if they do not.
- It is a compile-time error to write a `where` clause on a free function, on a method of an
  interface, on a method that is `virtual`, `override`, or `abstract`, or on a method that
  implements an interface method: a call through the interface or the base class would not check
  the bound.
- A clause is not part of a member's signature: two members with the same name and parameter
  types conflict regardless of their clauses.

**Example:**

```java
T maxOf<T extends Comparable<T>>(T a, T b) {
    return a.compareTo(b) >= 0 ? a : b;
}

void main() {
    maxOf(3, 7);
    maxOf(true, false);                 // error[WYL0208]: 'bool' is not 'Comparable'
}
```

```java unchecked
Pair<T, T> twin<T extends copyable>(T value) {
    return Pair<T, T>(first: value, second: value);     // 'value' is copied
}
```

<a id="generics.unbounded"></a>
## 11.4 Unbounded type parameters

A value of a type parameter without bounds is declared, moved, passed (including by `ref` and `in`), and
returned, and nothing else.

- The type argument may be a unique type, so a value of type `T` is not copied unless `T` has the bound
  `copyable`. Moves need no bound: a method that stores its argument, or returns a value it removes from a
  container, works for every `T`.
- `==` is not defined on `T`, and an overload accepts a `T` only if it is generic.
- `T()` is not an expression ([Type parameters](#generics.parameters)).
- A struct with a field of type `T` and no initializer for it has no default value inside the generic code:
  `Pair<T, int>()` is an error, and `Pair<T, int>(first: x)` is valid. From code where the type arguments are
  known, `Pair<int, int>()` is the default value of that type.

**Example:**

```java unchecked
T? firstCopy<T extends copyable>(ReadOnlySpan<T> items) {
    return items.length() == 0 ? null : items[0];
}
```

<a id="generics.nullable"></a>
## 11.5 Nullable type parameters

`T?` is allowed for every type parameter `T`. It is a nullable value if the type argument is a value type, and
a nullable reference if it is a class. When the type argument is itself nullable, `T?` is the same type as `T`.

A call infers `T` from a `T?` parameter through the value type of its argument: `orElse(x, 0)` with
`int? x` infers `int`.

**Example:**

```java
T? first<T>(List<T> items) { ... }

void main() {
    var ints = new List<int>();
    var nodes = new List<Node>();
    var maybeInts = new List<int?>();
    int? a = first(ints);
    Node? b = first(nodes);
    int? c = first(maybeInts);          // null for an empty list and for a null element
}
```

<a id="generics.checking"></a>
## 11.6 Checking generic code

The code of a generic declaration is checked once, where it is declared, against its bounds: names and types
are resolved, and operations are allowed, with the type parameters as types that have only what their bounds
give. It is a compile-time error in the declaration, not in a use of it, if the code calls a method that no
bound provides.

Whether a value of a type involving a type parameter is copied or moved follows from the bounds in the
declaration, not from the type arguments of an instance: `T y = x;` moves `x` for every `T` unless `T` has the
bound `copyable`, and using `x` after that is a compile-time error even if every instance is copyable
([Moves](17-memory.md#memory.moves)). The one place an instance decides is a reference binding into a value that
may be replaced ([Reference bindings](15-patterns.md#patterns.references)).

**Example:**

```java
interface Named { String name(); }

void show<T>(T value) {
    std::print(value.name());           // error[WYL0300]: cannot find method 'name' in 'T'
}
```

<a id="generics.recursion"></a>
## 11.7 Recursive instantiation

A generic declaration is instantiated for each combination of type arguments it is used with, including the
ones its own code uses.

- It is a compile-time error if instantiating a declaration needs an instance of itself with type arguments
  that contain its own: `void f<T>(T x) { f<Pair<T, T>>(...); }`. The set of instances would not be finite.
- The same rule applies to the field types of a generic struct
  ([Recursive structs](07-structs.md#structs.recursive)).
