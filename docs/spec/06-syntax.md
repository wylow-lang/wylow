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
Vec3 v = Vec3(1, 2, 3);                        // struct constructor, no allocation
var e = Enemy { health = 50, position = v };   // struct initializer
var list = new List<Enemy>();                  // class: heap allocation
var arr = new float[1024];                     // heap array
```

This makes every allocation visible: scanning for `new` finds them all.

### Struct constructors

- **Every struct has a default constructor**, `T()`, which sets each field to its initializer, or
  to zero / `false` / `""` / the field type's default. It cannot be declared explicitly; use field
  initializers to customize it. (Arrays and pools rely on every struct having a default.)
- Field initializers run in declaration order each time a value is default-constructed. They may
  call functions but cannot use `this` or other fields.
- A declared constructor starts from the default value, then runs its body. `this` refers to the
  value being built, and `return;` ends construction early.
- Any struct can be built with an **initializer**, `T { field = value, ... }`, which starts from the
  default constructor and then assigns the listed fields in the order written. Initializers can also
  follow a constructor call: `Enemy(pos) { health = 50 }`.
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

- A `ref` argument must be a mutable place: a local, a parameter, a field path rooted at one
  (`ref enemy.position`), an element (`ref points[i]`), or a reference a method returned. Its
  type must match the parameter exactly; no conversions apply.
- A place is read-only, and cannot be assigned or passed by `ref`, if it is rooted at an `in`
  parameter or variable, at `this` inside a `readonly` method, or at a call that returns `in`.
- Arguments are evaluated left to right, and a `ref` argument is a reference to the place, not a
  snapshot of its value: in `x + increment(ref x)`, the left `x` is read before the call.

### `readonly` struct methods

In a struct method, `this` is a `ref`, so methods may mutate the struct. A method marked `readonly`
cannot mutate `this`. Only `readonly` methods (and properties) can be called through an `in`
parameter or a `ReadOnlySpan<T>`.

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
```

- Methods may be **overloaded** by parameter types, as in Java.
- Parameters may have **default values** (compile-time constants or struct constructors).
- Arguments may be passed **by name**; named arguments must come after positional ones.

## 6.5 Properties

C#-style properties:

```java
class Player {
    int health { get; private set; } = 100;     // auto-property with initializer
    bool isDead { get => health <= 0; }         // computed, read-only

    float speed {
        get => _speed;
        set { _speed = clamp(value, 0, MAX_SPEED); }   // `value` is the assigned value
    }
    private float _speed;
}
```

- Accessors may have their own access modifier (`private set`).
- On structs, a `get` accessor that doesn't mutate may be marked `readonly`:
  `readonly bool isDead { get => health <= 0; }`.
- Simple accessors are inlined by the C++ compiler and cost nothing.

> Implemented so far: get-only properties, `T name { get => value; }` or `T name { get { ... } }`,
> and in interfaces and abstract classes `T name { get; }`. A property is read without parentheses
> and cannot be assigned. Auto-properties, `set`, and static properties are not implemented.

## 6.6 Operator overloading

Operators are `static` methods named `operator <symbol>`:

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
| Overloadable | `+ - * / %`, unary `- ! ~`, `& \| ^ << >>`, `== != < > <= >=`, indexers |
| Not overloadable | `&& \|\| = . ?? ?.`, casts, `new` |

- **Compound assignment** (`+=`, `*=`, ...) is derived automatically from the binary operator.
- Defining `==` derives `!=` (it may be defined explicitly instead).

### Indexers

```java
struct Mat4 {
    float[16] m;
    ref float this[int row, int col] { return ref m[row * 4 + col]; }
}
```

An indexer returning `ref` gives mutable access to an element (see the `ref` return rule in
[03-memory-model.md](03-memory-model.md#returning-references)), and one returning `in` gives
read-only access. An indexer may instead define `get`/`set` accessors like a property.

> Implemented so far: indexers returning `ref`, `in`, or a value, with any number of parameters.
> Indexers with `get`/`set` are not implemented.

### Equality

| Kind | `==` means |
|---|---|
| Struct | Memberwise equality, **generated automatically** if every field supports `==` (can be overridden) |
| Class, interface | **Reference identity** (same object), as in Java. Value equality is `equals()`. Allowed only when one side could be the other (or is `null`) |
| Enum | Same variant and equal data |

## 6.7 Lambdas and function types

Lambda syntax is Java's. Function types are written `(Params) -> Return`:

```java
(int, int) -> int add = (a, b) -> a + b;
list.sort((a, b) -> a.depth - b.depth);
button.onClick(() -> { play(sound); });
```

### Escaping and non-escaping

A function-typed **parameter** is **non-escaping** by default: the callee may call it but may not
store it, return it, or pass it to an escaping parameter.

| | Non-escaping | Escaping |
|---|---|---|
| Where | Function-typed parameters | Fields, locals, return values, `@Escaping` parameters, containers |
| Allocation | **None** (compiled to an inline template or function reference) | Heap-allocated, refcounted closure |
| Captures | By reference: may capture `ref`s and `Span`s, and may **mutate** captured locals | By copy: captured locals must be effectively final (Java's rule) |

```java
int count = 0;
enemies.forEach(e -> { if (e.isDead) count++; });   // non-escaping: mutates local, no allocation

void onEvent(@Escaping () -> void handler) { handlers.add(handler); }
```

Non-escaping lambdas are what make `Jobs.parallelFor` and `Mutex.lock` safe
([05-concurrency.md](05-concurrency.md)).

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

- The bounds are integers, evaluated once, start first. The variable has their common type (or
  its declared `int` or `long` type) and is a copy: assigning it doesn't change the iteration.
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
  copies with the element's type. A `ref` loop needs a mutable source: not an `in` parameter or a
  `ReadOnlySpan`.
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
| `42`, `-1`, `true` | An integer or `bool` equal to the literal |

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

Interpolated values must have a `toString()` (all primitives and stdlib types do).

## 6.10 Annotations

Java syntax: `@Name` or `@Name(args)`. Currently compiler-defined only:

| Annotation | Meaning | Chapter |
|---|---|---|
| `@CallerLocation` | A panic inside the function reports where it was called from | below |
| `@Unchecked` | Remove runtime checks | [01-safety.md](01-safety.md) |
| `@AllowLargeStack` | Suppress the large-stack warning | [01-safety.md](01-safety.md) |
| `@Align(n)`, `@Pad(n)`, `@Packed` | Struct layout | [02-types.md](02-types.md) |
| `@Escaping` | Function parameter may be stored | §6.7 |

User-defined annotations (and reflection) are *open*.

> Implemented so far: `@CallerLocation`, without arguments. Any other annotation is an error.

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
| Constants (`const`, `static final`) | `UPPER_SNAKE_CASE` | `MAX_SPEED` |
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

    readonly bool isDead { get => health <= 0; }
}

public class EnemySystem {
    const float CHASE_SPEED = 4.0;

    Pool<Enemy> enemies = new Pool<>();

    public Handle<Enemy> spawn(Vec3 at) {
        return enemies.add(Enemy { position = at });
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
