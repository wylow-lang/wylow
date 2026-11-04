# Wylow guide

> **Unrevised source material.** This list was copied from the old spec's README and hasn't been
> checked: it compares Wylow with other languages without sources, uses non-generic examples,
> links to old spec chapters that are being replaced, and has stale claims (item 2: a static's type
> must be copyable as well as sendable). It is rewritten once the spec's chapters are, against
> them.

Learning material for Wylow: what to expect, and what differs from the languages it borrows from.
The rules themselves are in [the specification](../spec/README.md).

## Behaviors that surprise people

These are deliberate, but differ from what a Java/C#/C++ programmer might expect.
Each is explained in detail in its chapter.

1. **Copying a struct shares what's inside it.** Struct copies are shallow: class references in the
   copy point at the same objects (same `List`, same `String`). Use `.clone()` for a deep copy.
   See [Struct declarations](../spec/07-structs.md#structs.declarations).
2. **Statics are read-only.** Unlike Java, there is no `static mut`: a static's type must be
   sendable, and shared state changes through `Atomic<T>` or `Mutex<T>`. State one thread changes
   lives in an object passed to what uses it. See [Statics](../spec/06-variables.md#variables.statics).
3. **`1.5` is a `float`.** Use `1.5d` for `double`. See [Floating-point literals](../spec/02-lexical.md#lexical.literals.float).
4. **`byte` is unsigned** (like C#). `sbyte` is the signed version.
   **Arithmetic keeps its operands' type**: `byte + byte` is a `byte`, not an `int`, so it can
   overflow. Widen first where it might: `((int)a + b) / 2`. See [Arithmetic](../spec/13-expressions.md#expressions.arithmetic).
5. **`new` means heap allocation.** Structs are constructed without it: `Vec3(1, 2, 3)`.
   See [Construction](../spec/13-expressions.md#expressions.construction).
6. **Class `==` is identity, struct `==` is memberwise.** `String` is the exception: its `==`
   compares contents. See [Equality](../spec/07-structs.md#structs.equality), [classes](../spec/08-classes.md#classes.equality), [enums](../spec/09-enums.md#enums.equality), and [`String`](../spec/04-types.md#types.string).
7. **A variant can leave out its enum only where the type is known.** `Direction d = East;` and
   `move(East)` work, but `var d = East;` doesn't, and neither does an argument to overloads that
   disagree on its type. See [Implicit variants](../spec/09-enums.md#enums.variants.implicit).
8. **`break` in a `switch` case leaves the enclosing loop**, since a switch has no fallthrough to
   stop. See [Switch statements](../spec/14-statements.md#statements.switch).
9. **A list can't change length while it's borrowed.** A loop over a list, a `ref` or span into
   it, or an assignment like `list[0] = f()` borrows it, and `add` or `removeAt` meanwhile panics.
   See [Borrows](../spec/17-memory.md#memory.borrows).
10. **Cycles are freed only when you ask.** Reference counting frees everything else immediately, but
   objects that refer to each other in a cycle stay alive until the program calls
   `memory::collectCycles`, which never runs on its own. Use `Weak<T>` for references that shouldn't
   keep their target alive. See [Cycles](../spec/17-memory.md#memory.cycles).
11. **`T?` is `T` when `T` is already nullable.** A generic `T? find()` on a `List<int?>` returns
   an `int?` that is null both when nothing was found and when a null was. See
   [Nullable type parameters](../spec/11-generics.md#generics.nullable).
12. **A null check doesn't narrow a `mut` local that a lambda assigns**, from where the lambda is
   made on, since the lambda may run during any call. Copy it to another local and check that. See
   [Lambdas](../spec/12-functions.md#functions.lambdas).
13. **Variables, parameters, and fields are immutable unless declared `mut`**, as in Rust. For a
   class reference, this means the variable can't be reassigned; the object itself can still
   change. See [Mutability](../spec/06-variables.md#variables.mutability).
14. **Unmarked means private to the file, not to the type.** Other types in the same file see a
   type's unmarked members; `pub(module)`, `pub(package)`, and `pub` widen it. There is no
   `protected`. See [Visibility](../spec/03-programs.md#programs.visibility).
15. **Generic code can't copy a `T` unless `T extends copyable`**, since `T` may be a
   `unique struct`, which is moved and never copied. Moving, passing by `ref` or `in`, and returning
   need no bound. See [Unbounded type parameters](../spec/11-generics.md#generics.unbounded).

## Organizing code

Written against [chapter 3](../spec/03-programs.md#programs).

- **A free function or a static method.** A free function belongs to a concept, and lives in the
  module named for it: `geometry::distance(a, b)`, `text::parse(s)`. A static method belongs to a
  type: its factories and constants, `Point.origin()`, `Color.WHITE`. A class with only static
  members, written to hold functions, is better written as a module.
- **Sealing a type's internals.** A type's members without a visibility are visible to its whole
  file, other types included. A type whose internals the rest of its module shouldn't touch goes in
  a file of its own.

## Structs and classes

Written against [chapter 7](../spec/07-structs.md#structs) and [chapter 8](../spec/08-classes.md#classes).

- **Avoiding a struct copy.** A copy is made wherever [the list of copies](../spec/07-structs.md#structs.copies)
  says. To refer to the value instead, take a large argument by `in`, bind `in` or `ref` locals, and loop
  with `for (in Order o : orders)` or `for (ref Order o : orders)`. Calling a method on a place never
  copies it. Declaring a struct `unique` makes every copy of it an error.
- **Copying a unique struct on purpose.** Give it a method that makes the copy, conventionally `clone()`.
- **Owning a resource.** A resource that has to be released is held by a class, whose destructor runs when
  the last reference goes, or by a `unique` struct with a destructor, whose single owner decides when.
- **Keeping an invariant in a struct.** There are no properties: a value is a field, read and
  assigned directly, or a method, called with parentheses. A struct's fields are as mutable as the
  variable that holds it ([Mutability](../spec/06-variables.md#variables.mutability)), so a struct
  whose fields must keep an invariant leaves them unmarked, private to its file, and offers methods:
  `span.length()`. A class field that is not `mut` is read anywhere and assigned only by its class's
  constructors ([Fields](../spec/08-classes.md#classes.fields)), which covers what a read-only
  property would. Turning a field into a method later means updating its uses.
