# Decision records

One file per decided design question: what was decided, what else was considered, and why. The
spec (`docs/spec/`) states only the rules; the reasons live here, so a rule can be read without its
history and a reason can be found without searching the spec.

- **Name**: `NNNN-short-slug.md`, numbered in order. A number is never reused.
- **When**: a design decision gets a record when it is made. A question still open is a GitHub
  issue (or an entry in the roadmap, issue #6), not a record; it becomes a record once decided.
- **Changes**: once accepted, a record's text doesn't change, except its status. A decision that
  replaces it is a new record, and the old one's status becomes "superseded by NNNN".
- **Form**:

```markdown
# NNNN. Title

- Status: accepted (YYYY-MM-DD) | superseded by NNNN (YYYY-MM-DD)
- Spec: the sections it decides, by anchor (`memory.moves`)

## Decision

## Alternatives

## Why
```

## Records

| # | Decision |
|---|---|
| [0001](0001-document-set.md) | The document set: spec, decision records, implementation docs, diagnostics index, guide |
| [0002](0002-spec-structure-and-conventions.md) | The spec's outline, anchors, grammar notation, and fixed phrasing |
| [0003](0003-diagnostics.md) | Diagnostics: style, codes, and the catalog that enforces them |
| [0004](0004-doc-comments.md) | Doc comments: `///` Markdown, Javadoc style, fixed sections |
| [0005](0005-design-principles.md) | Design principles (superseded by 0027) |
| [0006](0006-lengths-are-int.md) | Lengths and indices are `int` |
| [0007](0007-float-literals.md) | An unsuffixed floating-point literal is a `float` (superseded by 0041) |
| [0008](0008-arithmetic-keeps-operand-types.md) | Arithmetic keeps its operands' type |
| [0009](0009-unique-values-and-moves.md) | Unique structs and enums, and moves |
| [0010](0010-construction-sees-base-class.md) | While a base class constructs, the object is that class |
| [0011](0011-string-value-type.md) | `String` is a built-in value type of UTF-8 bytes |
| [0012](0012-nullable-references.md) | Nullable references, `ref? T` |
| [0013](0013-try-prefix.md) | `try` is a prefix keyword |
| [0014](0014-panicking-and-non-panicking.md) | Panics are for bugs; the library has non-panicking versions |
| [0015](0015-threading-model.md) | Threading model: confinement, one sendable fact, no `static mut` |
| [0016](0016-channels-and-locks.md) | Channels and locks |
| [0017](0017-isolated-graphs.md) | Isolated object graphs |
| [0018](0018-temporaries-are-read-only.md) | Temporary values can't be written |
| [0019](0019-function-values.md) | Non-escaping functions are second-class values |
| [0020](0020-lambda-typing.md) | How lambdas are typed |
| [0021](0021-switch-matches-in-place.md) | A switch matches its value in place |
| [0022](0022-construction-syntax.md) | Construction: `new` only for the heap, one way to build a struct |
| [0023](0023-assignment-is-a-statement.md) | Assignment is a statement |
| [0024](0024-printing.md) | Printing: opt-in `toString`, no cultures |
| [0025](0025-unsafe-permits.md) | `unsafe` permits operations and never removes checks |
| [0026](0026-no-cost-documentation.md) | Costs are not documented |
| [0027](0027-design-principles.md) | Design principles |
| [0028](0028-destructors-on-unique-structs.md) | Destructors on unique structs |
| [0029](0029-statics-are-not-destroyed.md) | Statics are not destroyed at the end of the program |
| [0030](0030-statics-are-initialized-before-main.md) | Statics are initialized before `main`, on one thread |
| [0031](0031-elements-are-released-last-to-first.md) | Elements are released last to first |
| [0032](0032-no-statics-in-generic-types.md) | Generic types have no static fields |
| [0033](0033-source-text-and-columns.md) | Source files are strict UTF-8, and columns count characters |
| [0034](0034-language-names-are-reserved.md) | The names every file sees mean only their declarations |
| [0035](0035-main-arguments-and-exit-status.md) | `main` may take the arguments and return the exit status |
| [0036](0036-type-properties.md) | `copyable` and `sendable` are type properties, not interfaces |
| [0037](0037-interfaces-the-language-calls.md) | Interfaces the language calls are ordinary interfaces |
| [0038](0038-built-in-types-declare-their-interfaces.md) | The built-in types declare their interfaces in the standard library |
| [0039](0039-loop-replaces-do.md) | `loop` replaces `do`, and a `for` has a condition |
| [0040](0040-defer-rules.md) | A deferred statement cannot jump out |
| [0041](0041-double-literals.md) | An unsuffixed floating-point literal is a `double` |
| [0042](0042-unit.md) | A `Result` with no success value uses `Unit` |
| [0043](0043-no-constants.md) | No constant declaration and no top-level variables |
| [0044](0044-moved-value-destructor.md) | A moved value's destructor runs on the thread that releases it |
| [0045](0045-byte-pointers-follow-the-rule.md) | A `byte*` has no exception to the storage rule |
| [0046](0046-nan-bits-and-budgets.md) | NaN bits and budgeted collection stay implementation-defined |
| [0047](0047-standard-output-buffering.md) | Standard output is buffered, and an abort may lose what is not yet written |
| [0048](0048-long-chains-are-released-by-their-destructor.md) | A long chain of objects is released by a destructor that loops |
| [0049](0049-getting-a-unique-value-out.md) | Getting a unique value out: moving bindings, `move` methods, `replace`, and references on classes |
| [0050](0050-by-value-receivers.md) | By-value receivers replace `move` methods |
| [0051](0051-visibility-is-one-word.md) | Visibility is one word: `module`, `package`, or `pub` |
| [0052](0052-member-bounds.md) | A member may bound its type's type parameters |
| [0053](0053-statics-initialization-and-storage.md) | Statics: one order, one initializer, storage never destroyed |
| [0054](0054-shared-classes.md) | Shared classes: a count for any thread, the global heap, and no collection |
| [0055](0055-shared-functions.md) | Shared functions: four forms, shared closures, and no collection |
| [0056](0056-threads.md) | Threads: a unique handle, a free `spawn`, and a by-value `join` |
| [0057](0057-interpreter-interleaving.md) | The interpreter runs the operating system's threads |
| [0058](0058-sendable-handle.md) | A standard library type the compiler knows is sendable |
| [0059](0059-channel.md) | Channels: an unbounded queue, ends that count, and a close that drains |
| [0060](0060-isolated-storage.md) | `Isolated<T>`: a value beside a runtime buffer, and `take` through an extern |
| [0061](0061-isolated-cycle-buffer.md) | An isolated graph's cycle buffer |
| [0062](0062-isolated-api.md) | The `Isolated<T>` API: one `open`, `Unit` for nothing, and a by-value `take` |
| [0063](0063-isolated-conversion.md) | A sendable value converts to a box, and a box is sendable for any `T` |
| [0064](0064-constructors-start-from-storage.md) | A constructor starts from empty storage, so a field with a destructor must be assigned |
| [0065](0065-initial-value-is-the-default.md) | A field's initial value is its type's default value, which a constructor stores when it is more than empty storage |
| [0066](0066-threading-module.md) | One module, `std::threading`, for the types threads use |
| [0067](0067-mutex.md) | `Mutex<T>`: an isolated graph behind a lock |
| [0068](0068-one-lock-check.md) | The lock rule is one flag per thread, which every wait reads |
| [0069](0069-atomic.md) | `Atomic<T>` for integers, `AtomicBool`, and one memory order |
| [0070](0070-frozen.md) | `Frozen<T>`: an immutable list that `freeze` moves a list's elements into |
| [0071](0071-unit-return-is-implicit.md) | A function that returns `Unit` may leave out its return |
| [0072](0072-std-is-a-path-root.md) | Every file sees `std` as a path root, and code calls functions through their module |
| [0073](0073-suppress-warnings.md) | A warning for a bare call of an imported function, and `@SuppressWarnings` |
| [0074](0074-list-isolate.md) | A list moves to another thread through `isolate`, and `List<T>` stays the only growable array |
| [0075](0075-iterators.md) | Iteration: `Iterator<T>` with `moveNext` and `current`, struct iterators through bounds, adapters as extension methods |
| [0076](0076-element-iterators.md) | Element iterators `RefIterator` and `InIterator`, and returned references that hold what they refer into |
| [0077](0077-inference-through-bounds.md) | A bound determines the type parameters it mentions, so `<I extends Iterator<T>, T>` infers `T` |
| [0078](0078-extern-struct-bodies.md) | A method of an extern struct may have a body, and `String`'s methods are written in Wylow over a native floor |
| [0079](0079-iteration-module.md) | The module `std::iteration` declares the iterator interfaces and their adapters and terminal operations |
| [0080](0080-extension-lookup.md) | An extension call looks past the places that have no extension of its name for the receiver |
| [0081](0081-overloads-by-bounds.md) | Overloads may differ only in their bounds, and resolution drops an overload whose bounds fail |
| [0082](0082-mutable-current-and-container-ref-iterators.md) | `current` is `mut`, containers give a `RefIterator`, and the shapes of the adapters |
| [0083](0083-invisible-members-do-not-hide-extensions.md) | A member that a call cannot see does not hide an extension |
| [0084](0084-extern-blocks-are-the-one-extern-form.md) | Extern blocks are the only extern declaration, the standard library uses them too, and signatures are checked |
| [0085](0085-c-structs-unions-and-enums.md) | Structs and unions in extern blocks are the C++ types, and enums cross only as parameters |
| [0086](0086-native-manifest-block.md) | The manifest's `native` block, and a package's `native/` directory |
| [0087](0087-extern-calls-are-not-interpreted.md) | The interpreter runs only the extern functions it implements |
| [0088](0088-memory-compare-and-the-string-floor.md) | `memory::compare` and `memory::equal`, and the native floor of `String` |
| [0089](0089-collections-module.md) | The module `std::collections` holds `List<T>` and the other collections; `Array<T>` stays in the root |
| [0090](0090-equatable-and-hashable.md) | `Equatable`, `Hashable`, and `Hasher`; `Comparable` extends `Equatable`; the built-in types implement `Hashable` |
| [0091](0091-written-equality.md) | The compiler writes `equals` and `hash` for a struct or enum that implements `Equatable` or `Hashable` without them |
| [0092](0092-unsafe-interfaces-and-element-owners.md) | Unsafe interfaces, and `memory::ElementOwner` reports the values a class keeps in raw memory to the cycle collector |
| [0093](0093-hash-map-and-hash-set.md) | `HashMap` and `HashSet`: concrete classes, open addressing with linear probing, and `HashMap` in the unsafe floor |
| [0094](0094-unique-structs-backed-by-cpp-classes.md) | The standard library may declare a struct backed by a C++ class `unique` |
| [0095](0095-native-paths-must-exist.md) | Each path that the manifest's `native` block names must exist |
| [0096](0096-void-pointers-and-byte-lengths.md) | `void*` converts from every pointer, and `byteLength()` is an `int` that panics on overflow |
| [0097](0097-c-function-pointer-types.md) | A C function pointer is an `extern fn` type, and only a `@CCallback` function converts to it |
| [0098](0098-supported-toolchains.md) | Each supported C++ compiler has an oldest version, and clang-cl comes from Visual Studio |
| [0099](0099-c-native-sources.md) | A package's `native/` directory may hold C sources, which compile as C17 into kept objects |
| [0100](0100-repr-enum-results.md) | An extern function may return a `@Repr` enum, and the call panics on an unknown value |
| [0101](0101-dependencies-libraries-and-versions.md) | Dependencies are local paths, a library declares its kind and version, and a version is a check |
| [0102](0102-statics-as-patterns.md) | A static whose initializer is a literal is a pattern, and an extern static may give its value |
| [0103](0103-header-search.md) | An extern block's header is in its own package, or is a system header written in angle brackets |
