# Spec restructure: where the old text goes

Temporary: the chapter-rewrite sessions (step 4 of the restructure, issue #6) use it, and the last
of them deletes it along with the old files. Each row is a section or paragraph group of an old
file (`02-types.md` to `07-interop.md`, as of `ab97f18`) and its new home, by section name. A new
chapter's text is written from the rows that point at it; an old passage is deleted once its new
home is written.

## Rules for every rewrite

- **Rules only**, in chapter 1's phrasing: "It is a compile-time error if ...", "... panics",
  "the program aborts". No "must" or "cannot" in rules about programs.
- **No comparisons with other languages** ("as in Java", "like C#", "unlike Rust"). If it is a
  reason, it goes in a decision record's *Alternatives* or *Why* (a new record if none covers it),
  naming the exact feature and linking its primary source (CLAUDE.md, "Docs state checked
  facts"); otherwise it is deleted. The same for "so that", "since", "because" clauses that only
  justify.
- **Rewrite, don't copy.** An old passage is source material: restate it in chapter 1's phrasing
  and check it against the tests. A sentence carried over verbatim brings its stale claims with it.
- **No costs**, anywhere (decision 0026): "costs nothing", "one allocation", "O(1)", "a count
  change". Delete them. How something is implemented goes to `docs/design/runtime.md` if it isn't
  there already.
- **No status**: "implemented so far", "not yet", "planned", "for now", "yet". Check that issue #6
  lists it, then delete. A rule that holds only "for now" is stated as the rule, with the plan to
  lift it in #6.
- **Generic examples** (CLAUDE.md): replace game examples (enemies, weapons, meshes, chunks,
  vertices, players, frames, audio) with ordinary ones (`Point`, `Node`, `Order`, `Buffer`,
  `Parser`) as the text is rewritten.
- **One home per rule.** Where the old text states a rule twice (cycles and shared classes, enum
  switches and exhaustiveness), keep it in the home given here and link from elsewhere.
- **Advice** ("use X when ...", "the usual fix is ...") goes to `docs/guide/`.
- **Library behavior** that isn't a declaration the language names (appendix A) goes to the
  declaration's `///` doc comment in `src/Wyl.Compiler.Semantics/Stdlib/` (decision 0004).
- Grammar productions go into the section that gives their meaning, as the rows below place them.
- Each written rule is checked against the tests; write the index pages of its diagnostics in
  step 4 alongside (decision 0003).

## Already written

| New section | From | State |
|---|---|---|
| Chapter 1 | decision 0002, old `01-safety.md` §1.1 | Complete |
| `errors.panics` | old `01-safety.md` §1.3 | Rewritten; check against tests |
| `unsafe.blocks`, `unsafe.aliasing`, `unsafe.unchecked`, `unsafe.threads`, `unsafe.undefined` | old `01-safety.md` §1.4, `05-concurrency.md` §5.8 | Rewritten; `unsafe.pointers` still empty (pointer defaults, `==`, `p[i]`, `p + n`, address-of, casts, `bitcast`) |
| `annotations.allow-large-stack` | old `01-safety.md` §1.5 | Rewritten; the threshold and flag belong to the warning's diagnostics page |
| `library.names` | old README, "Standard library" | Complete; `@Pure` externs are an implementation detail (`runtime.md`) |
| Chapter 2 | old `02-types.md` §2.2, `06-syntax.md` §6.11, `07-interop.md` §7.5 (naming exemption); tokens, comments, identifiers, keywords, and operators from the lexer | Complete, with the pages of its codes |
| Chapter 3 | old `06-syntax.md` §6.1, §6.2 | Complete, with the pages of its codes. Left in `06-syntax.md` for their chapters: `std::panic`, `std::memory` (`library.panic`, `library.memory`), and three §6.2 rules (`classes.overriding`, `structs.constructors.memberwise`, `functions.overloading.resolution`). "Functions or static methods" and "a file of its own" are in the guide |

## 02-types.md

| Old section | Part | New home |
|---|---|---|
| 2.1 Primitives | Table, fixed sizes | `types.primitives` (subsections per kind) |
| | `nint`/`nuint` | `types.primitives.integers`; width is implementation-defined (`introduction.implementation-defined`) |
| | Lengths and indices are `int` | `types.primitives.integers` |
| | `char` is not a number | `types.primitives.char` |
| 2.2 Literals | Table and suffixes | `lexical.literals.integer`, `.float`, `.char`, `.string` |
| | Too large for its type, hex needs `u` | `lexical.literals.integer` (the type rule), `conversions.constants` (fits) |
| | Escapes table, invalid escapes, scalar values | `lexical.literals.escapes` |
| | `$` strings | `lexical.literals.interpolated` (form), `expressions.interpolation` (meaning) |
| 2.3 Conversions | Implicit table and its bullets | `conversions.numeric` |
| | Integer constants | `conversions.constants` |
| | Casts table, `char`/`bool` casts | `conversions.casts` |
| | How `(Name)` is recognized | `expressions.casts` (grammar) |
| | `tryFrom` | `types.primitives.methods` |
| 2.4 Arithmetic | Operand and result types | `expressions.arithmetic.operands` |
| | `++`, `--`, compound assignment | `statements.assignment` |
| | Unary `-`, `~` | `expressions.arithmetic`, `expressions.bitwise` |
| | Shifts | `expressions.shifts` |
| | `char`: comparisons | `expressions.comparison` |
| | `char`: converts to `int` in arithmetic | `expressions.arithmetic.operands` |
| | `char`: in `switch` and patterns | `patterns.kinds` |
| | Overflow, division | `expressions.arithmetic.overflow`; debug and release per `introduction.build-modes` |
| | `wrapping*`, `checked*` | `types.primitives.methods` |
| 2.5 Nullability | Intro, `T?` on every type | `types.nullable` |
| | Table rows: `?.`, `??`, `!` | `expressions.null` |
| | Table rows: `!= null`, `is` | `flow.narrowing`, `expressions.is` |
| | Members only through `?.`, `!`, narrowing | `expressions.members` |
| | Narrowing rules | `flow.narrowing` |
| | `x is T name` binding and scope | `patterns.bindings` (scope), `flow.narrowing` (narrows `x`) |
| | `?.` chaining, `??` typing, `!` on non-nullable | `expressions.null` |
| | `null` converts, `var x = null` | `conversions.nullable`, `variables.locals.var` |
| | Overloads differing only in nullability | `functions.overloading` |
| | `ref T?` vs `ref? T` | `memory.nullable-references` |
| Nullable value types | Inline with a flag, what can be nullable | `types.nullable.values` |
| | Conversions | `conversions.nullable` |
| | Default `null` | `types.defaults` |
| | Operators `==`, `!=`; no lifted operators | `expressions.comparison` |
| | `x is int n` | `patterns.kinds` |
| | `print` accepts nullables | `print`'s doc comment |
| | Changing in place; nothing refers into it | `memory.references` (the rule), `flow.narrowing` (the narrowed local is a variable) |
| | `v!` and `v?.m()` work on a copy | `expressions.null` |
| | Struct containing itself through `?` | `structs.recursive` |
| Nullable type parameters | All | `generics.nullable` |
| 2.6 Type inference | `var` | `variables.locals.var` |
| 2.7 Mutability | Variables, values follow their variable | `variables.mutability` |
| | Class fields | `classes.fields` |
| | Declared without a value, assigned once | `variables.assign-once` (rule), `flow.assign-once` (the check) |
| | Where `mut` goes, where it isn't allowed | `variables.mutability` (grammar) |
| | `ref struct` variables may be `mut` | `structs.ref` |
| | `mut` struct methods | `structs.methods.mut` |
| | `mut` not part of a signature | `classes.overriding` |
| | `const` | `variables.constants` |
| 2.8 Enums | Declaration, variants, methods after `;`, no fields | `enums.declarations` |
| | Value type, shallow copies, no self-containment | `enums.declarations`, `structs.recursive` |
| | Immutable values, no `mut` methods | `enums.methods` |
| | Generic enums, interfaces | `enums.declarations`, `interfaces.structs` |
| | `unique enum` | `enums.unique` |
| | Fields only through patterns | `enums.variants` |
| Creating values | Qualified forms | `enums.variants` |
| | Leaving out the enum, expected types, call arguments | `enums.variants.implicit` |
| | Generic enum inference | `generics.arguments.inference` |
| Equality, defaults, conversions | `==`, not ordered | `enums.equality` |
| | Default is a dataless first variant | `enums.default` |
| | Integer casts | `conversions.casts`, `enums.repr` |
| `switch` | Exhaustive over enums | `patterns.exhaustiveness` (delete the duplicate) |
| Representation | `@Repr` and `@Flags` pointer | `enums.repr`, `enums.flags` |
| 2.9 Structs | Value type, stored inline, copied | `structs.declarations`, `structs.copies` |
| | No copy constructors; only unique structs have destructors | `structs.declarations` (rule); "resource ownership belongs to classes" to the guide |
| | Interfaces only as bounds | `interfaces.structs` |
| | Copies are shallow | `structs.copies` (rule); already in the guide's list |
| Unique structs | Moves where copies would be | `memory.moves` |
| | Use after move is an error | `flow.moves` |
| | Containing types must be `unique`; `T[N]`, `T?`, generic instances | `structs.unique` |
| | Not `Copyable` | `types.copyable` |
| | Destructor, `discard`, read-only `this` ("Destructors of unique structs") | `structs.unique`; decision 0028 |
| | `clone()` convention | the guide |
| Values in use | The panic on releasing a value in use | `memory.holds`; decision 0028 |
| Where copies happen | The list of where | `structs.copies` |
| | How to avoid a copy | the guide |
| Layout | Declaration order, C alignment, `@Align`, `@Packed` | `structs.layout` (alignment is implementation-defined) |
| 2.10 Classes | Heap, counted, inheritance | `classes.declarations` |
| | Keyword table | `classes.overriding`, `classes.abstract` |
| | Non-null references | `types.nullable` |
| | Destructors | `classes.destructors` |
| | "Standard library collections are classes" | delete |
| Inheritance rules | Upcasts | `conversions.identity` |
| | Downcasts through `is` | `patterns.kinds` |
| | `override` matching, no hiding, `super.method` | `classes.overriding` |
| | Implementing abstract and interface methods | `classes.abstract`, `interfaces.implementing` |
| | Generic methods not virtual | `classes.overriding` |
| | Implicit parameterless constructor | `classes.constructors` |
| | Visibility | `programs.visibility` |
| Construction | Steps 0 to 3 | `classes.construction` |
| | Fields needing a value, `this` before they are assigned | `classes.construction` (rule), `flow.constructors` (the check), `structs.constructors` (same for structs) |
| | Base class while constructing and destroying | `classes.construction`, `classes.destructors` |
| Strings | Immutable UTF-8 value type, default, `extern struct` | `types.string` |
| | Interpolation joins, no `+`, `StringBuilder` | `types.string` (no `+`); `StringBuilder` to #6 |
| | Literals don't allocate, built strings are counted | delete (implementation) |
| | `==` compares contents | `types.string` |
| | Byte offsets and lengths | `types.string` |
| | Methods (`byteLength`, `charCount`, `slice`, `indexOf`, `startsWith`, `compareTo`, `bytes`) | `string.wyl` doc comments |
| | No indexer; `bytes()` holds the string; no view of a string field | `types.string`, `memory.holds` |
| | For-each over a string | `statements.for-each` |
| 2.11 Arrays and spans | Table | `types.kinds` |
| | Bounds checks | `expressions.indexing` |
| | `T[]` never reallocates | `types.arrays` |
| | Implicit span conversions | `conversions.spans` |
| | Span rules, borrowing a list | `types.spans`, `structs.ref`, `memory.borrows` |
| Fixed arrays | Dimensions, value semantics, `length`, constant index | `types.fixed-arrays` |
| | Array literals | `expressions.construction` |
| | Default value; locals hold the default | `types.defaults`, `flow.definite-assignment` (the exception) |
| Heap arrays | `Array<T>`, `new T[n]`, default needed, `T?[]`/`T[]?`, `length` | `types.arrays`, `expressions.construction` |
| Lists | All | `list.wyl` doc comments; the borrow panic in `memory.borrows` |
| Spans | `ref struct`s, read-only indexer | `types.spans` |
| | Conversions | `conversions.spans` |
| | Inference from a span parameter | `generics.arguments.inference` |
| Queries | All | issue #14 (iteration design); delete |
| `Box<T>` | All | delete; #6 lists it, its doc comment comes with it |
| 2.12 Interfaces | On classes, on structs | `interfaces.declarations`, `interfaces.structs` |
| | Contents, extending | `interfaces.declarations` |
| | Implementing, visibility, not overridable | `interfaces.implementing` |
| | Duplicates | `interfaces.declarations` |
| | `mut` methods | `interfaces.mut` |
| 2.13 Generics | Checked against bounds | `generics.checking` |
| Declaring and using | Type parameter placement, names | `generics.parameters` |
| | Inference, struct type arguments written | `generics.arguments`, `generics.arguments.inference` |
| | Generic vs non-generic overload | `functions.overloading.resolution` |
| | `Name<...>` read as type arguments | `expressions.primary` (grammar) |
| Bounds | Interfaces only, nullable doesn't satisfy, no conversion | `generics.bounds` |
| | Built-in types satisfy `Printable`, `Comparable` | `library.printable`, `library.comparable` |
| | `Copyable` | `types.copyable` |
| | `Comparable` example, float total order | `library.comparable` |
| Unbounded type parameters | All | `generics.unbounded` |
| Default values | Defaultable types, defaults | `types.defaults` |
| Recursive structs | All | `structs.recursive`, `generics.recursion` (growth without end) |

## 03-memory-model.md

| Old section | Part | New home |
|---|---|---|
| Intro and table | Kinds of storage | `memory.storage` |
| 3.1 Values | Inline, copied, no destructors | `memory.storage` |
| Moves | What moves, the source holds nothing | `memory.moves` |
| | Only whole locals and parameters; `std::replace`, `std::swap` | `memory.moves`; the two functions' doc comments |
| | Use after move, loops | `flow.moves` |
| | Moved variable not released, the flag | `memory.moves` (the flag is `runtime.md`) |
| | Unique operand used in place | `expressions.order` |
| 3.2 Objects | `new`, counted, destructor on last release | `memory.objects` |
| When references are released | Locals, parameters, fields; destruction order | `memory.release.destruction` |
| | Arrays and `List` release their elements last to first | `memory.release.destruction` (arrays); `list.wyl` doc comment (`List`); decision 0031 |
| | Closures and choices release order | `memory.release.destruction` |
| | Temporaries (all bullets) | `memory.release.temporaries` |
| | Assignment stores before releasing | `memory.release` |
| | The compiler may release earlier | delete (`introduction.observable` covers it); holds' order to `memory.holds` |
| | Destructor keeping its object panics | `classes.destructors` |
| Weak references | All rules | `memory.weak` |
| | `T` converts to `Weak<T>` | `conversions.weak` |
| | Sendable when shared | `concurrency.sendable` |
| | When to use one | the guide |
| Cycles | Never runs on its own, threads free their own | `memory.cycles` |
| | The three functions | `library.memory` |
| | Determinism, budgets | `memory.cycles`; the budgeted reach is implementation-defined (`introduction.implementation-defined` item 7; #6 asks whether to keep that) |
| What the collector looks at | Cyclic classes, function fields, generics, buffering, trial deletion | `memory.cycles.collected` (what is buffered and freed); the algorithm is `runtime.md` |
| | `@Acyclic` | `memory.cycles.collected`, listed in `annotations.list` |
| | Shared classes aren't collected | `concurrency.shared-classes` (one home; link from here) |
| Destroying a cycle | All | `memory.cycles.destroying` |
| | Collection is always safe | `memory.cycles` |
| Diagnostics | Debug-build performance warnings | `memory.cycles.diagnostics` (that they exist and when; text informative) |
| 3.3 References | `ref T`, `in T`, second-class table | `memory.references` |
| | Initialized with a place, not a class object | `variables.references` |
| Returning references | All | `functions.ref-returns` |
| Nullable references | All | `memory.nullable-references` |
| | Conversions from `ref` | `conversions.references` |
| | `is ref` | `patterns.references`; how long its hold lasts to `memory.holds` |
| | `?.` on a `ref?` | `expressions.null` |
| | Overloads `ref` vs `ref?` | `functions.overloading` |
| | "Because a reference cannot outlive ..." | `memory.holds` (intro) |
| Pinning class objects | Holding the object of a place and a receiver | `memory.holds` |
| | `ref`, `in`, span variables hold for their scope | `memory.holds` |
| Evaluation order and the heap | All | `expressions.order` (state plain left-to-right; the "conflicts with every call" wording describes the compiler) |
| Borrow counters | Borrow count, table, examples, how long a borrow lasts | `memory.borrows`; `List`'s panicking operations in `list.wyl` doc comments |
| | "The usual fix" | the guide |
| `Borrowable` | All | `memory.borrows.borrowable` |
| Spans | `ref struct` rules | `structs.ref` |
| Second-class values | All | `memory.second-class` |

## 04-errors.md

| Old section | Part | New home |
|---|---|---|
| Intro table | Panic vs `Result` | delete (`errors` chapter intro links both) |
| 4.1 `Result` | Declaration, seen without import | `errors.result` |
| | `Ok`/`Err` where the type is known | `enums.variants.implicit` (delete the duplicate) |
| | Error types example | `errors.result` (example, rewritten generic) |
| 4.2 Results must be used | Unused `Result` | `errors.unused` |
| | `_ =` | `statements.expressions.discard` |
| 4.3 `try` | All | `expressions.try` |
| 4.4 Handling results | `switch`, `is` | `errors.handling` |
| | Helper methods table | `result.wyl` doc comments |

## 05-concurrency.md

| Old section | Part | New home |
|---|---|---|
| Intro | What the language adds | `concurrency` (chapter intro), without the library list |
| 5.1 Thread confinement | All | `concurrency.confinement` |
| 5.2 Sendable types | Table, inferred, errors | `concurrency.sendable` |
| | Moving and sharing, the borrow panic on moving | `concurrency.sendable.moving` |
| | The library floor declaring sendable | `concurrency.sendable` (how is open, #6) |
| 5.3 `shared class` | All, including cycles among them | `concurrency.shared-classes` |
| 5.4 Shared functions | All | `concurrency.shared-functions` |
| | Conversion to non-`shared` | `conversions.functions` |
| 5.5 Statics | All | `variables.statics` |
| | Initialized before `main`: order, on demand, cycles, no threads | `variables.statics`; `programs.execution` links it; decision 0030 |
| 5.6 Intro | Output order may vary | `concurrency.interleaving` |
| Threads, Channels, Locks and atomics, Frozen arrays, Parallel work | Library behavior | issue #14 (the stdlib design brief) until the library implements them, then their doc comments; the one-lock panic is library behavior too |
| Isolated object graphs | All | `concurrency.isolated`; its cycle buffer in `memory.cycles` (link) |
| | A mutex holds one | `concurrency.isolated` (one sentence), the rest with `Mutex` in #14 |
| 5.7 Panics | All | `concurrency.panics` |

## 06-syntax.md

| Old section | Part | New home |
|---|---|---|
| Intro | "Syntax is Java's unless stated" | delete |
| 6.1 Packages, modules | Layout example, packages | `programs.packages` |
| | Modules are directories | `programs.modules` |
| | A module's names, overloads across files | `programs.names` |
| | Within a module, any number of declarations | `programs.names` |
| Imports | Forms, path start, scope, `pub import` | `programs.imports` |
| | `::` vs `.`, module namespace, paths start at a seen module | `programs.paths` |
| | One name, one meaning; `*` | `programs.imports` |
| | No prelude; the language's own types | `programs.builtin-names` |
| | `std::panic` | `library.panic` |
| | `std::memory` | `library.memory` |
| Functions or static methods | All | the guide |
| Entry point | All | `programs.entry` |
| | The end of the program: statics not destroyed, threads stopped | `programs.execution`; decision 0029 |
| The manifest | All | `programs.manifest` |
| 6.2 Visibility | Levels table, modules don't nest | `programs.visibility` |
| | No type-private level, no `protected` | `programs.visibility` (rules; the "goes in a file of its own" advice to the guide) |
| | Overrides keep their visibility | `classes.overriding`, `interfaces.implementing` |
| | Visible signatures | `programs.visibility.signatures` |
| | Memberwise constructor visibility | `structs.constructors.memberwise` |
| | Overloads chosen among visible ones | `functions.overloading.resolution` |
| | Entry point, members in any order, modifier order, destructor and variants take none | `programs.visibility` |
| 6.3 Construction | `new` only for heap | `expressions.construction` |
| | Locals assigned before read | `flow.definite-assignment` |
| Struct constructors | All | `structs.constructors`, `.memberwise`, `.default` |
| Class constructors | All | `classes.constructors` |
| 6.4 Parameter modes | Table, `ref`/`in` rules, unique struct modes | `functions.parameters.modes` |
| | Read-only places | `variables.mutability` |
| | Arguments left to right | `expressions.order` |
| `mut` struct methods | All, temporaries | `structs.methods.mut` |
| Overloading, defaults, named arguments | Defaults, named arguments | `functions.arguments` |
| | Overload resolution | `functions.overloading.resolution` |
| | Overrides take defaults | `classes.overriding` |
| | Function types have no names or defaults | `types.functions` |
| Extension methods | All | `functions.extensions` |
| 6.5 No properties | All | the guide (an absence needs no rule) |
| 6.6 Indexers | No operator overloading | `functions.indexers` (intro) |
| | `operator []`, `operator []=` | `functions.indexers`, `.setters` |
| Equality | Table | `structs.equality`, `classes.equality`, `enums.equality`, `types.string`; `Equatable` to #14 |
| 6.7 Lambdas | Function type syntax | `types.functions` |
| | Lambda parameters, body, typing, inference, overloads | `functions.lambdas.typing` |
| | Calling function values | `expressions.calls` |
| | Function references | `functions.references` |
| | `?:` and switch choosing functions | `functions.choices` |
| | No default, no `==` | `types.functions`, `types.defaults` |
| | Definite assignment and narrowing in lambdas | `flow.lambdas` |
| Escaping and non-escaping | Table (minus the cost row, already gone), rules, nullable functions | `functions.lambdas.escaping` |
| | Captures, moving uniques | `functions.lambdas.captures` |
| | Choices | `functions.choices` |
| | Signatures | `classes.overriding`, `functions.overloading` |
| | Type patterns can't name function types | `patterns.kinds` |
| | A stored function value is held during its call | `memory.holds` |
| Lambdas held by `ref struct`s | All | issue #14 (lazy queries); delete |
| 6.8 `defer` | All | `statements.defer` |
| 6.9 Control flow | Assignment is a statement | `statements.assignment` |
| | Evaluation order | `expressions.order` |
| | Unreachable statements | `flow.returns` |
| | Mandatory braces | `statements.blocks` (grammar) |
| | Ranges | `statements.for-each.ranges` |
| | For-each with keys | `statements.for-each`, `.indices`; the `Map` row is deleted until `Map` exists |
| `switch` | Arrow form, no fallthrough, `case A, B` | `patterns.cases` |
| | Guards, the "watch out" | `patterns.cases.guards` (rule: matches in place); the advice to the guide |
| | `case null` | `patterns.cases.null` |
| | Statement vs expression cases | `statements.switch`, `expressions.switch` |
| | Exhaustive, unreachable cases | `patterns.exhaustiveness` |
| | `break` and `continue` in a case | `statements.jumps` |
| | Value evaluated once, its type, temporaries | `patterns.matching`, `memory.release.temporaries` |
| Patterns | Table, literals, lone names, struct patterns | `patterns.kinds` |
| | `x is pattern` | `expressions.is`, `patterns.matching` |
| | Bindings stored to existing locals, copied out first | `patterns.bindings` |
| Reference bindings | All | `patterns.references` |
| String interpolation | All | `expressions.interpolation` |
| | `Printable` | `library.printable` |
| | Primitives' `toString()` | `types.primitives.methods` |
| | `print` | `print`'s doc comment |
| Format specifiers | All | `expressions.interpolation.format` |
| 6.10 Annotations | Syntax, arguments, `fn` parentheses | `annotations.syntax` |
| | Table | `annotations.list` (each linking to its home) |
| | An annotation never changes a type | `annotations.syntax` |
| `@CallerLocation` | All | `annotations.caller-location` |
| 6.11 Naming conventions | Table | `lexical.naming` (informative: warnings aren't part of the language) |
| 6.12 Complete example | All | delete, or a generic example in the guide |

## 07-interop.md

| Old section | Part | New home |
|---|---|---|
| Intro | Declaring and marking | `interop` (chapter intro), without the "no FFI" explanation |
| 7.1 `extern` blocks | Declarations, `@Include`, manifest `native` | `interop.extern` |
| | `extern struct` (the quoted paragraph) | `interop.extern.extern-structs` |
| C++ names | `@CppName`, no templates | `interop.extern.names` |
| 7.2 Safety | Extern calls need `unsafe`, `@Safe`, constants | `interop.safety` |
| 7.3 The wrapper pattern | All | the guide |
| 7.4 Blittable types | Table | `interop.blittable` |
| Layout verification | The build fails on a mismatch | `interop.blittable.layout` |
| 7.5 Enums with integer representations | `@Repr`, values, casts | `enums.repr` |
| | `@Flags` | `enums.flags` |
| | Naming warning exemption | `lexical.naming` |
| 7.6 Strings | All | `interop.strings` |
| Buffers | All | `interop.buffers` |
| Callbacks | All | `interop.callbacks` |
| 7.7 C++ exceptions | All | `interop.exceptions` |
| 7.8 Native C++ files | All | `interop.native` |
| 7.9 Exporting | All | `interop.export` |

## Sessions

Issue #6 groups the rewrites: (2, 3), (4–6), (7–11), (12–15), (16–17), (18–22 and appendix A).
Each session writes its chapters from the rows above, deletes the old passages, fixes links into
them (search `docs/`, `src/`, `tests/cases/` with a glob that skips `tests/.out`), and removes the
old file once it is empty. The last one deletes this file and the README's "Chapters being
rewritten" list.
