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
