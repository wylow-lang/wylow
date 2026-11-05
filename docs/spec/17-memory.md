<a id="memory"></a>
# 17. Memory and ownership

A value is stored inline in the variable that holds it, in an object on the heap, or as a reference
or view to storage elsewhere. This chapter gives the kinds of storage, how values move, when and in
which order they are released, how objects are kept and their cycles collected, and the rules of
weak references, references, holds, borrows, and second-class values.

<a id="memory.storage"></a>
## 17.1 Kinds of storage

Every value is held in one of three ways:

| Kind | Held | Released by |
|---|---|---|
| A value type's value | Inline in the variable, field, or element that holds it | That variable, field, or element |
| An object | On the heap, referred to by class references | The last reference to it |
| A reference or view | A local variable or parameter that refers to storage elsewhere | — |

- The value types are the primitive types, structs, enums, fixed arrays, nullable value types,
  function types, `String`, and `Weak<T>` ([Kinds of types](04-types.md#types.kinds)). Assigning
  one copies it, or moves it if it is unique ([Moves](#memory.moves)); releasing it releases its
  fields. Only a unique struct has a destructor
  ([Destructors](07-structs.md#structs.unique.destructors)).
- An object is an instance of a class, made with `new`. A variable of a class or interface type
  holds a reference to an object, and copying the variable copies the reference
  ([Objects](#memory.objects)).
- A reference (`ref`, `in`, `ref?`, `in?`) refers to a place, and a view (a span or another
  `ref struct`) refers to storage elsewhere. Both are second-class: they may be only a local
  variable or a parameter ([References](#memory.references),
  [Second-class values](#memory.second-class)).

<a id="memory.moves"></a>
## 17.2 Moves

A value of a type that is not copyable is **moved** where a copyable value would be copied:
initializing or assigning a variable, passing a by-value argument or a by-value receiver,
a `return`, storing into a field or element, an escaping lambda's capture, the operand of `try`, and
a binding by value in a pattern ([Copies](07-structs.md#structs.copies),
[By-value receivers](07-structs.md#structs.methods.by-value), [`try`](13-expressions.md#expressions.try),
[Moving bindings](15-patterns.md#patterns.bindings.moves)). A move gives the destination the value's
contents, and the source holds no value afterwards.

- Only a whole local, a by-value parameter, or a temporary is moved. A field, an element, and what
  a reference refers to are not moved out of. It is a compile-time error to use one by value if its
  type is not copyable. `replace`, `swap`, and `take` exchange what such a place holds
  ([`replace`, `swap`, and `take`](a-library.md#library.replace)).
- It is a compile-time error to use a variable after its value is moved
  ([Use after move](16-flow.md#flow.moves)).
- A moved variable is not released. A unique local or parameter is released at the end of its
  scope only if it holds a value then, and assigning to a moved variable stores a value without
  releasing anything.
- A unique operand is used in place, never copied at its turn. When it is passed to an `in`
  parameter or used as a receiver that is not taken by value, it is referred to the way a `ref`
  argument is, so a call in another operand sees and may change the same value: in
  `mix(buffer, next())`, `mix` sees what `next` left in `buffer`. A unique operand moved into a
  by-value parameter is moved at its turn
  ([Evaluation order](13-expressions.md#expressions.order)).

<a id="memory.objects"></a>
## 17.3 Objects and reference counting

An object is an instance of a class, made with `new`
([Construction](13-expressions.md#expressions.construction)). A class reference to it keeps it
alive; copying the reference copies the reference, not the object.

- An object is destroyed when its last reference is released. Its destructor runs first, and then
  its fields are released ([Destructors](08-classes.md#classes.destructors),
  [Order of destruction](#memory.release.destruction)).
- An object is kept alive by each reference to it: one in a variable, a parameter, a field, an
  element, or a capture of a closure, or a hold that keeps it alive while something refers into it
  ([Holds](#memory.holds)).
- An escaping function value has an object of its own, holding what its lambda captured.
  Releasing its last reference releases what it captured
  ([Captures](12-functions.md#functions.lambdas.captures)).
- An object can be referred to weakly, without keeping it alive ([Weak references](#memory.weak)).
- Objects that refer to each other can keep each other alive. Such a cycle is freed only by a
  collection the program asks for ([Cycles](#memory.cycles)).
- A class is never unique; its objects are shared
  ([Unique structs](07-structs.md#structs.unique)).

<a id="memory.release"></a>
## 17.4 When values are released

A value is released when the variable holding it is assigned a new value, goes out of scope, or is
otherwise destroyed, and when a container removes it or is released. Releasing a value runs its
destructor if its type has one, and then releases its fields.

- An assignment stores the new value before releasing the old one, so a destructor triggered by an
  assignment sees the new value in the place that was assigned. Assigning a struct stores every
  field before releasing any old field.
- A local or parameter releases its value when it goes out of scope, and a temporary at the end of
  its statement ([Temporaries](#memory.release.temporaries)).
- A class's object releases the fields it declares after its destructor has run
  ([Destructors](08-classes.md#classes.destructors)).
- A value that was moved is not released ([Moves](#memory.moves)).
- A static is never released ([Statics](06-variables.md#variables.statics)).

<a id="memory.release.temporaries"></a>
### 17.4.1 Temporaries

A **temporary** is a value an expression computes that no variable holds
([Terms](01-introduction.md#introduction.terms)). A temporary is released when the statement that
created it completes, not at its last use. Within a statement:

- Temporaries are released in the reverse of the order in which they were created.
- An assignment releases the old value first, and then the statement's temporaries: in
  `x = f(g())`, the old value of `x` is released before the result of `g()`.
- A temporary created in an operand that may not be evaluated (the right side of `&&`, `||`, or
  `??`, a branch of `?:`, a case of a switch expression, or a call after `?.`) is released when
  that operand completes.
- Each evaluation of an `if` or `while` condition, or of a `for` loop's condition or update, is a
  statement of its own, so its temporaries are released before the body or the next step runs. A
  `switch`'s matched value, what an `is ref` binding holds, and what a moving binding leaves of a
  temporary last longer ([Holds](#memory.holds),
  [Moving bindings](15-patterns.md#patterns.bindings.moves)).
- In a `return`, the statement's temporaries are released after the value is computed and before
  the function's locals.
- Each argument of `super(...)` is a statement of its own, so its temporaries are released before
  the next argument is evaluated and before the base constructor runs. The values passed are
  released when the base constructor returns, the last first.

**Example:**

```java
class Token {
    String word() { return "token"; }
    ~Token() { print("released"); }
}

Token make() { return new Token(); }

void main() {
    print(make().word());           // prints "token", and then "released"
}
```

<a id="memory.release.destruction"></a>
### 17.4.2 Order of destruction

- The locals and parameters of a scope are released at its end, in the reverse of the order in
  which they were declared.
- A class's object is destroyed starting with its own class. Each class in turn runs its destructor,
  releases the fields it declares in reverse order of declaration, and then the destructor of its
  base class runs ([Destructors](08-classes.md#classes.destructors)).
- A struct releases its fields in reverse order of declaration, after its destructor if it has one
  ([Destructors](07-structs.md#structs.unique.destructors)).
- An array, fixed (`T[N]`) or on the heap (`T[]`), releases its elements from the last to the
  first.
- A closure releases the copies it captured in the order in which they are held, and a choice
  releases the callables it holds in that order.
- A temporary follows the rules of [Temporaries](#memory.release.temporaries).
- Releasing a value that holds the last reference to an object destroys that object inside the
  release, so the stack holds one nested release for each object of a chain, and a long enough chain
  exhausts it ([Aborts](01-introduction.md#introduction.aborts)). A destructor that detaches the
  rest of a chain and releases it in a loop does not nest.

<a id="memory.weak"></a>
## 17.5 Weak references

`Weak<T>` refers to an object without keeping it alive. It is a value type of the standard
library, so it can be a field, a local, a parameter, or an element.

- `T` is a class or interface type, written without `?`. It is a compile-time error to write a
  nullable type or any other type as the type argument of `Weak`, including a type parameter, which
  may stand for a struct.
- A `T` converts implicitly to `Weak<T>` ([Weak conversions](05-conversions.md#conversions.weak)).
  The default value of `Weak<T>` refers to nothing.
- `get()` returns a `T?`: the object while it exists, and `null` afterwards. A weak reference has
  no `==`; what `get()` returns is compared instead.
- A weak reference reads as `null` from the moment its target's last reference is released, before
  the target's destructor runs, whether the target was released or freed as part of a cycle. A
  weak reference to the object taken by its own destructor reads `null` too.
- `Weak<T>` is sendable only when `T` is a `shared class`
  ([Sendable types](19-concurrency.md#concurrency.sendable)).
- There is no `weak` modifier: `Weak<T>` is the only kind of weak reference.

**Example:**

```java
import std::{Weak, print};

class Node {
    pub mut Weak<Node> parent;
}

void main() {
    var child = new Node();
    var parent = new Node();
    child.parent = parent;
    if (child.parent.get() is Node node) {
        print("alive");
    }
}
```

<a id="memory.cycles"></a>
## 17.6 Cycles

Objects that refer to each other keep each other alive, so the last reference to each is never
released and none of them is destroyed. Such a **cycle** is freed by `memory::collectCycles`, which
runs only when the program calls it.

- The collector never runs on its own: no allocation, release, or timer triggers it, and the
  program does not collect at the end of the run
  ([Running a program](03-programs.md#programs.execution)).
- A thread other than the main one frees the cycles left in its buffer when that thread ends.
- `memory::collectCycles()` frees every cycle that is unreachable.
  `memory::collectCycles(int maxVisits)` visits at most about `maxVisits` objects and leaves the
  rest for a later call; a negative number panics.
- A call visits the same objects on every run of one build, on every machine. Which objects a call
  with a budget visits is implementation-defined
  ([Implementation-defined behavior](01-introduction.md#introduction.implementation-defined)).
- Each thread collects only its own objects. An isolated object graph has a buffer of its own
  ([Isolated object graphs](19-concurrency.md#concurrency.isolated)).
- Collection is always safe: an object that running code can still reach is never freed, so an
  object in use has a reference from outside any cycle it is in.

<a id="memory.cycles.collected"></a>
### 17.6.1 What the collector looks at

A class is **cyclic** if an object of it can reach an object of its own class through strong
references in fields, including through structs, enums, fixed arrays, and container elements. Every
class that a field's type could refer to counts: its subclasses, and the implementations of an
interface.

- An escaping function value holds an object with copies of what its lambda captured. A field or
  an element of a function type can reach what any lambda of that type captures, and each closure
  is decided like a class.
- A generic class is decided for each instantiation: `List<Node>` may be cyclic, and `List<int>` is
  not.
- The elements of the standard library's containers, `List` and `Array`, are the references they
  hold. A class of the program is looked at through its fields, even if it is `Spannable`.
- Objects of a class that is not cyclic are never buffered or visited, so the references they hold
  count as references from outside any cycle.
- When a reference to a cyclic object is released and the object is not destroyed, the object is
  added to its thread's buffer, once until the next collection.
- `@Acyclic` on a class declares that its objects never form cycles, so they are never buffered
  ([Annotations](22-annotations.md#annotations.list)). If a class marked `@Acyclic` does form a
  cycle, the cycle leaks; the program does not otherwise misbehave.
- Objects of a `shared class` are not collected, since other threads may refer to them
  ([Shared classes](19-concurrency.md#concurrency.shared-classes)).

<a id="memory.cycles.destroying"></a>
### 17.6.2 Destroying a cycle

A collection that frees a cycle does this, in order:

1. Every weak reference to every object in the cycle is cleared.
2. Every object's destructor runs, oldest object first, while all their fields are intact, so each
   destructor sees the rest of the cycle undestroyed. The destructors of every cycle that one call
   frees interleave in this way. For objects created 2^32 or more objects apart, the order is
   fixed, but it may not follow age.
3. The fields are released and the memory is freed. The standard library's containers release
   their elements here, with the fields, so their destructors run after every destructor of the
   cycle.

- A destructor that stores a reference to its object, or to another object of the cycle, outside
  the cycle panics ([Destructors](08-classes.md#classes.destructors)).
- Calling `memory::collectCycles` from a destructor panics.

<a id="memory.cycles.diagnostics"></a>
### 17.6.3 Diagnostics

A debug build writes performance warnings to standard error
([Build modes](01-introduction.md#introduction.build-modes)):

- when a thread's buffer grows on five consecutive collections;
- when the collector has freed objects of a class in ten collections;
- at the end of the run, about the cycles that were never freed, by class.

Each warning is written once per program. A generic class is named with its type arguments where
they are known. The exact wording of a warning is not specified.

<a id="memory.references"></a>
## 17.7 References

A reference refers to a place: `ref T` to a mutable place, and `in T` to a read-only one. A place
is a variable, a field, an element, or what another reference refers to
([Terms](01-introduction.md#introduction.terms)). Assigning to a reference assigns to the place it
refers to, and a reference refers to the same place for its whole scope.

- It is a compile-time error to refer to a temporary value, or to give a reference a type other
  than the type of the place it refers to. How a `ref` or `in` variable is declared, and how a
  parameter takes one, is in [`ref` and `in` variables](06-variables.md#variables.references) and
  [Parameter modes](12-functions.md#functions.parameters.modes).
- It is a compile-time error to bind `ref` to a read-only place; `in` takes a place that is
  mutable or read-only.
- It is a compile-time error for a `ref` or `in` type to name a function type.
- It is a compile-time error for a field to be a reference.
- It is a compile-time error for a `ref` or `ref?` variable to refer to a nullable local, which a
  null check could narrow and the reference could then set to null. An `in` or `in?` variable is
  read-only, so it may refer to one.
- It is a compile-time error for a `ref`, `in`, `ref?`, or `in?` variable, or a reference binding,
  to refer into the value of a nullable local that can be assigned again: assigning the local null
  would destroy the value. A nullable local that is not `mut` never changes, so references into its
  value may be kept
  ([Narrowing](16-flow.md#flow.narrowing), [Nullable references](#memory.nullable-references)).

A reference is second-class ([Second-class values](#memory.second-class)). It may be a method
parameter, a local variable, a loop variable, or a pattern binding; it is never a field, a
container or array element, a type argument, or a capture of an escaping lambda. A method returns
a reference into its receiver ([Returning references](12-functions.md#functions.ref-returns)). A
reference holds what it refers to while it is used ([Holds](#memory.holds)), and borrows it where
it is a container ([Borrows](#memory.borrows)).

**Example:**

```java
struct Point { pub int x; }

void main() {
    mut Point point = Point(1);
    ref int x = point.x;
    x = 2;
    print(point.x);
    mut Point? maybe = point;
    if (maybe != null) {
        ref int y = maybe.x;            // error[WYL0406]
    }
}
```

<a id="memory.nullable-references"></a>
## 17.8 Nullable references

`ref? T` and `in? T` are references that may refer to nothing, for a lookup that can fail without
copying what it finds.

- A `ref? T` is a reference that may refer to nothing. It is not a `ref T?`, which is a reference
  to a variable of type `T?`. The two combine: `ref? int?` may refer to an `int?` field or element.
- It is allowed where a `ref` is (parameters, locals, pattern bindings, and returns into the
  receiver), and follows the same rules: it is second-class, and what it refers into is held or
  borrowed while it is used.
- It converts implicitly as [Reference conversions](05-conversions.md#conversions.references)
  gives: `ref` to `in`, `ref` and `in` to their nullable forms, and `null` to `ref?` or `in?`.
  Nothing converts in the other direction.
- A `ref?` is initialized or passed like a `ref`, or with `null`: `ref? T x = place`,
  `ref? T x = null`, and `ref? var x = list.get(i)`. A `ref?` parameter takes `ref place`, another
  `ref?`, or `null`, which needs no `ref`, and may default to `null`. An `in?` parameter takes what
  an `in` one takes, including a temporary value, as well as `null` and any `ref?` or `in?`.
- It is used as a place only after a check: `x is ref T name` (or `x is in T name`) binds the
  place, and `x != null` narrows a `ref?` local or parameter. Narrowing is always sound here, since
  a reference cannot be made to refer elsewhere: assigning to it assigns to the place. A null
  check outside a lambda therefore narrows a `ref?` inside it too. `x!` gives the place or panics.
- Where it is narrowed, it is used as the place: `found.quantity -= 10`, `found = Point(...)` (which
  assigns the place), or passed by `ref`.
- `x?.member` reaches the member in the place referred to, not in a copy, so
  `list.get(i)?.reset()` changes the element. What the place lies in is held or borrowed
  until the member is done.
- It is not a value: `var x = list.get(i)` is a compile-time error (write `ref? var`, or copy with
  `!`), and so are `??` on one, a `?:` or switch expression that produces one, and using one as a
  type argument. Overloads cannot differ only in `ref` and `ref?`
  ([Overloading](12-functions.md#functions.overloading)).
- `x is ref T name` is an `is` test, not a case of a switch. The name is in scope like any
  pattern's ([Reference bindings](15-patterns.md#patterns.references)).
- It is a compile-time error to use a `ref?` or `in?` as a value where it might refer to nothing.
  Check it with `x != null`, `x!`, or `is ref`.

A place in the value of a narrowed nullable local is used while an operation runs. Assigning the
local null meanwhile would destroy that place, so it is a compile-time error to pass the local by
`ref`, or to run a lambda that may assign it, in the same operation that uses the place.

**Example:**

```java
void bump(ref? int value) {
    print(value + 1);               // error[WYL0412]
    if (value != null) {
        value += 1;
    }
}
```

<a id="memory.second-class"></a>
## 17.9 Second-class values

References, `ref struct`s (spans among them), and non-escaping functions are **second-class
values**: each may point into storage it does not own, so it must not outlive that storage. A
second-class value may be only a local variable or a parameter, bound once where it is declared
and never made to refer to or hold something else.

- It is a compile-time error for a second-class value to be a field, a variant field, an array or
  container element, or a type argument.
- It is a compile-time error for an escaping lambda to capture a second-class value.
- It is a compile-time error to declare a second-class variable without an initializer.
- A method returns a reference or a `ref struct` only as the rules for its receiver allow
  ([Returning references](12-functions.md#functions.ref-returns),
  [`ref struct`s](07-structs.md#structs.ref)); a non-escaping function is never returned
  ([Escaping and non-escaping](12-functions.md#functions.lambdas.escaping)).

<a id="memory.holds"></a>
## 17.10 Holds

A reference may point into storage that the function does not own, so the storage must stay alive
while the reference is used. A **hold** is a reference that keeps an object alive, and a
**borrow** keeps a container's storage where it is ([Borrows](#memory.borrows)).

- Taking a reference into a field of a class object holds that object while the reference is in
  scope, so the object is not destroyed and the field stays where it is.
- The receiver of a method call is held during the call, so the method may replace the field that
  holds the object it runs on.
- A `ref`, `in`, `ref?`, or `in?` variable, and a span variable, holds the object it refers into
  for its whole scope, even while a local also holds it, since the function may reassign that
  local.
- An object that `this` holds, or that a field of `this` that is not `mut` holds outside its
  class's constructors, needs no hold.
- What an expression refers into is held until the statement ends.
- A stored function value is held during its call, so the call may replace the field or element
  that holds it.
- An `is ref` or `is in` test that succeeds holds what the name refers into until the statement
  that contains the test ends, even where the rest of the condition is false. In a `while`
  condition the hold lasts until that iteration's body ends, and in a `when` guard until the
  `switch` ends, through the cases after it. When the test fails, nothing is held.
- The holds that a statement adds are released as its temporaries are, in the reverse of the order
  in which they were made.

A value of a unique struct type that has a destructor is **in use** while anything refers to the
place holding it: from the start of a call of one of its methods, or of a call given a `ref` or
`in` argument that refers to it, until that call returns; for the scope of a `ref` or `in`
variable or pattern binding that refers to it; and for the body of its destructor. A call on
`this`, or on a `ref` or `in` parameter, while the value is already in use is part of the same use.

A value of a unique enum, or a value of a nullable type whose value type is not copyable, is **in
use** while anything refers into what it holds: for the scope of a reference binding into its variant
or value, and until a call returns when it is reached through `!` or `?.` as a receiver or as a `ref`
or `in` argument. Each such value that the reference passes through is in use. A reference to the
whole value does not make it in use, since the place stays and shows the new value.

It panics to release a value that is in use: by assigning to the place that holds it, by removing
it from a container, or by any other release. `replace`, `swap`, and `take` panic on a value that is
in use, or that has one in use among its fields, before they change anything
([`replace`, `swap`, and `take`](a-library.md#library.replace)). A move of any other kind releases
nothing and does not panic.

**Example:**

```java
class Order {
    pub mut int quantity;
}

void main() {
    mut var order = new Order();
    ref int count = order.quantity;
    order = new Order();
    count = 5;
    print(order.quantity);          // 0: 'count' refers into the first order
}
```

<a id="memory.borrows"></a>
## 17.11 Borrows

A container that can move or free its elements is **borrowed** while a `ref`, `in`, or span into
it is used. Taking one borrows the container, and the borrow ends when the reference or view is no
longer used.

- It panics to change the length or capacity of a container while it is borrowed, or to free its
  storage. Which operations of a container do this is in its documentation.
- A read by value, or a store of a value that runs no code, needs no borrow.
- A borrow starts when a reference or span into the container is taken from one of its methods, an
  indexer or `asSpan`, and lasts:

| Where the reference or span is | Borrowed until |
|---|---|
| A `ref`, `in`, or span variable | The end of the variable's scope |
| A for-each loop over the container | The end of the loop |
| A `ref` or `in` argument, a span argument, or a struct method's receiver | The end of the statement |
| An assignment's target, when the value runs code: `list[0] = f()` | The end of the assignment |

- Destroying a borrowed container cannot happen: a borrow also holds the container.

<a id="memory.borrows.borrowable"></a>
### 17.11.1 `Borrowable`

`Borrowable` is an interface of the standard library ([The names](a-library.md#library.names)). A
class that implements it has `beginBorrow` called when a borrow of it starts and `endBorrow` when
the borrow ends, including when the object is known only through an interface.

- It is a compile-time error for a struct or an enum to implement `Borrowable`, or an interface
  that extends it. Only a class can be `Borrowable`: a value is borrowed through whatever holds
  it.
- It is a compile-time error to call `beginBorrow` or `endBorrow` directly: the implementation
  calls them around every borrow.
