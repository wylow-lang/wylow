# 0002. The spec's outline, anchors, grammar, and phrasing

- Status: accepted (2026-11-01)
- Spec: all; the conventions are written in chapter 1

## Decision

### Outline

Chapters follow the language's structure, as the [JLS's](https://docs.oracle.com/javase/specs/jls/se27/html/index.html) do, one file each:

1. Introduction: scope, notation, conventions, terms
2. Lexical structure
3. Programs, packages, and modules: the manifest, imports, visibility, the entry point
4. Types
5. Conversions
6. Variables and constants
7. Structs
8. Classes
9. Enums
10. Interfaces
11. Generics
12. Functions and methods: parameters, overloading, extension methods, indexers, lambdas
13. Expressions, including evaluation order
14. Statements
15. Patterns
16. Flow rules: definite assignment, narrowing, moves, reachability
17. Memory and ownership
18. Errors and panics
19. Concurrency
20. Unsafe code
21. Interop
22. Annotations

Appendix A: the standard library types the language names.

### Numbering and anchors

Sections are numbered (`17.3.2`) for reading. Every heading also has a named anchor
(`<a id="memory.moves"></a>`), lowercase, dot-separated, starting with the chapter's short name.
Links, the diagnostics catalog, and decision records use anchors, never numbers, so renumbering
breaks nothing. An anchor, once published, is kept when its section moves.

### Grammar

Productions are inline, in the section that gives their meaning, as in the
[Go spec](https://go.dev/ref/spec), with no separate appendix copy. The notation is
[Go's EBNF](https://go.dev/ref/spec#Notation), without its `…` ranges in productions:

```
Production  = name "=" [ Expression ] "." .
Expression  = Term { "|" Term } .
Term        = Factor { Factor } .
Factor      = name | token | Group | Option | Repetition .
Group       = "(" Expression ")" .
Option      = "[" Expression "]" .
Repetition  = "{" Expression "}" .
```

Nonterminals are `PascalCase`, tokens are quoted. A test collects every production in the spec and
checks that each name is defined exactly once and every name used is defined.

### Phrasing

Chapter 1 defines these, and every rule uses them:

- **Compile-time errors**: "It is a compile-time error if ...". Rules about programs never use
  "must" or "cannot" instead.
- **Run-time failures**: "... panics". When a program panics is normative; a panic's message text
  is informative. "The program aborts" is reserved for failures that aren't panics: stack overflow
  and running out of memory.
- **Evaluation order**: stated once, in chapter 13: operands are evaluated left to right. Other
  rules say only what is evaluated.
- **Nothing is unspecified.** Outside `unsafe` code, every program has one behavior. The term
  "implementation-defined" is used only for the closed list chapter 1 gives (pointer width, stack
  depth, memory limits). "Undefined behavior" exists only in `unsafe` code.
- **Observable behavior** is defined in chapter 1: standard output and error, the exit code, a
  panic and its location, the order destructors run in, and calls to extern functions. An
  implementation may do anything that doesn't change it.
- **Normative and informative**: everything is normative except blocks marked `> **Note:**` and
  `**Example:**`. An erroneous example marks its line with the diagnostic's code:
  `// error[WYL0382]`.
- **No costs, status, rationale, or implementation** ([0001](0001-document-set.md)).

## Alternatives

- **Topic chapters** (the spec until now). Finding a rule needs knowing which topic someone filed
  it under; structure-based chapters have one obvious place per construct.
- **Grammar in an appendix**: the JLS repeats its syntactic grammar in
  [chapter 19](https://docs.oracle.com/javase/specs/jls/se27/html/jls-19.html), the C# standard collects its productions in a
  [grammar annex](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/grammar),
  and the Rust Reference has a [grammar summary](https://doc.rust-lang.org/reference/grammar.html).
  Two copies of every production; with only one, the test above gives what the appendix would.
- **Other notations**: the [JLS's](https://docs.oracle.com/javase/specs/jls/se27/html/jls-2.html#jls-2.4) (alternatives on separate lines, hard
  to write in Markdown), ANTLR, which the C# standard uses (a tool's syntax, not a reader's). Go's
  is the smallest that reads well.
- **Section numbers as link targets.** Every renumbering breaks every link.
- **Per-rule identifiers**, like the Rust Reference's
  [`items.fn.generics`](https://doc.rust-lang.org/reference/items/functions.html#r-items.fn.generics),
  so a test can cite the
  exact rule it checks. Finer than needed now; can be added on top of the section anchors later.
- **"Unspecified" behavior**, such as the order C++ initializes a call's parameters in
  ([\[expr.call\]/8](https://eel.is/c++draft/expr.call#8): "indeterminately sequenced"), where Java
  fixes it ([JLS 15.7.4](https://docs.oracle.com/javase/specs/jls/se27/html/jls-15.html#jls-15.7.4)). Observable behavior must not depend on what
  the compiler chose, and the price of defining it is small.
- **Costs as normative guarantees**, like the *Complexity* element of the C++ standard library's
  specifications
  ([\[structure.specifications\]/3](https://eel.is/c++draft/structure.specifications#3)). They
  describe the current implementation and its optimizations, which are invisible by design.

## Why

The JLS and the Go spec both order their chapters by construct, so a reader of either finds the
structure they expect. Fixed phrasing makes each kind of rule recognizable at a glance and greppable: every compile-time
error rule matches one phrase, which step 3 of the restructure maps to diagnostic codes. Anchors
keep links stable through the rewrite and beyond it.
