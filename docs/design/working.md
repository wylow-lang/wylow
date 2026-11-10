# How we work

The working agreements for Wylow: how designs are made, how sessions run, and how work is handed
over. They apply to every session, on any machine. `CLAUDE.md` has the rules about code and docs;
this file has the rest.

## Who decides

Wylow is one person's language, and that person is new to building compilers. So a design
question gets a comparison of how established languages and compilers handle it, with sources,
and **one recommendation**; the user decides. Big refactors are welcome when they make later work
easier: the language is young and churn is expected.

## Design principles

- **One way to do things.** Reuse an existing mechanism (an annotation, a method, a call with named
  arguments) before adding syntax, and flag any two ways of doing the same thing. Where Wylow
  borrows a feature, it behaves the way it does in the language it came from.
- **Java's conventions win toss-ups** in surface syntax, since they are familiar and easy for tools
  and models to write. Where Wylow needs explicitness (visibility with `pub` and modules, `::` for
  module paths but `.` for members), it departs deliberately; on semantics, ask rather than assume
  Java.
- **Check a borrowed feature's status** before adopting it: shipped, preview, or withdrawn, and why.
- **Observable behavior never depends on codegen.** Where it could, put a cheap deterministic rule
  in the spec. Cheap runtime checks are welcome where a compile-time rule would be heavy.
- **Semantics are explicit; optimizations are invisible.** "The compiler could avoid this if it
  proved X" is a later optimization pass, never a language rule that depends on inference.
- **The language provides only what libraries can't.** Thread pools, jobs, parallel loops, pools of
  objects, and containers are library code.
- **Unsafe code lives only in the standard library's floor** (`Array`, `List`, `Span`, native
  `String`); every other container is built safely on them.
- **Wylow is general-purpose.** No design is justified by what one kind of program (games, a
  particular port) needs, and examples are generic.
- **Finish before starting.** Cleanup and completion of existing features come before new ones.
  "Wait for real use" is a reason only for genuine judgment calls; a known gap with a known design
  is queued work.
- **Soundness holes are the point of a refactor.** When one surfaces a case the old code got
  wrong, fix it, pin it with a test, and say so; don't preserve a bug to keep output unchanged.
  When two implementations disagree, judge both against the spec: neither is an oracle, the
  interpreter included.
- **The standard library prelude is provisional**: revisit it as the library changes. The user
  leans towards none, leaving imports to an editor.
- **`std`'s root holds the foundation.** A declaration belongs in the root if most code builds on
  it, as on `String`, `Result`, `Comparable`, and `Printable`. A declaration that is one of a family,
  such as a collection, belongs in a module named for the family's subject. Before adding a
  declaration to `std`, check the roadmap for similar declarations that are planned, and ask the
  user whether the declaration starts or joins a module. The user decides where it goes.

## Design sessions

- Each point for the user's decision is written as **what the spec says now / what I propose /
  alternatives**, plainly, so a proposal never reads as a fact.
- Decisions go into the spec (rules) and a decision record (reasons) once the user agrees, and the
  roadmap (issue #6) is updated in the same session.
- Say what you're doing every few minutes; long silent thinking looks stuck.

## Ending a session

Tell the user what was done (decisions, commits, spec and roadmap changes, anything decided
without their explicit approval), what is open, and what's next with a recommendation. Then write
the next session's kickoff prompt to `docs/handoff/` and commit it, so it survives on any machine.
Write the handoff in the same plain English as the docs it belongs to. The writing pass is in
`CLAUDE.md`. A handoff is deleted when its work is done; code and docs never cite one.

## State and memory

- **The repository is the memory.** Anything that isn't specific to one machine belongs in a
  committed file: rules in `CLAUDE.md`, conventions here, decisions in the spec and decision
  records, plans in issue #6, task state in a handoff. Assistant memories hold only what is tied
  to one machine (paths, local tools).
- **Don't record task progress anywhere but a handoff or the roadmap**: progress notes elsewhere
  go stale and are never deleted.

## Long runs

- The pre-commit check is seconds (`CLAUDE.md`, "When tests run"). Before anything that takes
  minutes, say what it is and how long it takes, using measured times.
- Tier-2 fuzzing, generation, mutation targets, and coverage run on GitHub Actions, not locally:
  hand over the `gh workflow run` command (`fuzz.yml`, `generate.yml -f seconds=1800 -f seed=N`,
  `mutation.yml -f target=...`) and triage from `gh run download`.
- Test tooling is worth maintaining only while it finds compiler bugs. When a harness needs
  repeated fixes of its own, say so and propose cutting it back.

## Branches and the roadmap

- Commits go on `dev`; the user pushes, and fast-forwards `main` by hand when CI is green. Don't
  propose automating that.
- The roadmap is issue #6 in `wylow-lang/wylow`; edit it with `gh issue edit 6 --body-file`.

## Recipe: auditing the generated C++

To list structural optimization opportunities (not micro-optimizations the C++ compiler does):

- Read `tests/.out/<group>/<name>/<name>.cpp`; user code starts after the standard library's
  instantiations (`grep -n "^void fn_main()"`). The largest user programs are in `programs/`.
- Grep across files for `wyl::Pin(`, `wyl::MakeBorrow(`, `wyl::Keep(`, by-value `wyl::Ref<` parameters,
  and `Ref` locals copied from fields.
- Categories: repeated or unneeded pins; borrows used only to reach an element; reference counts
  that a move or a `const` reference would save; value copies of large structs; ordering lambdas
  where nothing conflicts; redundant checks.
- Check each suggestion against the traps: release timing is specified, so a move that releases
  earlier is observable; a `const T&` parameter aliases a place the callee may reassign; a hoisted
  pin extends a lifetime; borrow counters are library code. Every suggestion names the pass and the
  analysis it would use: optimizations are IR passes, never shortcuts in lowering or the emitter.
- Report a numbered list by category, each with a file, the fix, and the analysis it reuses, then
  a short ranking by payoff over effort, and say nothing was benchmarked.
