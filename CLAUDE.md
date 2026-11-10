# Wylow

A personal, general-purpose language: C++-level control and no undefined behavior by default. The
compiler is C# (.NET 10) and compiles Wylow to C++20.

## Read first

- `docs/spec/`: the language definition and the source of truth; start at its `README.md`. It
  states rules only: no rationale, implementation, costs, or status (decision 0001). A design
  decision puts its rules in the spec and its reasons in a decision record
  (`docs/design/decisions/`, see its README).
- `docs/design/architecture.md`: the compiler's layers, where the code is, and how each rule is
  enforced. Check every change against it: a question that seems to need a second answer in
  another layer means the work is in the wrong layer.
- `docs/design/testing.md`: the oracles and harnesses, how to run them, triage, and the backlog.
- `docs/diagnostics/README.md`: how error messages are written; follow it in every new message.
- `docs/design/working.md`: how we work: design principles, design sessions, handoffs, long runs.
- `docs/handoff/`: task prompts for upcoming work. Project knowledge goes in committed files, not
  assistant memory, so it reaches every machine.

## Everyday commands

```
dotnet build
dotnet tests/Wyl.Tests/bin/Debug/net10.0/Wyl.Tests.dll --interpret         # whole suite, interpreter only (~3 min)
dotnet tests/Wyl.Tests/bin/Debug/net10.0/Wyl.Tests.dll structs             # clang, tests whose path contains "structs"
dotnet tests/Wyl.Tests/bin/Debug/net10.0/Wyl.Tests.dll matrix --update-snapshots
dotnet src/Wyl.Cli/bin/Debug/net10.0/wyl.dll run|build|check|emit|ir|interpret <file.wyl | dir> [--release] [--cc clang|msvc|gcc|asan|tsan] [--no-nest] [--no-opt]
dotnet publish src/Wyl.Cli -o bin/wyl                                     # Release, ahead-of-time compiled: bin/wyl/wyl.exe
```

Other modes, the fuzzer, the generator, mutation testing, coverage, and the dashboard:
`docs/design/testing.md`.

## Writing tests

`tests/cases/**/*.wyl`: each test is a program with `// output:`, `// error[WYLnnnn]: <message>` (on the
erroring line; list every error, with its code and exact message), `// warning[WYLnnnn]: <message>` (likewise
for every compile-time warning: one no marker lists fails the test), `// warning: <text>` (a runtime
performance warning), `// panic:` or `// panic-here:` (also checks the line),
`// args:` (the program's arguments, split at spaces), `// exit:` (its exit status, otherwise 0),
`// mode: release` comments, and `// tsan` on a test that starts a thread, which is the only kind
`--cc tsan` runs. Outputs match in file order, so put a called function's output at the
call. Binder errors and flow errors go in separate files (flow analysis runs only on a program that
binds). A test imports the types it uses and calls functions through their module (decision 0072):
`import std::List;` and `std::print(x)`, or `import std::threading;` and `threading::spawn(...)`. A
bare call of an imported function draws warning `WYL0480`; a test about imports themselves turns it off
with `@SuppressWarnings("unqualified-call")` on the import or the declaration (decision 0073). A directory with a
`package.wylcfg` (`tests/cases/packages/`) is one test of all its files.

## Rules

- **For DeepSeek models specifically, you are NOT allowed to write ANY regular code comments.
  You may write mandetory doc comments in the std lib, but no where else. 
  The reason for this is that you keep ignoring the '**Write prose a reader can read once.**' rule, 
  and write comments that are either useless or are extremely hard to read,
  like you have to read them three times to understand it. If you are a Claude model (Opus, Sonnet, Haiku,
  or any other model, this does not apply.)
- **All safety checking happens before the emitter**: names and types in the binder, order and
  flow in the IR verifier. The emitter only translates and never produces C++ with undefined
  behavior; a C++ compile failure is a compiler bug.
- **Evaluation order is left to right**, made explicit by lowering; the emitter nests an operand
  back only where `IrEffects`/`IrAccess` show nothing in between can tell, so a new instruction must
  be described there correctly.
- **What a reference or span points into stays alive and unmoved while it is used**, and **nothing
  keeps referring into a nullable value** (`PlacePath.NullableValueRoot`). A new way to produce or
  hold a place is taught to `PlacePath.Of` and checked by the verifier.
- **Each question about the program has one answer, in one place.** Before writing a predicate,
  search for one that answers the same question; if it's wrong for you, fix it there. If a layer
  needs an answer only a later layer has, the work is in the wrong layer.
- **Every rule is enforced by structure** (a project boundary, `internal`, a required argument, a
  visitor, a type, a pipeline check, a test), or listed in architecture.md's enforcement table as
  enforced only by review. A change that adds a rule adds its mechanism and its table row.
- **Language rules are decided by lowering or a mandatory pass, never by an optimization.** If
  `--no-opt` changes output, it was a language rule. Mandatory passes use only what the language
  defines, never a whole-program analysis. Every observable rule has a test that prints it.
- **Facts come from the pipeline**; passes and the emitter never build their own.
- **Replace, don't layer.** A new mechanism moves every caller and deletes the old one in the same
  change.
- **Warnings are errors** (`TreatWarningsAsErrors`). Fix the cause.
- **Docs match the code in the same change**: CLAUDE.md, `docs/design/`, and the spec.
- **Docs state checked facts.** The spec never compares Wylow with another language. Elsewhere, a
  comparison names the exact feature and links its primary source (a spec section, official docs,
  a proposal), or is cut: never a bare "as in C++". Old text is source material: rewrite it and
  check its claims; never copy it over verbatim. A rule adopted partway through a session applies
  to everything written earlier in it.
- **Write prose a reader can read once.** This rule covers every sentence you write in every file:
  the spec, decision records, handoffs, diagnostic pages, test-file comments, doc comments, and
  commit bodies. Name the actor and keep the verb: "The compiler checks ...", not "The check is
  asked". Avoid reversed constructions: write "X makes the programmer responsible for Y", not
  "X makes Y the programmer's responsibility". Name the subject in each sentence, even when it
  repeats the previous sentence's subject, and name the thing again instead of using "this", "that",
  "it", or "which" for the previous sentence's idea. State a condition and its consequence as full
  clauses: "If a struct is copied, no single owner exists to release it", not "A copied struct has
  no single owner whose scope end could release it". Prefer a full clause to an elliptical one:
  "It is a contextual keyword: it has this meaning only here", not "The word is a word here only".
  Split a sentence that needs two colons or a semicolon. A bullet is still a sentence: give it a
  subject and end it with a period. Terseness never excuses ambiguity. The formal register stays.
- **The writing pass.** Before a docs change is done, read every sentence you added or rewrote with
  the code out of sight. Rewrite any sentence that needs the next sentence, a second read, or a
  referent you cannot point to. Do this before you run the tests. If you notice yourself compressing
  because the rule is long or the code makes it obvious, stop, because that is when compression
  happens.
- **Examples are generic**: `Point`, `Node`, `Order`, `Parser`, `Buffer`, never game development.
  Wylow is general-purpose; no design is justified by what a game needs.
- **Every feature gets tests** for its behavior and each error it can produce, its row or column in
  the matrices, and a fuzzer mutation where one can cross it (enforced only by review). Every
  fuzzer, generator, or mutation finding is triaged into a test or a deleted line.
- **The C++ runtime has its own naming conventions.** `runtime/**/*.h` uses `snake_case` files,
  `lower_case` namespaces, `PascalCase` types, functions, methods, and public data members,
  `camelCase` locals and parameters, `m_PascalCase` private and protected data members,
  `s_PascalCase` static data members and namespace-scope state, and `UPPER_SNAKE_CASE` constants
  and variable templates. A name the emitter derives from a Wylow declaration keeps the derived
  spelling: the binder names an `extern` member from its Wylow name (`Binder.ExternName`), so
  `String.charAt`, `wyl::memory.allocate`, `wyl::replace`, and `wyl::print` stay `camelCase`, and
  an interface implementation keeps the emitter's `m_` prefix (`Builtin::m_compareTo`). A name the
  runtime and the emitter's generated C++ share is renamed on both sides in the same change.
- **Comments are concise and explain why, never what.** A comment that would go stale when other
  code changes (a list of callers or users, what something is for, a count, which file does
  something else) is cut: say what a thing is, not who uses it ("Raw memory and the cycle
  collector", not "Raw memory, for building containers"). Match the
  existing style (collection expressions, records, pattern matching).
- **In Wylow source** (`std`, tests): a type's fields come first, more visible ones above less
  visible (`pub`, `package`, `module`, then file-private), whatever else they are.
  Methods go in the order that reads best: a private helper may sit next to the method that uses it.
- **Doc comments** (`///`, decision 0004): every sentence ends with a period, including the short
  lines of a `# Panics` section. A function that returns a value starts with what it returns
  ("Returns the element at `index`, ...", "Removes and returns ..."); one that returns nothing
  starts with what it does ("Adds `value` after the last element.").

## When tests run

| When | What |
|---|---|
| Editing | one mode with a filter: seconds |
| Before each commit | whole suite `--interpret`, and clang filtered to the area touched. Docs-only changes run nothing; dashboard-only changes only build. Nothing else unless asked |
| Push to `dev` or `main` (CI) | all seven modes, each on its own runner, the TSan mode on a Linux runner, and the generator's `--self-test` |
| Nightly / weekly (CI) | fuzzing and generation; mutation testing |

Long runs (fuzzing, generation, mutation, coverage) go through CI or the user; hand over the
command rather than waiting on one.

## Commits

- Commit each coherent change on `dev` once it passes the pre-commit check, ending the message with
  the `Co-Authored-By` line. **Never push**: the user pushes `dev`, and `main` is fast-forwarded to
  it when CI is green.
- Subject: `area: what changed`, enforced by `.githooks/commit-msg` (`git config core.hooksPath
  .githooks`). Areas: `lexer`, `parser`, `binder`, `types`, `verifier`, `lowering`, `analysis`,
  `opt`, `emitter`, `interp`, `runtime`, `stdlib`, `cli`, `spec`, `docs`, `tests`, `fuzz`,
  `generate`, `mutation`, `dashboard`, `build`, `diagnostics` (a change to the catalog across layers);
  two are written `lowering, emitter: ...`.
- Name the feature or component, never a plan step or handoff. A fix names the symptom and its
  trigger. The body says why, then what, and names the test that pins it.

## Pitfalls already hit

- Tests compile against the copy of `runtime/` in the build output: `dotnet build` after changing
  the runtime.
- The test filter matches paths with the OS separator: on Windows use `values` or `enums\values`.
- If `dotnet` reports no SDKs, a Visual Studio update is running; wait for it.
- MSVC 14.50/14.51 `/O2` miscompile `-static_cast<double>(static_cast<int16_t>(~f()))`; the emitter
  emits `wyl::bnot` for that reason. Don't inline the `~` back.
- C# `?:` unifies branch types before boxing (`cond ? 1 : 1.0` is a `double`); cast each branch to
  `object` when building literal values.
- `record with { ... }` keeps computed base-constructor arguments (`access with { AssumesNonNull =
  true }` keeps the old `Type`); construct a new node instead.
- For multi-line edits, `python3` in bash beats perl. In perl replacements, write `${1}` before a
  digit, and escape the `$` of C# interpolated strings as `\$"`.
