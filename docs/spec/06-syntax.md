# 6. Syntax

VPL syntax is Java's unless stated otherwise. This chapter covers the differences and additions.

## 6.1 Files, packages, imports

```java
package game.combat;

import game.math.Vec3;
import game.math.*;                      // everything in a package
import game.physics.Body as PhysBody;    // alias

public struct Hit { ... }
public class Weapon { ... }
float damageFalloff(float d) { ... }     // top-level function
```

- **Package = directory.** The `package` declaration must match the file's directory.
- **Any number of types per file**, public or not. (Java's one-public-class-per-file rule does not apply.)
- **Top-level functions and constants are allowed.** They belong to the file's package and are
  called unqualified after import, or qualified (`math.lerp(...)`).
- **No declaration order and no headers.** Any declaration may reference any other, including across
  circular imports. The compiler emits C++ forward declarations itself.

### Entry point

A top-level `void main()` or `int main(String[] args)`.

## 6.2 Access modifiers

Java's four levels:

| Modifier | Visible to |
|---|---|
| `public` | Everyone |
| `protected` | The package and subclasses |
| *(none)* | The package |
| `private` | The enclosing type (or file, for top-level declarations) |

## 6.3 Construction

> **`new` appears only where memory is heap-allocated.** Structs are constructed without `new`.

```java
var v = Vec3(1, 2, 3);                         // struct constructor, no allocation
var e = Enemy(health: 50, position: v);        // fields by name
var list = new List<Enemy>();                  // class: heap allocation
var arr = new float[1024];                     // heap array
```

This makes every allocation visible: scanning for `new` finds them all.

A local must be assigned before it is read, even when its type has a default value, so that a
branch that forgets to assign it is an error rather than a silent zero. `var` keeps this short where
the type is visible, as in `var v = Vec3();`; write the type out where the value doesn't show it, as
in `Vec3 target = path.last();`.

### Struct constructors

A struct is built one way: by calling a constructor. There is no separate initializer syntax such
as C#'s `T { field = value }`; the memberwise constructor with named arguments does that job, so
default values, positional construction, and setting fields by name are all the same call.

- A struct that declares no constructor gets a **memberwise constructor**, which takes every field
  in declaration order: `Vec2(1, 2)` for `struct Vec2 { float x, y; }`. Each parameter defaults to
  its field's initializer, or else to the field type's default value, so arguments may be left out
  or passed by name (§6.4):

  ```java
  struct Enemy {
      Vec3 position;
      int health = 100;
      Player? target;
  }

  Enemy()                    // every field defaulted
  Enemy(pos)                 // health 100, no target
  Enemy(health: 50)          // position defaulted
  ```

  A field with no initializer whose type has no default (a non-null reference, a type parameter, or
  a struct containing one) is a required argument. The memberwise constructor is private if any
  field is.
- Arguments are evaluated in the order written, as in any call. Then each field is set, in
  declaration order, to its argument or, for one left out, to its initializer. Initializers may call
  functions but cannot use `this` or other fields.
- **`T()` is the default value.** It exists for a struct whose fields all have defaults, including
  one that declares constructors, since arrays and pools rely on it, and it cannot be declared.
- Declaring any constructor removes the memberwise one, apart from `T()`. A declared constructor
  starts from the default value, then runs its body. `this` refers to the value being built, and
  `return;` ends construction early. To set fields its parameters don't cover, assign them after the
  call: `mut var e = Enemy(pos); e.health = 50;`.
- Constructors are declared Java-style:

```java
struct Enemy {
    Vec3 position;
    int health = 100;

    Enemy(Vec3 pos) { position = pos; }
}
```

### Class constructors

Java-style, including `super(...)` and `this(...)` chaining:

```java
class Shotgun extends Weapon {
    int pellets;
    Shotgun(int pellets) { super(1.0); this.pellets = pellets; }
}
```

Classes are always created with `new`, and structs never are. The order of construction and what a
constructor may do before its fields are assigned are in
[02-types.md](02-types.md#construction). `this(...)` chaining is not implemented yet.

## 6.4 Methods and parameters

### Parameter modes

| Mode | Meaning | Call site |
|---|---|---|
| `T x` | By value (structs are copied) | `f(x)` |
| `ref T x` | Mutable reference | `f(ref x)`; the `ref` keyword is **required** |
| `in T x` | Read-only reference (no copy, no mutation) | `f(x)` |

```java
void applyDamage(ref Enemy e, in DamageInfo info) { e.health -= info.amount; }

applyDamage(ref enemy, info);
```

`ref` and `in` parameters follow the reference rules in
[03-memory-model.md](03-memory-model.md#34-references). `in` is intended for large read-only structs
such as matrices. On class-typed parameters, `ref`/`in` are not meaningful (a class is already passed
by reference) and are a compile error.

- A `ref` argument must be a mutable place: a `mut` local or parameter, a field path rooted at
  one (`ref enemy.position`), an element (`ref points[i]`), or a reference a method returned. Its
  type must match the parameter exactly; no conversions apply.
- A place is read-only, and cannot be assigned or passed by `ref`, if it is rooted at an `in`
  parameter or variable, a variable, parameter, or field that isn't `mut` (§2.7), `this` inside a
  `readonly` method, or a call that returns `in`.
- Arguments are evaluated left to right, and a `ref` argument is a reference to the place, not a
  snapshot of its value: in `x + increment(ref x)`, the left `x` is read before the call.

### `readonly` struct methods

In a struct method, `this` is a `ref`, so methods may mutate the struct. A method marked `readonly`
cannot mutate `this`. Interface methods may be `readonly` too
([02-types.md](02-types.md#212-interfaces)). Only `readonly` methods (and properties) can be called on a read-only place:
through an `in` parameter or a `ReadOnlySpan<T>`, or on a variable that isn't `mut`.

```java
struct Vec3 {
    float x, y, z;

    readonly float length() { return sqrt(x*x + y*y + z*z); }
    void normalize() { var l = length(); x /= l; y /= l; z /= l; }
}
```

### Overloading, defaults, named arguments

```java
void spawn(Vec3 pos, int count = 1, float spread = 0.5) { ... }

spawn(pos);
spawn(pos, 5);
spawn(pos, spread: 2.0);     // named argument
spawn(pos, count: 3, spread: 2.0);
```

- Methods may be **overloaded** by parameter types, as in Java.
- Parameters may have **default values**: a constant (a literal, an enum variant, or an operator
  applied to constants) or a struct constructor given constants, as in `Vec3 up = Vec3(0, 1, 0)`.
  A call that leaves the parameter out passes its default, evaluated at the call.
  - A `ref` parameter cannot have a default, since it must refer to a variable.
  - In generic code, a parameter whose type involves a type parameter can only default to `null`.
  - A struct constructor cannot give every parameter a default, since `T()` is always the
    struct's default value (§6.3).
- Arguments may be passed **by name**, as in C#. Positional arguments come first. Named ones follow
  **in the order of the parameters**, as in Swift, and may skip parameters that have defaults:
  `spawn(pos, spread: 2.0, count: 3)` is an error. So the order an argument list is written in is
  the order its values are evaluated in: each parameter's argument, or its default if left out.
  A struct's memberwise constructor takes both, which is how a struct is built with some fields
  named (§6.3).
- Overload resolution considers the overloads that can take the arguments by count and name, then
  compares the parameters that are given arguments. Among equally good overloads, one that leaves
  fewer parameters to their defaults wins, as in C#, then a non-generic one.
- A method that overrides or implements another takes that method's defaults and cannot declare
  its own, as in Kotlin, so a call passes the same values whatever type it is made through. Named
  arguments use the parameter names of the method the call finds, in the type it is made through.
- A function type's parameters have no names or defaults, so a function value is called with every
  argument, positionally. A method used as a function value takes all its parameters.

### Extension methods

A function whose first parameter is marked `this` can be called like a method of that parameter's
type, as in C#. It adds methods to types its author can't change, and to interfaces, which can't
have method bodies or generic methods themselves (§2.12).

```java
bool any<T>(this ReadOnlySpan<T> items, (T) -> bool test) {
    for (in T item : items) {
        if (test(item)) {
            return true;
        }
    }
    return false;
}

void scale(this ref Vec3 v, float factor) { v.x *= factor; v.y *= factor; v.z *= factor; }

if (mobs.any(m -> m.health <= 0)) { ... }
position.scale(2);
```

- The receiver takes a parameter mode: `this T` (a copy, or a reference for a class), `this in T`,
  or `this ref T`, which needs a mutable place, as calling a mutating struct method does, but no
  `ref` at the call. `this ref` and `this in` can't be used with class types, as for parameters.
- **Members win.** If the receiver's type has a method, field, or property of that name, its
  extensions are not considered, so an extension can never change what existing code calls. Among
  extensions, overload resolution chooses as for functions.
- The receiver converts to the `this` parameter's type by identity, upcasts, and the implicit span
  conversions (§2.11), so one extension on `ReadOnlySpan<T>` serves fixed arrays, `T[]`, `List`,
  and spans. No other conversions apply. Type arguments are inferred from the receiver as from any
  argument.
- A nullable receiver needs `?.` or a check, as for members, unless the `this` parameter's type
  is itself nullable: `bool isNullOrEmpty(this String? s)` can be called on a `String?` directly.
- An extension is an ordinary function: it sees only the receiver's public members, is statically
  dispatched (never virtual), and can also be called as `any(mobs, test)`.
- Once packages exist, an extension is found only where its package is imported. Until then, every
  extension in the program and the standard library is visible.

> Not implemented yet.

## 6.5 Properties

C#-style properties:

```java
class Player {
    int health { get; private set; } = 100;     // auto-property with initializer
    bool isDead { get -> health <= 0; }         // computed, read-only

    float speed {
        get -> _speed;
        set { _speed = clamp(value, 0, MAX_SPEED); }   // `value` is the assigned value
    }
    private float _speed;
}
```

- Accessors may have their own access modifier (`private set`).
- On structs, a `get` accessor that doesn't mutate may be marked `readonly`:
  `readonly bool isDead { get -> health <= 0; }`.
- Simple accessors are inlined by the C++ compiler and cost nothing.

> Implemented so far: get-only properties, `T name { get -> value; }` or `T name { get { ... } }`,
> and in interfaces and abstract classes `T name { get; }`. A property is read without parentheses
> and cannot be assigned. Auto-properties, `set`, and static properties are not implemented.

## 6.6 Operator overloading

Operators are `static` methods named `operator <symbol>`, except indexers, which are instance
methods (below):

```java
struct Vec3 {
    float x, y, z;

    static Vec3 operator +(Vec3 a, Vec3 b) { return Vec3(a.x + b.x, a.y + b.y, a.z + b.z); }
    static Vec3 operator *(Vec3 v, float s) { return Vec3(v.x * s, v.y * s, v.z * s); }
    static Vec3 operator *(float s, Vec3 v) { return v * s; }
    static Vec3 operator -(Vec3 v)          { return Vec3(-v.x, -v.y, -v.z); }   // unary
}
```

| Category | Operators |
|---|---|
| Overloadable | `+ - * / %`, unary `- ! ~`, `& \| ^ << >>`, `== != < > <= >=`, `[]` and `[]=` (indexers) |
| Not overloadable | `&& \|\| = . ?? ?.`, casts, `new` |

- **Compound assignment** (`+=`, `*=`, ...) is derived automatically from the binary operator.
- Defining `==` derives `!=` (it may be defined explicitly instead).

### Indexers

```java
struct Mat4 {
    float[16] m;
    ref float operator [](int row, int col) { return ref m[row * 4 + col]; }
}
```

An indexer is an instance method named `operator []`, as in C++, so `m[1, 2]` calls
`m.operator [](1, 2)`. One returning `ref` gives mutable access to an element (see the `ref` return
rule in [03-memory-model.md](03-memory-model.md#returning-references)), and one returning `in`
gives read-only access.

Where there is no element to refer to, such as in a packed array of 4-bit values, `operator []=`
handles assignment: it takes the indices and then the assigned value, and returns `void`.
`grid[i] = v` calls it, and `grid[i] += v` reads with `operator []` and writes with `operator []=`.
A type may not declare both `operator []=` and an `operator []` that returns `ref`.

```java
struct Nibbles {
    byte[2048] bytes;
    byte operator [](int i) { return bytes[i >> 1] >> ((i & 1) * 4) & 0xF; }
    void operator []=(int i, byte value) { ... }
}
```

> Implemented so far: `operator []` returning `ref`, `in`, or a value, with any number of
> parameters. `operator []=` is not implemented.

### Equality

| Kind | `==` means |
|---|---|
| Struct | Memberwise equality, **generated automatically** if every field supports `==` (can be overridden) |
| Class, interface | **Reference identity** (same object), as in Java. Value equality is `equals()`. Allowed only when one side could be the other (or is `null`) |
| Enum | Same variant and equal data |
| `String` | **Same contents** (the same bytes), as in C#, unlike Java's identity: a literal equals a string built at run time with the same text |

## 6.7 Lambdas and function types

Lambda syntax is Java's. Function types are written `(Params) -> Return`:

```java
(int, int) -> int add = (a, b) -> a + b;
list.sort((a, b) -> a.depth - b.depth);
button.onClick(() -> { play(sound); });
```

- A function type's parameters are always parenthesized, even one: `(Mob) -> bool`. They take
  parameter modes, which are part of the type: `(ref Vec3) -> void`.
- The return type takes any suffix: `(int) -> int?` returns an `int?`. To make the function itself
  nullable, or an array of functions, parenthesize it: `((int) -> int)?`, `((int) -> int)?[]`.
- `->` groups to the right: `(int) -> (int) -> int` returns a function.
- A lambda's parameters are either all untyped, taking their types and modes from the target
  (`x -> ...`, `(a, b) -> ...`), or all typed (`(int a, ref Vec3 v) -> ...`). Its body is an
  expression or a block.
- As in Java, a lambda is never an operand: its body extends as far as an expression can, so
  `x -> x + 1` is a lambda returning `x + 1`.
- A lambda has no type of its own: it takes one from where it's used (a declared variable, field,
  parameter, or return), so `var f = x -> x;` is an error. Passed to overloaded functions, it
  rules out those whose parameter takes a different number of arguments, or other types than it
  declares; overloads that differ only in what an untyped lambda's parameters would be are
  ambiguous.
- A generic function's type arguments are inferred first from its other arguments and from the
  parameter types lambdas declare. Those then give each lambda its parameter types, and a type
  argument still unknown comes from what the lambdas return, each returned value counting like an
  argument: `result.map(m -> m.name)` calls `map<String>`. Lambdas are taken in order, each once
  its parameter types are known, so one lambda's result can type the next one's parameters
  (`compose(2, x -> x * 0.5d, y -> y > 1d)`). Where lambdas disagree, the type they all convert to
  wins, as for arguments: `pick(() -> 1, () -> 2L)` infers `long`. A type argument that a lambda's
  parameters were bound with can't change after, so a later lambda that returns a wider type is an
  error.
- Inferring from a lambda's body needs the parameter types of the one overload being called, so it
  happens only when one overload is left after the other arguments are checked. If others are
  left too, the call is an error: specify the type arguments. A lambda returning nothing infers
  nothing, and `try` can't be used in a lambda whose return type is being inferred.
- A function value is called like a function, whether it's a local, a field (`button.onClick(5)`),
  or the result of a call (`adder(2)(3)`).
- A free function or static method used as a value, where a function type is expected, is a
  **function reference**: the lambda that calls it with its arguments, so `list.sort(Mob.byDepth)`
  passes `(a, b) -> Mob.byDepth(a, b)`. The function type's parameters pick the overload as a call's
  arguments would, and the result converts to its return type, or is discarded if that is `void`.
  Otherwise it is a lambda: it is escaping when its target is, a generic function's type argument
  can be inferred from what it returns, and `?:` can choose it. Passed to overloaded functions, it
  is an untyped lambda taking as many parameters as the function does, so it needs a function type
  from where it is used if the function's overloads take different numbers. An instance method
  can't be used as a value (`enemy::update` may come later); use a lambda that calls it.
- `return` in a lambda returns from the lambda; `break` and `continue` can't leave it.
- `?:` and switch expressions choose between functions, wherever a function can be used:
  `(int) -> int f = up ? x -> x + 1 : x -> x - 1;`, or `sort(items, up ? ascending : descending)`.
  The choice is made once, when the expression is evaluated. Passed as an argument, a choice
  whose options include lambdas waits for its function type as a lambda does: overloads and
  generic inference treat each option as the argument (`apply(3, up ? x -> x * 2 : x -> x + 1L)`
  infers `long`), and an option that is a function-typed local constrains the type like any other
  argument. Anywhere else, a lambda option without a target type takes another option's function
  type. A lambda a switch expression chooses can't use a name its case declares
  (`case Add(var n) -> x -> x + n` is an error), since the lambda is made outside the case; use a
  switch statement.
- A function type has no default value, so a field of one must be assigned by every constructor
  or be nullable. `((int) -> int)?` is a nullable value, like `int?`, narrowed by null checks.
  Function values have no `==`.
- A lambda can use a local only if it is definitely assigned where the lambda is created. A null
  check outside a lambda doesn't narrow inside it, since the code before the lambda runs could
  change the local; and a local that any lambda assigns is never narrowed, since the lambda may
  run during any call.

A function-typed parameter compiles to a C++ template parameter, so each lambda passed to it is
inlined into the function. Where C++ can't use a template, it is instead a non-owning reference
to the lambda, called indirectly: in an overridden virtual method (one that nothing overrides is
an ordinary method), an abstract or interface method, a class's implementation of an interface
method, a recursive function that passes itself a new lambda (or a choice) on some cycle of calls,
which would otherwise instantiate itself without end, a constructor that shares its parameter
count with another of its type, or a function type's own parameters. A recursive function that
only passes its function parameters on, or escaping values, is still a template:
`walk(child, visit)` instantiates the same one again. Overloads that differ only in function types
are told apart by the C++ name. A function chosen by `?:` or a switch expression holds every
function it could be and the index of the chosen one, so calling it branches to a direct call,
and each lambda can still be inlined.

> Implemented so far: everything above.

### Escaping and non-escaping

A function-typed **parameter or local** is **non-escaping** by default: the function may call it
and pass it to other non-escaping parameters, but may not store it, return it, or pass it to an
escaping parameter. `@Escaping` on the parameter or local opts in.

| | Non-escaping | Escaping |
|---|---|---|
| Where | Parameters and locals | Fields, return values, `@Escaping` parameters and locals, containers |
| Allocation | **None** (compiled to an inline template or function reference) | Heap-allocated, refcounted closure |
| Captures | By reference: may capture `ref`s and `Span`s, and may **mutate** captured locals | By copy: captured locals can't be `mut` |

```java
mut int count = 0;
enemies.forEach(e -> { if (e.isDead) count++; });   // non-escaping: mutates local, no allocation

void onEvent(@Escaping () -> void handler) { handlers.add(handler); }

(Mob) -> bool isDead = m -> m.health <= threshold;   // non-escaping: no allocation
@Escaping () -> void onDone = () -> play(sound);      // escaping: a heap closure, can be stored
handlers.add(onDone);
```

The cost of a lambda is visible where it's declared, never inferred from how it's used later: a
non-escaping local stays non-escaping, and storing it is an error that suggests `@Escaping`.
Like a `ref` local, a non-escaping local is bound once, at its declaration, and can't be
reassigned or declared `mut`, so it can't outlive what its lambda captures. An escaping local can
be reassigned if it is `mut`.

- **Escaping is part of the function type.** Each function type has a non-escaping and an
  escaping form, written the same way. Written as the type of a parameter, a local, or a function
  type's own parameter, it is non-escaping unless that is declared `@Escaping`, as in
  `(@Escaping () -> void) -> void`; anywhere else (a field, a return type, a type argument, an
  array element, or a function type's return type) it is escaping. A nullable function follows the
  same rule, except where noted below. `var` takes the form of its initializer. An escaping value
  converts to the non-escaping form, so it can be passed to a non-escaping parameter or bind a
  non-escaping local; nothing converts the other way.
- **A lambda is escaping when its target is**: the variable, field, parameter, or return type it
  is converted to, or the type of the `?:` or switch expression it is an option of. A lambda
  passed as an argument takes the form of the parameter that overload resolution picks.
- **Type arguments are always escaping**, so a type argument inferred from a function is its
  escaping form: `identity(f)` needs `f` to be `@Escaping`.
- **Nullable functions.** A parameter of type `((int) -> int)?` is non-escaping unless declared
  `@Escaping`, so an optional callback is passed without allocating. Like any non-escaping
  parameter it is bound once, and it is narrowed by null checks. Only a parameter can hold one: a
  local of nullable function type must be `@Escaping`, since a non-escaping local owns the lambda
  it holds and a null one holds none. For the same reason a `?:` or switch expression of nullable
  function type is escaping, so its lambdas are closures, and `??` can't be applied to a nullable
  non-escaping function; check it with `if`.
- **Captures.** An escaping lambda copies the locals and parameters it uses when it is created,
  so none may be `mut` (§2.7): the copy and the variable could then differ. Whether a `mut` local
  actually changes doesn't matter; the declaration decides. A local declared without a value and
  assigned once later can be captured, since it must be assigned before the lambda is created.
  Inside the lambda, the copies are read-only. A for-each or range loop's variable is a new
  variable in each iteration, so it can be captured, but the `mut` counter of a
  `for (mut int i = 0; ...; i++)` loop can't; copy it to a local in the body.
- An escaping lambda can't capture a `ref` or `in` local or parameter, a span or other
  `ref struct`, a non-escaping function, or `this` in a struct's method or constructor (copy what it
  needs to a local first). A captured class reference, including `this`, keeps its object alive.
  So a closure can be part of a cycle, as in `this.onHit = () -> react(this);`, which
  `Memory.collectCycles` collects like any other.
- **Choices.** A `?:` or switch expression of escaping type evaluates only the option it chooses,
  like any other. One of non-escaping type makes its lambdas and refers to its non-escaping
  locals up front, which no one can observe, and evaluates an escaping option (a field, a call, an
  escaping local) only when it is chosen.
- **Signatures.** An override or interface implementation must declare the same parameters
  `@Escaping` as the method it overrides or implements. Overloads can't differ only in which
  parameters are `@Escaping`. A function type's `@Escaping` parameters are part of the type, and
  print as written: `(@Escaping () -> void) -> void` and `(() -> void) -> void` are different
  types, and neither converts to the other. A lambda of the first may store its argument.
- A type pattern can't name a function type (`f is (int) -> int g`); narrow a nullable function
  with a null check.

```java
for (Mob m : mobs) {
    handlers.add(() -> m.hit());            // fine: each iteration has its own 'm'
}
mut int total = 0;
@Escaping () -> int f = () -> total;        // error: 'total' is 'mut'
total++;
```

An escaping function is a `vpl::Function`, two words: a pointer to the code that runs the lambda's
body, and a reference to a closure object, with the same header and reference count as a class
object, that holds copies of what its lambda captured. A lambda that captures nothing has no
closure, so making one allocates nothing, and copying it only tests for null; a function reference
captures nothing. Calling a function value is one indirect call. Copying it copies the reference. A
nullable function needs no flag: its null is a null code pointer. The cycle collector
visits the class references a closure holds. A call of a function value stored where the call
could replace it (a field, a list element, a local that a lambda assigns) holds a reference to the
closure until the call returns. A closure that provably can't outlive the scope making it is on the
stack, as a class object can be ([03-memory-model.md](03-memory-model.md#stack-allocation)).

A nullable non-escaping parameter is a `vpl::Optional` of a `vpl::FunctionRef`, made where the call
is, so that what it refers to (a lambda, or a copy of a stored function) lives until the call
returns. A `vpl::FunctionRef` made from a stored function refers to its closure and calls its code,
so the call is no more indirect than the stored function's.

Non-escaping lambdas are what make `Jobs.parallelFor` and `Mutex.lock` safe
([05-concurrency.md](05-concurrency.md)).

> Implemented so far: everything above.

### Lambdas held by `ref struct`s

A `ref struct` may have fields of function type, which hold non-escaping lambdas: the struct is
second-class ([03-memory-model.md](03-memory-model.md#spans)), so it can't outlive the lambda's
captures either. A function may return such a struct holding its own function-typed parameters,
where it may return a `ref struct` at all. This is what lets a lazy query such as
`mobs.where(m -> m.health < 5)` store its lambda without allocating
([02-types.md](02-types.md#queries)).

Each such field is compiled as a C++ template parameter of the struct, so the optimizer can inline
the lambda into the loop that calls it: `Where<T>` in VPL is `Where<T, F>` in C++, one type per
lambda. A `ref struct` can't be reassigned or chosen by `?:`, so a variable never needs to hold two
of them; a function taking one as a parameter is compiled once per lambda, as a generic function
is once per type argument.

> Not implemented yet.

### Costs to remove later

What the implementation above pays that it needn't. None of these changes behavior, so each can
be done when profiling shows it matters.

- **Indirect calls where templates can't be used.** A `vpl::FunctionRef` costs an indirect call
  and keeps the lambda from being inlined. It could be avoided by devirtualizing calls whose
  receiver type is known, and for constructors sharing a parameter count, by telling them apart
  with a tag argument.
- **Code size.** Each distinct lambda passed to a template instantiates the callee again, as
  generics do per type. A performance warning could point out large instantiation counts.
- **Pins for locals a lambda assigns.** Such a local is held while in use as a receiver or
  argument (a reference count increment and decrement) even when no lambda that assigns it can
  run during that call. Only calls passed such a lambda, directly or by forwarding, can change it.
- **Evaluation order.** Any call given a function value, and any call of one, is treated as
  writing every local that a non-escaping lambda captures and that is changed anywhere, so operands
  reading one are hoisted into an ordering lambda, though the lambda the call can run may not change
  it. Tracking which locals each lambda changes, and which lambdas each call can run, would avoid
  that. Each hoisting nests a C++ block, so about 25 levels of nested lambdas exceed MSVC's limit on
  block nesting. Real code doesn't nest that deep, so this is a known limit, not a planned fix.
- **Element aliasing and cycles.** A call of a function value counts as writing what every lambda
  of its type writes, so one `(Mob) -> void` lambda storing into elements of type `X` anywhere
  makes every loop over `X` that calls a `(Mob) -> void` count references per element
  ([03-memory-model.md](03-memory-model.md#loop-elements)); likewise, a function field may hold
  any lambda of its type for the cycle analysis. Both could count only the lambdas that can reach
  that call or field.
- **Narrowing.** A null check outside a lambda doesn't carry into it, and a local declared outside
  any lambda that assigns it is never narrowed, which can force a `!` (and its check).

### Decisions to revisit

*Open*: reasonable on paper; real programs will show whether they get in the way.

- **Bind-once function locals.** A non-escaping local can't be reassigned; `?:` and switch
  expressions cover choosing one.
- **Narrowing.** Carrying a null check on a local that isn't `mut` into a lambda would be sound.
- **`F?` is a nullable value**, not reference-like. Only parameters can be non-escaping `F?`:
  locals, choices, and `??` would need an optional that owns a lambda, which non-escaping locals
  and choices avoid by being bound once or holding every option.
- **Untyped lambdas are ambiguous between overloads** that differ only in function parameter
  types, rather than each overload being tried.
- **Parameter modes are part of a function type**, and an untyped lambda takes them from its
  target; function-typed parameters can't be `ref` or `in`.
- **Expression-bodied `void` lambdas** must have a call as their body, as statements must.
- **Which functions take a `vpl::FunctionRef`**: the rules are simple and conservative, and could
  be wrong in the slow direction often enough to matter.
- **Inferring from a lambda's body needs one overload left.** Two generic overloads whose lambdas
  take the same number of parameters, or a generic and a non-generic one, make
  `x.map(m -> m.name)` an error asking for type arguments. Binding the body once per distinct
  parameter-type list, and sharing it, would lift that.

## 6.8 `defer`

`defer statement;` runs the statement when the enclosing scope exits (normally, or via
`return`/`break`/`continue`). Multiple defers run in reverse order.

```java
void render() {
    gpu.beginPass(pass);
    defer gpu.endPass();
    ...
}
```

Owned resources are released by class destructors; `defer` is for paired calls that aren't objects.

## 6.9 Control flow

All of Java's control flow (`if`, `while`, `do`/`while`, C-style `for`, for-each, labeled
`break`/`continue`, `?:`), with these changes and additions.

### Assignment is a statement

`=`, compound assignments (`+=` etc.), `++`, and `--` are statements, not expressions. They
cannot appear inside other expressions, so `a = b = c`, `f(x = 1)`, and `arr[i++]` are compile
errors. This removes a class of evaluation-order bugs. `++` and `--` may be written prefix or
postfix with the same meaning.

### Evaluation order

Operands and arguments are evaluated left to right. This includes reads of object fields relative
to calls; see [03-memory-model.md](03-memory-model.md#evaluation-order-and-the-heap).

### Mandatory braces

Bodies of `if`, `else`, `for`, `while`, and `do` must be blocks. `else if` is allowed.

```java
if (x) foo();        // compile error
if (x) { foo(); }    // OK
```

### Ranges

```java
for (int i : 0..n)  { ... }    // 0, 1, ..., n-1
for (int i : 0..=n) { ... }    // 0, 1, ..., n
```

- The bounds are integers, evaluated once, start first. The variable has their common type (§2.4),
  or its declared integer type, and is a copy: assigning it doesn't change the iteration.
- A range whose end is before its start runs no iterations. `a..=b` stops without overflowing when
  `b` is the type's maximum value.
- Ranges are only valid in a for-each.

### For-each with keys

Collections that have keys allow a two-variable for-each:

```java
for (ref Enemy e : enemies) { ... }                       // values only
for (Handle<Enemy> h, ref Enemy e : enemies) { ... }      // Pool: handle + value
for (int i, ref Bullet b : bullets) { ... }               // List / arrays / spans: index + value
for (String name, int score : scores) { ... }             // Map: key + value
```

A for-each loops over a range, a fixed array, a span, or a value that converts to a span
([02-types.md](02-types.md#spans)), such as an `Array` or a `List`:

- `T x` copies each element, `ref T x` refers to it, and `in T x` refers to it read-only. `var x`
  copies with the element's type. A `ref` loop needs a mutable source: not an `in` parameter, a
  `ReadOnlySpan`, or a fixed array in a variable or field that isn't `mut`.
- Each variable, including a range's, is a new variable in each iteration and is immutable unless
  declared `mut`, as in `for (mut Vec3 p : points)`; changing a copy doesn't change the element.
- `int i, ... x` adds the element's index, a copy of the position.
- The loop holds what it iterates for its whole duration, as a span variable would, so a list is
  borrowed until the loop ends ([03-memory-model.md](03-memory-model.md#borrow-counters-on-containers)).
  A fixed array is referred to, not copied, unless it is a temporary.
- A loop over a value that converts to a span iterates a `ReadOnlySpan` unless its variable is a
  `ref`, so read-only loops work on read-only sources.

> Implemented so far: ranges, fixed arrays, spans, and `Spannable` types (`Array`, `List`).
> Pools and maps don't exist yet.

### `switch`

```java
var label = switch (state) {
    case Idle           -> "idle";
    case Chasing(var t) -> "chasing";
    case Fleeing(_, _)  -> "fleeing";
};

switch (state) {
    case Idle -> wander();
    case Chasing(var t) -> {
        moveToward(t);
        attack(t);
    }
    case Fleeing -> runFrom(threat);
}
```

- Arrow form only (`case X -> ...`). **No fallthrough.** `case A, B -> ...` matches either pattern;
  such a case cannot bind names.
- A **switch statement**'s case is a block or a single statement other than a declaration. A
  **switch expression**'s case is an expression followed by `;`. A case that cannot complete, such
  as `default -> panic("...")`, needs no value; the others must all convert to one type (the
  expected type, if known, else the first case's type that every other converts to, as for `?:`).
- **Every switch must be exhaustive**: its cases must match every value, or end with `default`.
  Over an enum or `bool`, listing every variant (or both values) is enough; over integers and class
  types, a `default` or a pattern that always matches is needed. The error names a value that no
  case handles, such as `Pair(South, _)`.
- `default` must be the last case. **A case that can never match is an error**: one whose values the
  cases above all match, including a `default` after cases that already cover everything. A type
  test on a class counts as possibly failing, so it never makes a later case unreachable unless it
  always matches.
- `break` and `continue` inside a case apply to the enclosing loop, as in Kotlin; a switch is not a
  loop and needs no `break`.
- The value is evaluated once, before any case is tried. Names a case binds are copies made before
  the case runs, so a case may freely change the value it matched. If the value is a temporary, it
  lives until the switch ends (for a switch expression, until the switch's value is produced).

### Patterns

Patterns appear in `case` and after `is`. A pattern either always matches or tests the value:

| Pattern | Matches |
|---|---|
| `_` | Anything |
| `var name` | Anything, and binds it to `name` |
| `Variant`, `Enum.Variant` | That variant, whatever its fields |
| `Variant(p1, p2)` | That variant, if each field matches its pattern; every field must be listed |
| `Type name`, `Type` | A class or interface value that is non-null and a `Type`. For a value type, only its own type, which just states it |
| `42`, `-1`, `'a'`, `true` | An integer, `char`, or `bool` equal to the literal |

- The enum qualifier in `Enum.Variant` must be the matched value's enum; type arguments may be left
  out, since the value determines them: `case Result.Ok(var v)`.
- A lone name is a variant when matching an enum, and otherwise a type: `x is Dog` tests a class.
- `null`, floating-point, and string literals are not patterns.
- `x is pattern` is `true` when the pattern matches. Patterns that always match (`_`, `var x`) are
  errors after `is`. Names bound in an `if` or `while` condition follow the rules of §2.5: in scope
  for the whole statement, definitely assigned where the test succeeded.

```java
if (state is Chasing(var target) && target.isAlive()) { ... }
while (node is Some(var n)) { node = n.next; }
```

- Binding names in an `is` stores to existing locals, which releases their old values. Every bound
  value is copied out of the matched value before any of those releases, since a destructor they run
  may change it.

### String interpolation

```java
log($"hp: {player.health}, pos: {player.position}");
```

A string literal prefixed with `$` is interpolated, as in C#: each `{expression}` in it is replaced
by the expression's value, and the result is a new `String`. Only `$` strings interpolate, so braces
in ordinary strings mean nothing special.

- In a `$` string, `{{` and `}}` stand for `{` and `}`. A lone `}` is an error. Escapes (§2.2)
  work as in any string.
- The parts are evaluated left to right, each exactly once.
- A hole holds any expression on one line, including other strings, but a conditional
  `a ? b : c` must be parenthesized, since `:` starts a format specifier, as in C#.
- An interpolated value must be *printable*. Primitives and `String` are, and are written as
  `print` writes them (`3.0`, `true`, a `char` as its character), a nullable one as its value or
  `null`.
- Any other type is printable when it implements the standard library's `Printable` interface,
  and is written by its `toString()`. A nullable value of such a type is written as `null` when
  it is null. There is no default `toString()`: a type opts in, so printing never silently shows
  something like Java's `Player@1b6d3586`.

```java
interface Printable {
    readonly String toString();
}

struct Vec3 implements Printable {
    float x;
    float y;
    float z;

    readonly String toString() { return $"({x}, {y}, {z})"; }
}
```

- Primitives, `String`, and their nullables satisfy a `T extends Printable` bound without
  implementing it, so generic code can print any printable value, including an `int`, with
  `$"{value}"` or `value.toString()`. Outside generic code too, they have a `toString()` that
  writes them as interpolation does: `5.toString()` is `"5"`.

#### Format specifiers and alignment

```java
log($"{name,-12}|{score,6:D}|{ratio,8:F2}|{flags:X8}");
```

As in C#, a hole is `{value,alignment:format}`, and both parts may be left out.

- The **alignment** is an integer literal, optionally negative. The value's text is padded with
  spaces to at least that many characters: on the left for a positive alignment, so the text is
  right-aligned, and on the right for a negative one. Characters are counted as Unicode code
  points. Any printable value may be aligned, including `null`.
- The **format specifier** is a letter and an optional precision of up to two digits. It is
  checked against the value's type, so a specifier that doesn't apply is a compile error rather
  than being ignored at run time, as in C#. A nullable value is formatted when it isn't null.

| Specifier | Applies to | Writes | Precision | Example |
|---|---|---|---|---|
| `D`, `d` | Integers | Decimal | Minimum digits, zero-padded after the sign | `{-42:D5}` is `-00042` |
| `X`, `x` | Integers | Hexadecimal, upper- or lowercase | Minimum digits | `{255:x4}` is `00ff` |
| `B`, `b` | Integers | Binary | Minimum digits | `{5:B8}` is `00000101` |
| `F`, `f` | `float`, `double` | Fixed-point | Digits after the point; default 2 | `{3.14159:F3}` is `3.142` |
| `E`, `e` | `float`, `double` | Exponential, with the case of the `E` given | Digits after the point; default 6 | `{1500.0:E2}` is `1.50E+003` |

- `X` and `B` write a negative value's two's-complement bits at the type's width:
  `{-1:X}` is `FFFFFFFF` for an `int`.
- `F` and `E` round to the nearest digit. Infinities and NaN are written as `print` writes them
  (`inf`, `-inf`, `nan`).
- The other C# specifiers (`N`, `P`, `G`, `C`, `R`) and custom formats such as `0.00` are not
  supported. `N` and `C` depend on a culture, which VPL doesn't have. A `Printable` type has no
  specifiers of its own; it may only be aligned.

> Implemented so far: everything above. Not yet implemented:
> - `Printable` stdlib types (`List`, `Array`, `Span`, `Result`, ...). A collection should be
>   printable when its element type is, which needs conditional implementations, or a `toString()`
>   with its own `T extends Printable` bound;
> - a generated field dump for debugging, like Rust's `#[derive(Debug)]`, so small types needn't
>   write `toString()` by hand;
> - other ways to build strings: `+` on strings and `StringBuilder` (§2.10). Interpolation builds
>   strings with the runtime's `vpl::StringBuffer`, which a `StringBuilder` could wrap.

## 6.10 Annotations

Java syntax: `@Name` or `@Name(args)`. Currently compiler-defined only:

| Annotation | Meaning | Chapter |
|---|---|---|
| `@CallerLocation` | A panic inside the function reports where it was called from | below |
| `@Unchecked` | Remove runtime checks | [01-safety.md](01-safety.md) |
| `@AllowLargeStack` | Suppress the large-stack warning | [01-safety.md](01-safety.md) |
| `@Align(n)`, `@Pad(n)`, `@Packed` | Struct layout | [02-types.md](02-types.md) |
| `@Repr(T)` | An enum's integer representation | [07-interop.md](07-interop.md#75-enums-with-integer-representations) |
| `@Flags` | An enum whose values combine as bits | [07-interop.md](07-interop.md#75-enums-with-integer-representations) |
| `@Escaping` | Function-typed parameter or local may be stored | §6.7 |
| `@Acyclic` | A class's objects never form cycles | [03-memory-model.md](03-memory-model.md) |

An argument is a type, as in `@Repr(uint)`, or a literal, as in `@Align(16)`. Settings that
change how a declaration is laid out or treated, rather than what it is, are annotations rather
than new syntax.

Parentheses right after an annotation are its arguments unless they belong to the type it
annotates, which they do when followed by `->`, `?`, `[`, or `*`: `@Escaping (int) -> int f` and
`@Escaping ((int) -> int)? f` annotate a function type.

User-defined annotations (and reflection) are *open*.

> Implemented so far: `@CallerLocation`, `@Escaping`, `@Acyclic`, and `@Repr`. Any other
> annotation is an error.

### `@CallerLocation`

Every panic inside a function marked `@CallerLocation`, including those of the checks it calls
and of other `@CallerLocation` functions it calls, reports the location of the call to it. The
standard library uses it so that a failed `list[i]` points at the user's line rather than into
`List`. It applies to functions, methods, constructors, indexers, and properties, but not yet to
virtual, abstract, or interface methods.

## 6.11 Naming conventions

Enforced with compiler **warnings** (not errors):

| Kind | Convention | Example |
|---|---|---|
| Types (classes, structs, enums, interfaces) | `PascalCase` | `EnemySystem` |
| Enum variants | `PascalCase` | `AiState.Chasing` |
| Methods, fields, properties, locals, parameters | `camelCase` | `applyDamage`, `isDead` |
| Constants (`const`, and `static` fields that aren't `mut`) | `UPPER_SNAKE_CASE` | `MAX_SPEED` |
| Packages | `lowercase` | `game.combat` |

## 6.12 Complete example

```java
package game.enemies;

import game.math.*;

public enum AiState {
    Idle,
    Chasing(Handle<Enemy> target),
}

public struct Enemy {
    Vec3 position;
    Vec3 velocity;
    int health = 100;
    AiState state = AiState.Idle;

    readonly bool isDead { get -> health <= 0; }
}

public class EnemySystem {
    const float CHASE_SPEED = 4.0;

    Pool<Enemy> enemies = new Pool<>();

    public Handle<Enemy> spawn(Vec3 at) {
        return enemies.add(Enemy(position: at));
    }

    public void tick(float dt) {
        for (Handle<Enemy> h, ref Enemy e : enemies) {
            switch (e.state) {
                case Idle -> {}
                case Chasing(var t) -> {
                    if (enemies.get(t) is ref Enemy target) {
                        e.velocity = (target.position - e.position).normalized() * CHASE_SPEED;
                    } else {
                        e.state = AiState.Idle;
                    }
                }
            }
            e.position += e.velocity * dt;

            if (e.isDead) {
                enemies.remove(h);
            }
        }
        log($"{enemies.count} enemies alive");
    }
}
```
