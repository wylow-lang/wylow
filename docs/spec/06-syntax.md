# 6. Syntax

Wylow syntax is Java's unless stated otherwise. This chapter covers the differences and additions.

## 6.1 Packages, modules, imports

Packages, modules, imports, visibility, and the entry point are in
[chapter 3](03-programs.md#programs). These library modules stay here until appendix A is written:

- **`std::panic`** holds what is about panics ([Panics](18-errors.md#errors.panics)): `panic(message)`, which `std` re-exports, and
  the checks the built-in arrays make, for a container whose index isn't its storage's (a ring
  buffer on an `Array`): `checkIndex(index, length)` and `checkRange(start, count, length)`, which
  report the location of a `@CallerLocation` caller as the built-in checks do. A message is all a
  panic carries: it is a bug, not a value to recover ([Panics](18-errors.md#errors.panics)).
- **`std::memory`** holds raw memory (`allocate`, `free`, `initialize`, ..., all `unsafe`, §7) and
  the cycle collector (`collectCycles`, `cycleStats`, §3).

## 6.2 Visibility

Visibility is in [chapter 3](03-programs.md#programs.visibility). These rules about it stay here
until their chapters are written:

- **An override is written with exactly the visibility of the method it overrides**, and an
  interface method's implementation with the interface's visibility, so a method's reach is the
  same in the base class and every subclass.
- A class's implicit parameterless constructor is as visible as the class.
- **A call chooses only among the overloads visible where it is**, as in Java: one it can't see
  never wins over one it can. If none is visible, the error says how far the one that fits reaches.

## 6.3 Construction

> **`new` appears only where memory is heap-allocated.** Structs are constructed without `new`.

```java
var v = Vec3(1, 2, 3);                         // struct constructor, no allocation
var e = Enemy(health: 50, position: v);        // fields by name
var list = new List<Enemy>();                  // class: heap allocation
var arr = new float[1024];                     // heap array
```

A local must be assigned before it is read, even when its type has a default value, so that a
branch that forgets to assign it is an error rather than a silent zero. Declaring it makes no value,
so a struct's field initializers don't run for it (a fixed-array local is the exception; see
[02-types.md](02-types.md)). `var` keeps this short where the type is visible, as in
`var v = Vec3();`; write the type out where the value doesn't show it, as in
`Vec3 target = path.last();`.

### Class constructors

Java-style, including `super(...)` and `this(...)` chaining:

```java
class Shotgun extends Weapon {
    pub Shotgun(int pellets) { super(1.0); this.pellets = pellets; }
    int pellets;
}
```

Classes are always created with `new`, and structs never are. The order of construction, what
`this(...)` runs, and what a constructor may do before its fields are assigned are in
[02-types.md](02-types.md#construction).

## 6.4 Methods and parameters

### Parameter modes

| Mode | Meaning | Call site |
|---|---|---|
| `T x` | By value (structs are copied, unique ones moved) | `f(x)` |
| `ref T x` | Mutable reference | `f(ref x)`; the `ref` keyword is **required** |
| `in T x` | Read-only reference (no copy, no mutation) | `f(x)` |

```java
void applyDamage(ref Enemy e, in DamageInfo info) { e.health -= info.amount; }

applyDamage(ref enemy, info);
```

`ref` and `in` parameters follow the reference rules in
[03-memory-model.md](03-memory-model.md#33-references). `in` is intended for large read-only structs
such as matrices. For a unique struct ([02-types.md](07-structs.md#structs.unique)), a by-value parameter takes the value over, and
the caller can't use what it passed ([03-memory-model.md](03-memory-model.md#moves)), while `in` and
`ref` leave it with the caller. On class-typed parameters, `ref`/`in` are not meaningful (a class is already passed
by reference) and are a compile error. So is a function-typed parameter, nullable or not, since a
function is already passed without copying.

- A `ref` argument must be a mutable place: a `mut` local or parameter, a field path rooted at
  one (`ref enemy.position`), an element (`ref points[i]`), or a reference a method returned. Its
  type must match the parameter exactly; no conversions apply.
- A place is read-only, and cannot be assigned or passed by `ref`, if it is rooted at an `in`
  parameter or variable, a variable, parameter, or field that isn't `mut` (§2.7), `this` inside a
  struct method that isn't `mut`, or a call that returns `in`.
- Arguments are evaluated left to right, and a `ref` argument is a reference to the place, not a
  snapshot of its value: in `x + increment(ref x)`, the left `x` is read before the call.

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
bool any<T>(this ReadOnlySpan<T> items, fn(T) -> bool test) {
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
- **Members win.** If the receiver's type has a method or field of that name, its
  extensions are not considered, so an extension can never change what existing code calls. Among
  extensions, overload resolution chooses as for functions.
- The receiver converts to the `this` parameter's type by identity, upcasts, and the implicit span
  conversions (§2.11), so one extension on `ReadOnlySpan<T>` serves fixed arrays, `T[]`, `List`,
  and spans. No other conversions apply. Type arguments are inferred from the receiver as from any
  argument.
- A nullable receiver needs `?.` or a check, as for members, unless the `this` parameter's type
  is itself nullable: `bool isNullOrEmpty(this String? s)` can be called on a `String?` directly.
- An extension is an ordinary function: it sees the receiver's members that its own file can see, is statically
  dispatched (never virtual), and can also be called as `any(mobs, test)`.
- An extension is found where it is visible and its name is imported, or within its own module.

## 6.5 No properties

Wylow has no properties (C#'s `get` and `set`), as Java has none. A value is either a field, read
and assigned directly, or a method, called with parentheses:

```java
class Player {
    pub mut int health = 100;
    pub bool isDead() { return health <= 0; }
}

if (player.isDead()) { ... }
```

A class field that isn't `mut` can be read anywhere but assigned only by its class's constructors,
which covers what a read-only property would: `array.length`. A struct's fields are as mutable as
the variable holding it (§2.7), so a struct whose fields must keep an invariant leaves them unmarked, private to its file,
and offers methods: `span.length()`. Turning a field into a method later means updating its uses.

## 6.6 Indexers

There is no operator overloading: an operator always means what it means for the built-in types,
as in Java, so `a + b` never runs user code. Vector math is written with methods
(`a.add(b)`, `v.scale(s)`). The one exception is indexing: any struct or class may declare an
indexer, so that a user's container indexes like the standard library's.

```java
struct Mat4 {
    pub float[16] m;
    pub mut ref float operator [](int row, int col) { return ref m[row * 4 + col]; }
}
```

An indexer is an instance method named `operator []`, as in C++, so `m[1, 2]` calls
`m.operator [](1, 2)`. One returning `ref` gives mutable access to an element (see the `ref` return
rule in [03-memory-model.md](03-memory-model.md#returning-references)), and one returning `in`
gives read-only access.

An indexer may return `ref`, `in`, or a value, and take any number of parameters.

Where there is no element to refer to, such as in a packed array of 4-bit values, `operator []=`
handles assignment: it takes the indices and then the assigned value, and returns `void`.
`grid[i] = v` calls it, and `grid[i] += v` and `grid[i]++` read with `operator []` and write with
`operator []=`. A type may not declare both `operator []=` and an `operator []` that returns `ref`,
inherited or not, so an assignment never has two meanings. Such an element is not a place:
`ref grid[i]`, passing `grid[i]` by `ref`, and returning it with `return ref` are errors.

```java
struct Nibbles {
    byte[2048] bytes;

    pub byte operator [](int i) { return bytes[i >> 1] >> ((i & 1) * 4) & 0xF; }
    pub mut void operator []=(int i, byte value) { ... }
}
```

As in C#, where one indexer declares both its `get` and its `set`, an `operator []=` is the writing
half of an indexer:

- Each `operator []=` pairs with the `operator []` whose index parameters have the same types and
  modes, which returns a value or `in` (not `ref`, nor `in?`), and its value parameter has that
  indexer's element type. A type with only `operator []=` is an error.
- An assignment resolves its indices against the `operator []` overloads, with conversions,
  default values, and named arguments as for any call, then takes the paired `operator []=`. So
  `operator []=` declares no default values: its indices take the reading indexer's.
- The value converts to the value parameter as an argument does. It is passed by value or `in`,
  never `ref`.
- The receiver, then each index, is evaluated once, before the value; a compound assignment reads
  the element with `operator []` after the indices and before the value
  ([03-memory-model.md](03-memory-model.md#evaluation-order-and-the-heap)). A by-value or `in` index
  is evaluated into a temporary that both calls take, so the value can't change it in between.
- It is an instance method, which may be `virtual` or declared in an interface, and may be less
  visible than its `operator []`, as C#'s `private set`. On a struct it needs `mut` to change the
  struct, and then a mutable place, not a temporary, as any `mut` method call does (§6.4); one that isn't `mut` suits a
  struct that writes through a class it refers to. A class receiver needs neither.
- Indexing a type parameter, to read or to assign, is not supported yet.

### Equality

| Kind | `==` means |
|---|---|
| Struct | Memberwise equality, **generated automatically** if every field supports `==`; it cannot be redefined |
| Class, interface | **Reference identity** (same object), as in Java. Value equality is `equals()`, from an `Equatable<T>` interface beside `Comparable<T>`, designed with the standard library. Allowed only when one side could be the other (or is `null`) |
| Enum | Same variant and equal data |
| `String` | **Same contents** (the same bytes), as in C#, unlike Java's identity: a literal equals a string built at run time with the same text |

## 6.7 Lambdas and function types

Lambda syntax is Java's. Function types are written `fn(Params) -> Return`, a built-in type like
`int`, so a function type never looks like a lambda:

```java
fn(int, int) -> int add = (a, b) -> a + b;
list.sort((a, b) -> a.depth - b.depth);
button.onClick(() -> { play(sound); });
```

- A function type's parameters are always parenthesized, even none or one: `fn() -> void`,
  `fn(Mob) -> bool`. They take parameter modes, which are part of the type: `fn(ref Vec3) -> void`.
- The return type is always written, `void` included, and takes any suffix: `fn(int) -> int?`
  returns an `int?`. To make the function itself nullable, or an array of functions, parenthesize
  it: `(fn(int) -> int)?`, `(fn(int) -> int)?[]`. Parentheses in a type are only for that: they
  hold one function type and are followed by `?`, `[`, or `*`, so `(fn(int) -> int) f` is an error.
- `->` groups to the right: `fn(int) -> fn(int) -> int` returns a function.
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
  `fn(int) -> int f = up ? x -> x + 1 : x -> x - 1;`, or `sort(items, up ? ascending : descending)`.
  The choice is made once, when the expression is evaluated. Passed as an argument, a choice
  whose options include lambdas waits for its function type as a lambda does: overloads and
  generic inference treat each option as the argument (`apply(3, up ? x -> x * 2 : x -> x + 1L)`
  infers `long`), and an option that is a function-typed local constrains the type like any other
  argument. Anywhere else, a lambda option without a target type takes another option's function
  type. A lambda a switch expression chooses can't use a name its case declares
  (`case Add(var n) -> x -> x + n` is an error), since the lambda is made outside the case; use a
  switch statement.
- A function type has no default value, so a field of one must be assigned by every constructor
  or be nullable. `(fn(int) -> int)?` is a nullable value, like `int?`, narrowed by null checks.
  Function values have no `==`.
- A lambda can use a local only if it is definitely assigned where the lambda is created. A null
  check outside a lambda doesn't narrow inside it, since the code before the lambda runs could
  change the local; and a local that any lambda assigns is never narrowed, since the lambda may
  run during any call.

### Escaping and non-escaping

A function-typed **parameter or local** is **non-escaping** by default: the function may call it
and pass it to other non-escaping parameters, but may not store it, return it, or pass it to an
escaping parameter. Declaring the parameter or local `escaping` opts in.

| | Non-escaping | Escaping |
|---|---|---|
| Where | Parameters and locals | Fields, return values, `escaping` parameters and locals, containers |
| Captures | By reference: may capture `ref`s and `Span`s, and may **mutate** captured locals | By copy: captured locals can't be `mut` |

```java
mut int count = 0;
enemies.forEach(e -> { if (e.isDead) count++; });   // non-escaping: mutates local, no allocation

void onEvent(escaping fn() -> void handler) { handlers.add(handler); }

fn(Mob) -> bool isDead = m -> m.health <= threshold; // non-escaping: no allocation
escaping fn() -> void onDone = () -> play(sound);    // escaping: a heap closure, can be stored
handlers.add(onDone);
```

`escaping` is a keyword written before the type, after any `mut`: `mut escaping fn() -> void f`.
It is written only where a function could be either form: on a parameter, a local, or a function
type's own parameter. Anywhere else a function is always escaping, and writing `escaping` is an
error.

A non-escaping local stays non-escaping: storing it is an error.
Like a `ref` local, a non-escaping local is bound once, at its declaration, and can't be
reassigned or declared `mut`, so it can't outlive what its lambda captures: non-escaping functions
are second-class values ([03-memory-model.md](03-memory-model.md#second-class-values)). An escaping local can
be reassigned if it is `mut`.

- **Escaping is part of the function type.** Each function type has a non-escaping and an
  escaping form, written the same way. Written as the type of a parameter, a local, or a function
  type's own parameter, it is non-escaping unless that is declared `escaping`, as in
  `fn(escaping fn() -> void) -> void`; anywhere else (a field, a return type, a type argument, an
  array element, or a function type's return type) it is escaping. A nullable function follows the
  same rule, except where noted below. `var` takes the form of its initializer. An escaping value
  converts to the non-escaping form, so it can be passed to a non-escaping parameter or bind a
  non-escaping local; nothing converts the other way.
- **A lambda is escaping when its target is**: the variable, field, parameter, or return type it
  is converted to, or the type of the `?:` or switch expression it is an option of. A lambda
  passed as an argument takes the form of the parameter that overload resolution picks.
- **Type arguments are always escaping**, so a type argument inferred from a function is its
  escaping form: `identity(f)` needs `f` to be `escaping`.
- **Nullable functions.** A parameter of type `(fn(int) -> int)?` is non-escaping unless declared
  `escaping`, so an optional callback is passed without allocating. Like any non-escaping
  parameter it is bound once, and it is narrowed by null checks. Only a parameter can hold one: a
  local of nullable function type must be `escaping`, since a non-escaping local owns the lambda
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
- An escaping lambda **moves** a unique local or by-value parameter it uses
  ([03-memory-model.md](03-memory-model.md#moves)), which can't be used after the lambda is made;
  inside the lambda it is read-only, like any capture. A non-escaping lambda refers to it in place.
- An escaping lambda can't capture a `ref` or `in` local or parameter, a span or other
  `ref struct`, a non-escaping function, or `this` in a struct's method or constructor (copy what it
  needs to a local first). A captured class reference, including `this`, keeps its object alive.
  So a closure can be part of a cycle, as in `this.onHit = () -> react(this);`, which
  `memory::collectCycles` collects like any other.
- **Choices.** A `?:` or switch expression of escaping type evaluates only the option it chooses,
  like any other. One of non-escaping type makes its lambdas and refers to its non-escaping
  locals up front, which no one can observe; the check a `!` makes runs only where its option is
  chosen, so an option that isn't chosen may still be null, and an escaping option (a field, a
  call, an escaping local) is evaluated only when it is chosen.
- **Signatures.** An override or interface implementation must declare the same parameters
  `escaping` as the method it overrides or implements. Overloads can't differ only in which
  parameters are `escaping`. A function type's `escaping` parameters are part of the type, and
  print as written: `fn(escaping fn() -> void) -> void` and `fn(fn() -> void) -> void` are different
  types, and neither converts to the other. A lambda of the first may store its argument.
- A type pattern can't name a function type (`f is fn(int) -> int g`); narrow a nullable function
  with a null check.

```java
for (Mob m : mobs) {
    handlers.add(() -> m.hit());            // fine: each iteration has its own 'm'
}
mut int total = 0;
escaping fn() -> int f = () -> total;      // error: 'total' is 'mut'
total++;
```

A call of a function value stored where the call could replace it (a field, a list element, a
local that a lambda assigns) holds a reference to the closure until the call returns.

### Lambdas held by `ref struct`s

A `ref struct` may have fields of function type, which hold non-escaping lambdas: the struct is
second-class ([03-memory-model.md](03-memory-model.md#spans)), so it can't outlive the lambda's
captures either. A function may return such a struct holding its own function-typed parameters,
where it may return a `ref struct` at all. This is what lets a lazy query such as
`mobs.where(m -> m.health < 5)` store its lambda without allocating
([02-types.md](02-types.md#queries)).

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
errors. `++` and `--` may be written prefix or
postfix with the same meaning.

### Evaluation order

Operands and arguments are evaluated left to right. This includes reads of object fields relative
to calls; see [03-memory-model.md](03-memory-model.md#evaluation-order-and-the-heap).

### Unreachable statements

A statement that can never run — one after `return`, `break`, `continue`, `panic`, or a call that
never returns — is allowed and does nothing: it is not an error, and the compiler emits no code for
it.

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
for (int i, ref Bullet b : bullets) { ... }               // List / arrays / spans: index + value
for (String name, int score : scores) { ... }             // Map: key + value
```

A for-each loops over a range, a fixed array, a span, a value that converts to a span
([02-types.md](02-types.md#spans)), such as an `Array` or a `List`, or the characters of a
`String`, whose index is a byte offset ([02-types.md](02-types.md#strings)):

- `T x` copies each element, `ref T x` refers to it, and `in T x` refers to it read-only. `var x`
  copies with the element's type. A `ref` loop needs a mutable source: not an `in` parameter, a
  `ReadOnlySpan`, a fixed array in a variable or field that isn't `mut`, or a temporary fixed array (§6.4).
- Each variable, including a range's, is a new variable in each iteration and is immutable unless
  declared `mut`, as in `for (mut Vec3 p : points)`; changing a copy doesn't change the element.
- `int i, ... x` adds the element's index, a copy of the position.
- The loop holds what it iterates for its whole duration, as a span variable would, so a list is
  borrowed until the loop ends ([03-memory-model.md](03-memory-model.md#borrow-counters-on-containers)).
  A fixed array is referred to, not copied, unless it is a temporary.
- A loop over a value that converts to a span iterates a `ReadOnlySpan` unless its variable is a
  `ref`, so read-only loops work on read-only sources.
- A nullable local can be looped over where it is known not to be null, as for using its members.

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
- **Guards**, as in Java: `case Pattern when condition -> ...` matches only if the pattern matches
  and then the condition, a `bool`, is true. `when` is a keyword. The guard runs after the case's
  names are copied (below), so the guard and the case see the same copies. The names the pattern
  binds are in scope in the condition, and the case is as an `if`'s body is to its condition: names
  an `is` in the condition binds are in scope in it, and what the condition narrows stays narrowed.
  Likewise the names of a case whose guard is false live until the switch ends, as an `if`
  condition's names live through its `else`. In `case A, B when c`, the guard applies to the whole
  case. A case with `default` or `null` can't have a guard. A guarded case never counts toward
  exhaustiveness, and never makes a later case unreachable, since its condition may be false; it
  is still an error if the cases above already match everything it matches.

  ```java
  switch (shape) {
      case Circle(var r) when r > 10 -> big();
      case Circle(var r) -> small(r);
      case Rect(var w, var h) -> area(w, h);
  }
  ```

  **Watch out:** a switch matches its value where it is, without a copy of its own, so a guard
  that changes the value, or calls code that does, changes what the later cases test. Each case
  tests the value as it is when that case is tried (including whether it is null, so this is never
  unsafe). To match the value as it was, copy it first:

  ```java
  var snapshot = ai.state;
  switch (snapshot) {
      case Chasing(var t) when ai.calm() -> chase(t);   // calm() may set ai.state
      case Idle -> rest();
      case Chasing(var t) -> follow(t);
  }
  ```

- **`case null`**, as in Java: a switch with a `case null` takes its value as it is, nullable or
  not. Without one, a nullable value must be narrowed first (§2.5), or it is an error. `case null`
  matches only null, and may be written anywhere among the cases. `default` doesn't match null;
  write `case null, default ->` for both (only `null` may be listed with `default`). The other
  cases' patterns match the non-null value, so `case Enemy e`, `case var v` (a `v` of the non-null
  type), and `case Chasing(var t)` match as they would on a narrowed value. A case's test narrows
  the switched local in that case's body, as an `if`'s would; to name the value, bind it.

  ```java
  switch (findTarget()) {
      case Boss b -> flee(b);
      case null -> wander();
      case var e -> attack(e);
  }
  ```

  A `case null` on a value whose type can't be null is an error, and so is one on a local that
  narrowing knows isn't null there, such as one assigned a value or tested just before: the case
  could never match. A `ref?` or `in?` reference is tested with `is ref` instead
  ([03-memory-model.md](03-memory-model.md#nullable-references-ref-t)), not
  switched on with `case null`.
- A **switch statement**'s case is a block or a single statement other than a declaration. A
  **switch expression**'s case is an expression followed by `;`. A case that cannot complete, such
  as `default -> panic("...")`, needs no value; the others must all convert to one type (the
  expected type, if known, else the first case's type that every other converts to, as for `?:`).
- **Every switch must be exhaustive**: its cases must match every value, or end with `default`.
  Over an enum or `bool`, listing every variant (or both values) is enough; over integers and class
  types, a `default` or a pattern that always matches is needed. A nullable value is null or a
  value of its type, and a nullable field likewise. The error names a value that no case handles,
  such as `Pair(South, _)` or `Moved(null)`.
- `default` must be the last case. **A case that can never match is an error**: one whose values the
  cases above all match, including a `default` after cases that already cover everything. A type
  test on a class counts as possibly failing, so it never makes a later case unreachable unless it
  always matches.
- `break` and `continue` inside a case apply to the enclosing loop, as in Kotlin; a switch is not a
  loop and needs no `break`.
- The value is evaluated once, before any case is tried. It must have a type of its own: `null`
  alone and a `void` call have none, and both are errors. Names a case binds are copies made before
  its guard and the case run, so a case may freely change the value it matched. If the value is a temporary, it
  lives until the switch ends (for a switch expression, until the switch's value is produced).

### Patterns

Patterns appear in `case` and after `is`. A pattern either always matches or tests the value:

| Pattern | Matches |
|---|---|
| `_` | Anything |
| `var name` | Anything, and binds it to `name` |
| `Variant`, `Enum.Variant` | That variant, whatever its fields |
| `Variant(p1, p2)` | That variant, if each field matches its pattern; every field must be listed |
| `Struct(p1, p2)` | A struct value, if each field, in declaration order, matches its pattern; every field must be listed |
| `null` | Null: a switch's nullable value (above), or a nullable field, in a `case` |
| `Type name`, `Type` | A class or interface value that is non-null and a `Type`. For a value type, only its own type, which just states it |
| `42`, `-1`, `'a'`, `true` | An integer, `char`, or `bool` equal to the literal |

- The enum qualifier in `Enum.Variant` must be the matched value's enum; type arguments may be left
  out, since the value determines them: `case Result.Ok(var v)`.
- An integer pattern is a constant of the matched value's integer type: an unsuffixed literal
  converts to it where it fits, so matching a `byte` writes `case 200`, and matching a `long` with a
  value past `INT_MAX` writes `case 2147483648L`, since a literal too large for `int` needs a suffix
  ([Integer literals](02-lexical.md#lexical.literals.integer)).
- A lone name is a variant when matching an enum, and otherwise a type: `x is Dog` tests a class.
- A struct pattern deconstructs, as Java's record patterns do. A struct value is always its own
  type, so the pattern matches whenever its field patterns do; it is useful nested, as in
  `case Moved(Vec3(var x, _, _))`, or on a nullable value, where `pos is Vec3(var x, _, var z)`
  tests that a `Vec3?` isn't null and binds two fields. On a `Vec3` with field patterns that all
  always match (`_`, `var`, or such a struct pattern) it always matches, so it is an error after
  `is`, like `var x`. Every field must be visible where the pattern is written. As for an enum, the
  type arguments may be left out, since the value determines them: `case Pair(1, var s)` on a
  `Pair<int, String>`.
- Floating-point and string literals are not patterns. `null` is one only in a `case`, alone or
  inside another pattern, as in `case Moved(null)`, and on a type that can be null. A whole `is`
  pattern can't be `null`, since `== null` says that; `s is Moved(null)` is allowed.
- `x is pattern` is `true` when the pattern matches. Patterns that always match (`_`, `var x`) are
  errors after `is`. Names bound in an `if` or `while` condition follow the rules of §2.5: in scope
  for the whole statement, definitely assigned where the test succeeded.

```java
if (state is Chasing(var target) && target.isAlive()) { ... }
while (node is Some(var n)) { node = n.next; }
```

- Binding names in an `is` stores to existing locals, left to right, which releases their old
  values. Every bound value is copied out of the matched value before any of those releases, since a
  destructor they run may change it.

#### Reference bindings

A field pattern may bind a reference to the field instead of a copy: `in var name` (read-only) or
`ref var name` (mutable), or with a type, `in Vec3 name`. A field of a unique type
([02-types.md](07-structs.md#structs.unique)) can only be bound this way, since a binding by value
would copy it:

```java
switch (job) {
    case Mix(in var buffer, var gain) -> mixer.play(buffer, gain);   // 'buffer' refers into 'job'
    case Stop -> mixer.stop();
}
```

- A reference binding refers into the matched value, so the matched value must be a place that
  stays put while the binding is in scope: a local or parameter, or a field or element path rooted
  at one, that nothing in the binding's scope assigns, including a lambda that may run there. A
  value in an object, which any call might replace, is moved or copied into a local first. Assigning
  an enum a new variant while a binding refers into its old one would leave the binding pointing
  at another variant's fields, so this rule is what makes it safe; it is the rule for references
  into nullable values ([02-types.md](02-types.md#nullable-value-types)).
- `ref` needs a mutable place, as passing by `ref` does; a variant's fields can't be changed in
  place (§2.8), so a variant field is bound with `in` only, and `ref` binds a struct pattern's field.
- They are bound in `case` and after `is`, like other names, and are second-class references
  ([03-memory-model.md](03-memory-model.md#second-class-values)).
- Moving a unique field out of the matched value with a pattern is not supported; use
  `std::replace` on a struct field, or match a local and move it whole.

### String interpolation

```java
log($"hp: {player.health}, pos: {player.position}");
```

A string literal prefixed with `$` is interpolated, as in C#: each `{expression}` in it is replaced
by the expression's value, and the result is a new `String`. Only `$` strings interpolate, so braces
in ordinary strings mean nothing special.

- In a `$` string, `{{` and `}}` stand for `{` and `}`. A lone `}` is an error. Escapes ([Escapes](02-lexical.md#lexical.literals.escapes))
  work as in any string.
- The parts are evaluated left to right, each exactly once.
- A hole holds any expression on one line, including other strings, but a conditional
  `a ? b : c` must be parenthesized, since `:` starts a format specifier, as in C#.
- An interpolated value must be *printable*. Primitives and `String` are, and are written as
  `print` writes them (`3.0`, `true`, a `char` as its character), a nullable one as its value or
  `null`. A floating-point value is written as the shortest text that reads back as the same
  value, in the plain or exponential form C++'s `std::to_chars` picks, with `.0` added to a whole
  number written without an exponent; `inf`, `-inf`, and `nan` name the rest.
- Any other type is printable when it implements the standard library's `Printable` interface,
  and is written by its `toString()`. A nullable value of such a type is written as `null` when
  it is null. There is no default `toString()`.

```java
interface Printable {
    String toString();
}

struct Vec3 implements Printable {
    pub float x;
    pub float y;
    pub float z;

    pub String toString() { return $"({x}, {y}, {z})"; }
}
```

- The primitive types and `String` implement `Printable` ([02-types.md](02-types.md#bounds)), so
  generic code can print any printable value, including an `int`, with `$"{value}"` or
  `value.toString()`. Outside generic code too, their `toString()` writes them as interpolation
  does: `5.toString()` is `"5"`. A nullable type doesn't satisfy a `Printable` bound, though
  interpolation writes a nullable value as its value or `null`.

`print` writes its value and then one `\n` byte. The runtime writes standard output unchanged on
every platform (the stream is binary), so a value that ends in a newline character carries exactly
what the program made, with no translation of its own.

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
  (`inf`, `-inf`, `nan`). Every NaN is written `nan`, whatever its sign.
- The other C# specifiers (`N`, `P`, `G`, `C`, `R`) and custom formats such as `0.00` are not
  supported. A `Printable` type has no
  specifiers of its own; it may only be aligned.

## 6.10 Annotations

Java syntax: `@Name` or `@Name(args)`. Currently compiler-defined only:

| Annotation | Meaning | Chapter |
|---|---|---|
| `@CallerLocation` | A panic inside the function reports where it was called from | below |
| `@AllowLargeStack` | Suppress the large-stack warning | [`@AllowLargeStack`](22-annotations.md#annotations.allow-large-stack) |
| `@Align(n)`, `@Packed` | Struct layout | [02-types.md](02-types.md) |
| `@Repr(T)` | An enum's integer representation | [07-interop.md](07-interop.md#75-enums-with-integer-representations) |
| `@Flags` | An enum whose values combine as bits | [07-interop.md](07-interop.md#75-enums-with-integer-representations) |
| `@Acyclic` | A class's objects never form cycles | [03-memory-model.md](03-memory-model.md) |

An argument is a type, as in `@Repr(uint)`, or a literal, as in `@Align(16)`. Settings that
change how a declaration is laid out or treated, rather than what it is, are annotations rather
than new syntax. **An annotation never changes a type**: what can be assigned, converted, or
captured. Whatever is part of a type is a keyword, such as a parameter's mode (`ref`, `in`) or
whether a function is `escaping` (§6.7).

Parentheses right after an annotation are its arguments unless they start with `fn`, which makes
them the parentheses that group the annotated declaration's function type: `@Name (fn(int) -> int)? f`.

### `@CallerLocation`

Every panic inside a function marked `@CallerLocation`, including those of the checks it calls
and of other `@CallerLocation` functions it calls, reports the location of the call to it. The
standard library uses it so that a failed `list[i]` points at the user's line rather than into
`List`. A lambda written in such a function reports its own locations, since it may run after the
call has returned. It applies to functions, methods, constructors, and indexers, but
not yet to virtual, abstract, or interface methods.

## 6.12 Complete example

```java
// src/enemies/system.wyl, in module enemies
import std::List;
import math::*;

pub enum AiState {
    Idle,
    Chasing(int target),
}

pub struct Enemy {
    pub Vec3 position;
    pub Vec3 velocity;
    pub int health = 100;
    pub AiState state = AiState.Idle;

    pub bool isDead() { return health <= 0; }
}

pub class EnemySystem {
    const float CHASE_SPEED = 4.0;

    List<Enemy> enemies = new List<Enemy>();

    pub int spawn(Vec3 at) {
        enemies.add(Enemy(position: at));
        return enemies.count() - 1;
    }

    pub void tick(float dt) {
        for (ref Enemy e : enemies) {
            switch (e.state) {
                case Idle -> {}
                case Chasing(var t) when enemies.get(t) is ref Enemy target -> {
                    e.velocity = target.position.sub(e.position).normalized().scale(CHASE_SPEED);
                }
                case Chasing -> e.state = AiState.Idle;
            }
            e.position = e.position.add(e.velocity.scale(dt));
        }
        log($"{enemies.count()} enemies alive");
    }
}
```
