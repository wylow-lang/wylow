<a id="introduction"></a>
# 1. Introduction

<a id="introduction.scope"></a>
## 1.1 Scope

This specification defines the Wylow programming language: the form of a program, which programs
are valid, and what a valid program does when it runs.

It does not define:

- how an implementation is run, or how it builds a program;
- the text and codes of diagnostics, or which warnings exist
  ([docs/diagnostics](../diagnostics/README.md));
- what operations cost;
- the standard library, except the types and functions the language itself names
  ([Appendix A](a-library.md#library)). The rest of the library is documented in its source.

> **Note:** Wylow is a general-purpose language. It has value types, inline arrays, and control over
> layout; objects are freed by reference counting, with cycles collected only where the program
> asks; and outside `unsafe` code no program has undefined behavior. The current implementation
> compiles Wylow to C++.

<a id="introduction.organization"></a>
## 1.2 Organization

Chapters follow the structure of a program: its tokens (chapter 2), its packages and modules (3),
types and conversions (4, 5), declarations (6 to 12), expressions, statements, and patterns (13 to
15), and the flow rules that relate them (16). Chapters 17 to 22 give the rules that apply across
constructs: memory, errors and panics, concurrency, unsafe code, interop, and annotations. Appendix
A gives the parts of the standard library the language relies on.

Each rule is stated once, in the section about the construct it governs. Other sections refer to
it.

<a id="introduction.notation"></a>
## 1.3 Notation

<a id="introduction.notation.grammar"></a>
### 1.3.1 Grammar

The syntax is given in Extended Backus-Naur Form:

```
Production  = name "=" [ Expression ] "." .
Expression  = Term { "|" Term } .
Term        = Factor { Factor } .
Factor      = name | token | Group | Option | Repetition .
Group       = "(" Expression ")" .
Option      = "[" Expression "]" .
Repetition  = "{" Expression "}" .
```

A production is an expression built from terms and these operators, in increasing precedence:

```
|   alternation
()  grouping
[]  option (0 or 1 times)
{}  repetition (0 to n times)
```

Names of nonterminals are `PascalCase`. Tokens are quoted: `"class"`, `"->"`. The form `"a" … "z"`
stands for the set of characters from `a` through `z` as alternatives.

The productions of chapter 2 are lexical: they describe a token as a sequence of characters, with
nothing between them. All other productions describe a sequence of tokens, which whitespace and
comments may separate ([Tokens](02-lexical.md#lexical.tokens)).

Each production appears once, in the section that gives its meaning.

<a id="introduction.notation.examples"></a>
### 1.3.2 Examples

Code is Wylow unless it is marked otherwise. In code, `...` stands for code that is left out. An
example assumes the declarations it needs, such as a `Point` struct or a `Node` class.

<a id="introduction.normative"></a>
## 1.4 Normative and informative text

Everything in this specification is normative except:

- a block that starts with `> **Note:**`;
- an example: a block introduced by `**Example:**`, and the code in it;
- the text of a panic's message.

Informative text adds no rule. Where it seems to disagree with normative text, the normative text
applies.

A line of an example that is a compile-time error ends with a comment giving the code of the
diagnostic it produces, as in `// error[WYL0382]`. An example produces exactly the errors it marks.

**Example:**

```java
Buffer kept = data;             // 'Buffer' is a unique struct, so 'data' is moved
print(data.length);             // error[WYL0382]
```

<a id="introduction.terms"></a>
## 1.5 Terms

These terms are used throughout:

- An **implementation** is a compiler and runtime that check, build, and run Wylow programs.
- A **value** is what evaluating an expression produces. Every value has a type.
- A **variable** is storage that holds one value: a local variable, a parameter, a field of an
  object or of a struct value, an element of an array, or a static.
- A **place** is an expression that denotes a variable rather than computing a value: the name of
  a local or parameter, a field or element of a place or of an object, what a reference refers to,
  or the target of a pointer. A place is **mutable** or **read-only**
  ([Mutability](06-variables.md#variables.mutability)).
- An **object** is an instance of a class, made by `new`. An escaping function value also has an
  object, which holds what its lambda captured.
- A **value type** is a type whose values are stored in the variable that holds them, so that
  assigning a value stores a copy of it, or moves it if its type is unique: the primitive types,
  structs, enums, fixed arrays, nullable value types, function types, `String`, and `Weak<T>`.
- A **class reference** is a value of a class or interface type. It refers to an object; copying it
  copies the reference, not the object.
- A **reference** is a `ref`, `in`, `ref?`, or `in?` parameter or variable. It refers to a place
  ([References](17-memory.md#memory.references)).
- A **view** is a value that refers to elements stored elsewhere: a span, or another `ref struct`
  value ([`ref struct`s](07-structs.md#structs.ref)).
- A **temporary** is a value an expression computes that no variable holds, such as the result of a
  call that isn't stored.
- **Releasing** a class reference gives it up. When the last reference to an object is released,
  the object is destroyed ([Objects](17-memory.md#memory.objects)).

These are defined where their rules are:

| Term | Defined in |
|---|---|
| Copyable, unique | [Unique structs](07-structs.md#structs.unique) |
| Defaultable, default value | [Default values](04-types.md#types.defaults) |
| Escaping, non-escaping | [Escaping and non-escaping](12-functions.md#functions.lambdas.escaping) |
| Hold, borrow | [Holds](17-memory.md#memory.holds), [Borrows](17-memory.md#memory.borrows) |
| Move | [Moves](17-memory.md#memory.moves) |
| Narrowing | [Narrowing](16-flow.md#flow.narrowing) |
| Second-class value | [Second-class values](17-memory.md#memory.second-class) |
| Sendable | [Sendable types](19-concurrency.md#concurrency.sendable) |

<a id="introduction.behavior"></a>
## 1.6 Program behavior

<a id="introduction.errors"></a>
### 1.6.1 Compile-time errors

A rule that a program can break, and that can be checked before the program runs, is stated as
"It is a compile-time error if ...". An implementation does not run a program that breaks such a
rule, and reports at least one error for it. Rules about what a program may contain use no other
wording.

An implementation may also report warnings. A warning never makes a program invalid and never
changes what it does.

<a id="introduction.panics"></a>
### 1.6.2 Panics

A rule that a program can break only while it runs is stated as "... panics". When a program
panics, it ends: nothing runs after the panic, on any thread, so no destructor and no deferred
statement runs. The implementation writes a message and the location of the panic to standard
error. Which operations panic, and under which conditions, is normative; the message is
informative ([Panics](18-errors.md#errors.panics)).

<a id="introduction.aborts"></a>
### 1.6.3 Aborts

"The program aborts" is used only for the two failures that aren't panics: exhausting the stack,
and running out of memory. An abort ends the program as a panic does, but reports no location.

<a id="introduction.order"></a>
### 1.6.4 Evaluation order

Operands are evaluated left to right, as [Evaluation order](13-expressions.md#expressions.order)
defines. Other rules say only what is evaluated, never in which order.

<a id="introduction.undefined"></a>
### 1.6.5 Undefined behavior

Outside `unsafe` code, every rule says exactly what happens: no behavior is unspecified or
undefined. A program that breaks a rule of `unsafe` code has **undefined behavior**: this
specification says nothing about what it does
([Undefined behavior](20-unsafe.md#unsafe.undefined)).

<a id="introduction.implementation-defined"></a>
### 1.6.6 Implementation-defined behavior

Only the following are **implementation-defined**: each implementation chooses them, and documents
its choice.

1. The width of pointers on the target: the size of `nint`, `nuint`, and pointer types, 32 or 64
   bits.
2. The target's layout rules for C: the alignment of each type, and so the size of a struct and the
   offsets of its fields ([Layout](07-structs.md#structs.layout)).
3. How deep calls may nest before the stack is exhausted.
4. How much memory a program may use before running out.
5. The exit status of a program that panics or aborts, which is never 0.
6. The bits of a NaN that floating-point arithmetic produces.
7. Which buffered objects a call of `memory::collectCycles` with a budget visits, and so which
   cycles it frees and what `memory::cycleStats()` reports ([Cycles](17-memory.md#memory.cycles)).

<a id="introduction.build-modes"></a>
### 1.6.7 Build modes

A program is built in **debug** or **release** mode. The two behave the same except where a rule
says otherwise: on integer overflow ([Arithmetic](13-expressions.md#expressions.arithmetic)), and in
the performance warnings a debug build writes to standard error
([Cycles](17-memory.md#memory.cycles)).

<a id="introduction.observable"></a>
### 1.6.8 Observable behavior

The **observable behavior** of a run of a program is:

- what it writes to standard output and to standard error;
- its exit status;
- whether it panics, and the location of the panic;
- the order in which destructors run;
- the calls it makes to extern functions, with their arguments, in order.

A run's **inputs** are its arguments, the values extern functions return, and the time as the
program reads it. A program's observable behavior is determined by its source, its inputs, its
build mode, and the implementation-defined properties above. For a program that starts threads, it
is also determined by the order in which the threads' steps interleave
([Concurrency](19-concurrency.md#concurrency)).

An implementation may run a program in any way that gives it the observable behavior these rules
determine. Anything else, such as whether a value is copied, where an object is allocated, or
whether a reference count changes, is not part of the language.

<a id="introduction.anchors"></a>
## 1.7 Section names

Sections are numbered for reading. Each section also has a name, its anchor: lowercase words
separated by dots, starting with its chapter's short name, which is also in the chapter's file
name, so `memory.moves` is in `17-memory.md`. References between sections use the name, never the
number. A section keeps its name when it moves or is renumbered.
