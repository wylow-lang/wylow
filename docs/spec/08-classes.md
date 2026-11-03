<a id="classes"></a>
# 8. Classes

A class is the type of objects: values that are allocated, shared through references, and released when
the last reference to them goes ([Objects](17-memory.md#memory.objects)). This chapter gives how a class
is declared, what a subclass inherits and overrides, how an object is constructed, and its destructor.

<a id="classes.declarations"></a>
## 8.1 Class declarations

```ebnf
ClassDeclaration = { Annotation } [ Visibility ] [ "abstract" | "final" ] "class" Identifier
                   [ TypeParameters ] [ "extends" Type ] [ "implements" TypeList ]
                   "{" { ClassMember } "}" .
ClassMember      = Field | Constructor | Method | Destructor .
```

An object of a class is made with `new` ([Construction](13-expressions.md#expressions.construction)) and is
kept by references. A variable of a class type holds a reference, never null unless its type is
nullable ([Nullable types](04-types.md#types.nullable)). A class extends at most one class and implements
any number of interfaces ([Inheritance](#classes.inheritance),
[Implementing an interface](10-interfaces.md#interfaces.implementing)). A class can be generic
([Chapter 11](11-generics.md#generics)).

A class is never `unique`: its objects are shared ([Unique structs](07-structs.md#structs.unique)).

<a id="classes.fields"></a>
## 8.2 Fields

```ebnf
Field = { Annotation } [ Visibility ] [ "mut" ] Type Identifier { "," Identifier } [ "=" Expression ] ";" .
```

A field has a type and optionally an initializer, which is evaluated when an object is constructed
([Order of construction](#classes.construction)). A field declared with a list of names declares one field
for each.

- A field is **mutable**, so it can be assigned wherever the object is reachable, if it is declared
  `mut`. Any other field is assigned in the constructors of its class only. A struct stored in a field
  follows the field ([Mutability](06-variables.md#variables.mutability)).
- It is a compile-time error if a field has the name of a field of a base class that is visible to the
  class.
- It is a compile-time error if a field is declared with a body, as a property would be. A class has
  fields and methods.
- It is a compile-time error if a field is declared `virtual`, `override`, `abstract`, `final`, `unsafe`,
  or `extern`.
- It is a compile-time error if a field's initializer uses `this`, another field, or an instance method.
- It is a compile-time error if a field is `static` ([Statics](06-variables.md#variables.statics)), or if
  its type is a `ref` or `in` type ([References](17-memory.md#memory.references)).

**Example:**

```java
class Shape {
    pub int size;
}

class Square extends Shape {
    pub int size;                       // error[WYL0110]
}
```

<a id="classes.inheritance"></a>
## 8.3 Inheritance

`class B extends A` declares `B` a **subclass** of the class `A`, its **base class**. An object of `B`
has the fields of `A` and `B`, and `B` has the methods of `A` that it can see
([Visibility](03-programs.md#programs.visibility)); a method it cannot see is not inherited.

- A value of a subclass converts to its base classes, and to the interfaces that the class or its base
  classes implement ([Identity and reference conversions](05-conversions.md#conversions.identity)). A value
  converts to a subclass only through a type pattern ([Patterns](15-patterns.md#patterns.kinds)): there is
  no other conversion.
- `super.m(...)` calls the method `m` of the base class on `this`, without dispatch to an override. `super`
  is used in an instance method or a constructor of a class that has a base class.
- It is a compile-time error to use `super` in a class that has no base class, or in a static method.
- It is a compile-time error if the type after `extends` is an interface, or is not a class (a struct, an
  enum, a primitive type), or is nullable, or is a `final` class.
- It is a compile-time error if a class is its own ancestor.

**Example:**

```java
class Shape { }
class Square extends Shape { }

Shape s = new Square();
if (s is Square q) { }              // the only way to a Square
```

<a id="classes.overriding"></a>
## 8.4 Virtual methods and overriding

A method of a class is **virtual** if it is declared `virtual`, `override`, or `abstract`. A call to a
virtual method runs the method of the object's own class, or of its nearest base class that has one. Any
other method is called without dispatch.

| Modifier | Meaning |
|---|---|
| `virtual` | The method may be overridden. |
| `override` | The method overrides a `virtual`, `abstract`, or `override` method of a base class. |
| `abstract` | The method has no body, and a subclass overrides it ([Abstract classes](#classes.abstract)). |
| `final` | On an `override`: the method is not overridden further. |

A method **overrides** the method of a base class that has the same name and parameter types.

- It is a compile-time error if a method declares more than one of `virtual`, `override`, and `abstract`.
- It is a compile-time error if a method is declared `final` and not `override`.
- It is a compile-time error if a method has the name and parameter types of an inherited method that is
  `virtual`, `abstract`, or `override` and is not declared `override`.
- It is a compile-time error if a method has the name and parameter types of an inherited method that is
  not virtual: a method does not hide another.
- It is a compile-time error if a method is declared `override` and no base class has a method it
  overrides, or the one it overrides is not virtual, or is `final`.
- An override has the parameter types, including their nullability, and the return type of the method it
  overrides, exactly: return types are not covariant. It declares the same parameters `escaping`, and the
  same visibility ([Visibility](03-programs.md#programs.visibility)). It takes the default values of the
  method it overrides and does not declare its own ([Default arguments](12-functions.md#functions.arguments)).
  Whether a method is `mut` is not part of its signature ([`mut` methods](07-structs.md#structs.methods.mut)).
- It is a compile-time error if an override is `unsafe` and the method it overrides is not.
- It is a compile-time error if a virtual method is generic ([Type parameters](11-generics.md#generics.parameters)).

**Example:**

```java
class Shape {
    pub virtual int area() { return 0; }
}

class Square extends Shape {
    pub override int area() { return 1; }
}

class Circle extends Shape {
    pub int area() { return 2; }        // error[WYL0120]
}
```

<a id="classes.abstract"></a>
## 8.5 Abstract and final classes

A class declared `abstract` is **incomplete**: it may have abstract methods, which have no body, and no
object is made of it directly. A class declared `final` has no subclasses.

- It is a compile-time error if a class is declared both `abstract` and `final`.
- It is a compile-time error if an abstract method has a body.
- It is a compile-time error if a class that is not `abstract` has an abstract method.
- It is a compile-time error if a class that is not `abstract` does not implement every abstract method it
  inherits, and every method of every interface it implements, declared in it or inherited
  ([Implementing an interface](10-interfaces.md#interfaces.implementing)). An `abstract` class may leave
  them to its subclasses.
- It is a compile-time error to create an object of an abstract class, or to call an abstract method
  through `super`.
- While a base class's constructor or destructor runs, a call of an abstract method of the object panics
  ([Order of construction](#classes.construction)).

**Example:**

```java
abstract class Shape {
    pub abstract int area();
}

class Square extends Shape {
    pub override int area() { return 1; }
}

var s = new Shape();                    // error[WYL0130]
```

<a id="classes.constructors"></a>
## 8.6 Constructors

```ebnf
ClassConstructor = { Annotation } [ Visibility ] Identifier "(" [ Parameters ] ")" Block .
```

A constructor has the name of the class. It is not inherited, and has none of the modifiers of a
method.

A class that declares no constructor has a **parameterless constructor**, as visible as the class.

- A constructor starts with a call `super(...)` to a constructor of the base class, or `this(...)` to
  another constructor of the class. It is a compile-time error if the call is not the first statement of
  the constructor, if it is used outside a constructor, or if `super(...)` is used in a class that has no
  base class.
- A constructor that does not start with such a call starts with a call to the parameterless constructor of
  the base class, if there is one. It is a compile-time error if the base class has none, and also for the
  implicit constructor.
- It is a compile-time error if the arguments of `super(...)` or `this(...)` use `this`.
- It is a compile-time error if an argument of `super(...)` or `this(...)` for a `ref` or `in`
  parameter, or a parameter of a type that views storage, is not a parameter of the constructor, a field of
  one, or `null`.
- It is a compile-time error if a constructor calls itself through a chain of `this(...)` calls, however
  long.

**Example:**

```java
class Shape {
    pub Shape(int id) { }
}

class Square extends Shape {
    pub Square() { }                    // error[WYL0140]: Shape has no parameterless constructor
}
```

<a id="classes.construction"></a>
## 8.7 Order of construction

`new C(args)` makes an object of `C`, selects a constructor by the arguments, and runs, in this order:

1. If the constructor starts with `this(...)`, the constructor it calls runs these steps, and then the
   body of this constructor runs. This constructor runs neither a base constructor nor the field
   initializers itself, so each runs once.
2. Otherwise, the base class constructor runs, selected by `super(...)` or the parameterless one.
3. The initializers of the fields of `C` run, in declaration order.
4. The body of the constructor runs.

A field **needs a value** if it has no initializer and its type has no default value
([Default values](04-types.md#types.defaults)): a non-null reference, a type parameter, or a struct that
has no default value. Until every field of the class that needs a value is assigned, `this` is used only to
assign fields and to read fields that are assigned: no method is called on it, and it is not passed.

- It is a compile-time error if a field that needs a value is not assigned in a constructor on every path
  before it returns ([Constructor flow](16-flow.md#flow.constructors)). After `this(...)`, every field is
  assigned.
- It is a compile-time error if a class declares no constructor and a field needs a value.

**While the constructor or the destructor of a base class runs, the object is an instance of that base
class.** A call of a virtual method runs the implementation of that class, so it does not read the fields
of a subclass before they are assigned or after they are released. A call of an abstract method panics.

**Example:**

```java
class Node { }

class Link {
    Node next;                          // error[WYL0143]: no constructor assigns it
}
```

<a id="classes.destructors"></a>
## 8.8 Destructors

```ebnf
Destructor = "~" Identifier "(" ")" Block .
```

A class may declare one destructor, named `~` and the name of the class. The destructor of an object runs
when its last reference is released, before its fields are released
([Destruction](17-memory.md#memory.release.destruction)).

- It is a compile-time error if a destructor has a name other than the class's, parameters, a visibility, or
  a modifier.
- It is a compile-time error if a class declares more than one destructor.
- It is a compile-time error if an enum or an interface declares one. A struct declares one only if it is
  unique ([Destructors](07-structs.md#structs.unique.destructors)).
- Destruction starts at the object's own class. Each class in turn runs its destructor, releases the fields
  it declares in reverse order of declaration, and then the destructor of its base class runs. In a
  destructor, the object is an instance of the class whose destructor runs
  ([Order of construction](#classes.construction)).
- The destructor may use `this`. It panics if a reference to the object still exists when the destructor
  returns: it may not store the object anywhere.
- A panic in a destructor ends the program as any panic does.

**Example:**

```java
class Box {
    ~Crate() { }                        // error[WYL0147]
}
```

<a id="classes.equality"></a>
## 8.9 Equality

`==` and `!=` on class and interface types compare identity: `a == b` is true when both refer to the same
object, or both are `null`. It is a compile-time error to compare two types when neither converts to the
other ([Comparison](13-expressions.md#expressions.comparison)). Equality of the contents of two objects is
a method the class declares.
