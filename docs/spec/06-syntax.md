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

- A `ref` argument must be a mutable place: a local, a parameter, or a field path rooted at one
  (`ref enemy.position`). Its type must match the parameter exactly; no conversions apply.
- A place is read-only, and cannot be assigned or passed by `ref`, if it is rooted at an `in`
  parameter or at `this` inside a `readonly` method.
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
[03-memory-model.md](03-memory-model.md#34-references)). An indexer may instead define `get`/`set`
accessors like a property.

### Equality

| Kind | `==` means |
|---|---|
| Struct | Memberwise equality, **generated automatically** if every field supports `==` (can be overridden) |
| Class | **Reference identity** (same object), as in Java. Value equality is `equals()` |
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

Operands and arguments are evaluated left to right.

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

### For-each with keys

Collections that have keys allow a two-variable for-each:

```java
for (ref Enemy e : enemies) { ... }                       // values only
for (Handle<Enemy> h, ref Enemy e : enemies) { ... }      // Pool: handle + value
for (int i, ref Bullet b : bullets) { ... }               // List / arrays / spans: index + value
for (String name, int score : scores) { ... }             // Map: key + value
```

### `switch`

- Arrow form only (`case X -> ...`). **No fallthrough.**
- Usable as a statement or an expression.
- Must be exhaustive over enums (otherwise a `default` case is required).
- Cases may use patterns, with `_` to ignore a value.

```java
var label = switch (state) {
    case Idle           -> "idle";
    case Chasing(var t) -> "chasing";
    case Fleeing(_, _)  -> "fleeing";
};
```

### String interpolation

```java
log($"hp: {player.health}, pos: {player.position}");
```

Interpolated values must have a `toString()` (all primitives and stdlib types do).

## 6.10 Annotations

Java syntax: `@Name` or `@Name(args)`. Currently compiler-defined only:

| Annotation | Meaning | Chapter |
|---|---|---|
| `@Unchecked` | Remove runtime checks | [01-safety.md](01-safety.md) |
| `@AllowLargeStack` | Suppress the large-stack warning | [01-safety.md](01-safety.md) |
| `@Align(n)`, `@Pad(n)`, `@Packed` | Struct layout | [02-types.md](02-types.md) |
| `@Escaping` | Function parameter may be stored | §6.7 |

User-defined annotations (and reflection) are *open*.

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
