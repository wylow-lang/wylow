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

## 6.3 Construction

A local must be assigned before it is read, even when its type has a default value, so that a
branch that forgets to assign it is an error rather than a silent zero. Declaring it makes no value,
so a struct's field initializers don't run for it (a fixed-array local is the exception; see
[02-types.md](02-types.md)). `var` keeps this short where the type is visible, as in
`var v = Vec3();`; write the type out where the value doesn't show it, as in
`Vec3 target = path.last();`.


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

## 6.9 Control flow

Statements are in [chapter 14](14-statements.md#statements) and patterns in
[chapter 15](15-patterns.md#patterns). These rules stay here until their chapters are written:

### Unreachable statements

A statement that can never run — one after `return`, `break`, `continue`, `panic`, or a call that
never returns — is allowed and does nothing: it is not an error, and the compiler emits no code for
it.

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

## 6.10 Annotations

Java syntax: `@Name` or `@Name(args)`. Currently compiler-defined only:

| Annotation | Meaning | Chapter |
|---|---|---|
| `@CallerLocation` | A panic inside the function reports where it was called from | below |
| `@AllowLargeStack` | Suppress the large-stack warning | [`@AllowLargeStack`](22-annotations.md#annotations.allow-large-stack) |
| `@Align(n)`, `@Packed` | Struct layout | [02-types.md](02-types.md) |
| `@Repr(T)` | An enum's integer representation | [09-enums.md](09-enums.md#enums.repr) |
| `@Flags` | An enum whose values combine as bits | [09-enums.md](09-enums.md#enums.flags) |
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
