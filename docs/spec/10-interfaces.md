<a id="interfaces"></a>
# 10. Interfaces

An interface is a set of method signatures that types implement. This chapter gives how an interface is
declared, how a class, struct, or enum implements it, and where a value of an interface type is allowed.

<a id="interfaces.declarations"></a>
## 10.1 Interface declarations

```ebnf
InterfaceDeclaration = { Annotation } [ Visibility ] "interface" Identifier [ TypeParameters ]
                       [ "extends" TypeList ] "{" { InterfaceMethod } "}" .
InterfaceMethod      = [ "mut" ] ( Type | "void" ) Identifier "(" [ Parameters ] ")" ";" .
```

An interface has methods, each a signature with no body. An interface that `extends` others has their
methods as well as its own. An interface can be generic ([Chapter 11](11-generics.md#generics)).

A variable of an interface type refers to an object of a class that implements the interface, and a call of
its method runs the method of the object's class. Interface types are reference types: they are nullable with
`?`, `==` compares identity ([Equality](08-classes.md#classes.equality)), and a class converts to the
interfaces it implements ([Identity and reference conversions](05-conversions.md#conversions.identity)).

- It is a compile-time error if an interface declares `implements`, a constructor, or a field.
- It is a compile-time error if a method of an interface has a body, a visibility, a type parameter, or a
  modifier other than `mut`. A method is as visible as its interface.
- It is a compile-time error if a signature is declared twice in an interface, or is already declared in
  an interface it extends.
- It is a compile-time error to create an object of an interface type.

**Example:**

```java
interface Named {
    String name();
}

interface Labeled extends Named {
    String label();
}

void main() {
    var n = new Named();                // error[WYL0180]
}
```

<a id="interfaces.implementing"></a>
## 10.2 Implementing an interface

```ebnf
Implements = "implements" Type { "," Type } .
```

A class, struct, or enum **implements** the interfaces after `implements`, and the interfaces they extend. A
type implements a method of an interface with a method of the same name and exactly the same parameter and
return types, including nullability and which parameters are `escaping`, declared in the type or inherited
from its base class. The method is written without `override`, and has the visibility of the
interface ([Visibility](03-programs.md#programs.visibility)), which is the visibility of its methods.
Implementing a method does not make it overridable: a method that is to be overridden is declared `virtual`
([Virtual methods](08-classes.md#classes.overriding)).

- It is a compile-time error if a type that is not abstract has no method for a method of an interface it
  implements ([Abstract classes](08-classes.md#classes.abstract)).
- It is a compile-time error if the type after `implements` is not an interface, or if an interface is listed
  twice.
- It is a compile-time error if the implementing method is `static`, is `unsafe`, has another visibility, or
  has other parameter or return types.

**Example:**

```java
interface Named {
    String name();
}

class Box implements Named {
    String name() { return "box"; }
}

class Crate implements Named { }        // error[WYL0185]
```

<a id="interfaces.mut"></a>
## 10.3 `mut` interface methods

A method of an interface declared `mut` is one a struct implements with a `mut` method
([`mut` methods](07-structs.md#structs.methods.mut)).

- It is a compile-time error if a `mut` method implements a method of an interface that is not `mut`.
- A class implements a `mut` method of an interface with a method that is not `mut`: a class method never
  changes the reference it is called through.
- Code that is generic in a type parameter bounded by an interface calls a method of the interface that is
  not `mut` on any value, including a variable that is not `mut` and an `in` parameter. It is a compile-time
  error to call a `mut` one on a place that is not mutable, because the type argument may be a struct.

**Example:**

```java
interface Resettable {
    mut void reset();
}

void clear<T extends Resettable>(T value) {
    value.reset();                      // error[WYL0105]: 'value' is not 'mut'
}
```

<a id="interfaces.structs"></a>
## 10.4 Interfaces of structs and enums

A struct or enum implements an interface for use as a bound ([Bounds](11-generics.md#generics.bounds)). Its
value is not converted to the interface type: it is a compile-time error to store a struct or enum value in a
variable of an interface type, or to pass it where one is expected. A call through a bound on a struct or an
enum is made without dispatch. A struct or enum does not implement `Borrowable`, nor an interface that
extends it ([`Borrowable`](17-memory.md#memory.borrows.borrowable)).

**Example:**

```java
interface Named {
    String name();
}

struct Tag implements Named {
    String name() { return "t"; }
}

void main() {
    Named n = Tag();                    // error[WYL0079]: expected 'Named', found 'Tag'
}
```
