# Simplification inventory: duplicated concepts in src/Vpl.Compiler

The first step of the simplification pass. It makes no code changes. Every file under `Ir/`,
`Optimize/`, and `Analysis/` was read in full, as were `FlowAnalyzer.cs`, `PlacePath.cs`,
`Types.cs`, `Binder.Escaping.cs`, `Compilation.cs`, and the emitter's IR, nesting, lambda, type,
and cycle files. The rest of the binder was swept for predicates by shape, not read line by line.

Each finding is marked **confirmed** (reproduced with a probe program, quoted below) or **by
reading** (the code says so, but nothing was run). Line numbers are as of commit 97642cb.

Ranked by risk: soundness and crashes first, then wrong errors, then missed optimizations, then
clutter.

---

## 1. "May a lambda assign this local *here*?": flow-sensitive in the binder, whole-function in the IR

**Done in step 4:** FlowAnalyzer is gone, so the verifier's answer is the only one, and what lambdas
change is `LambdaWrites`, not flags on IR nodes. Before that, **fixed in 705e2fc:** the verifier asks the lambdas made on the path, using each lambda's
`IrFunction.ChangesAround` from the effects. It also checks that `IsChangedByLambda` covers them,
which found that lowering missed a struct constructor's `self` changed through `this`. The same-call
case is reported as the user's error. What follows is the finding as it was.

**Confirmed: the compiler crashes on a valid program.**

| Definition | Answer |
|---|---|
| `FlowAnalyzer.State.LambdaAssigned` — Semantics/FlowAnalyzer.cs:21, 154, 336 | flow-sensitive: the locals assigned by a non-escaping lambda *made so far*. Narrowing is refused only after that point (`Narrow`, :329). |
| `IrLocal.IsChangedByLambda` — Ir/Ir.cs:136, set from `capturedByLambda` in Ir/Lowering.cs:89-92 and :259 | whole-function: every writable local that any non-escaping lambda in the function captures, wherever the lambda is made. |
| `LambdaWrites.Apply` — Optimize/LambdaWrites.cs | clears the flag where no lambda body writes the local. Still whole-function. |

The verifier uses the IR answer: `CodeWrites` (Ir/IrVerifier.Values.cs:288) treats any code that
runs as able to replace the local. `ReportNullableValue` (:745) assumes the binder's answer agrees
("Narrowing is never kept for a variable a lambda assigns, so only passing it by 'ref' gets here").
It doesn't:

```
struct V { int x; }
void show(in int a) { print(a); }
void run(() -> void f) { f(); }
void main() {
    mut V? v = V(1);
    if (v != null) {
        show(v.x);            // binder: fine, no lambda exists yet
    }
    run(() -> { v = null; });
}
```

The verifier then throws: `the IR verifier rejected the lowered program: main: 'v!.x' is used
during the call of 'show', but the nullable value or variant it points into may be replaced`.
This happens with and without `--no-opt`.

Single home: the IR, flow-sensitively. The verifier already walks in order and sees each
`IrLambda` being made. "May a lambda change `l` here" can become verifier state: the locals
written by lambdas made so far on some path, joined at merges. Lowering's `capturedByLambda` is
then only the conservative input. `IsChangedByLambda` either goes away or becomes "some lambda in
the function may write it", documented as conservative, for HoldElision, BorrowedLocals,
LastUseMoves, StackSlots, IrAccess.WrittenByCode, and lowering's `ObjectLocal`/`IsStable`/`Receiver`.
FlowAnalyzer's answer stays (it reports the user's error), and the two must agree; a test like
the one above pins that down.

## 2. "Does copying or destroying this type count references?" (five answers)

**Done in step 2,** except the InPlaceElements and BorrowedLocals widening: `VplType.MayOwnObjects`
(was `MayHoldReferences`), `IsCounted` (every extern struct counts, so `Weak` does), `SharesStorage`
and `MayShareStorage` (was `IsHolder`), and `KeepsIdentity` (was `HoldsReference`). LastUseMoves
moves strings. Widening the other two needs their rule to see stores into a struct element's
fields, which `MayStoreOverElement` doesn't. What follows is the finding as it was.

**Confirmed: generated C++ fails to compile. Confirmed: two missed optimizations.**

| Definition | Answer |
|---|---|
| `VplType.MayHoldReferences` — Semantics/Types.cs:305 | inline types include a reference, type parameter, or escaping function. Leaves out `String` and `Weak<T>`: `InlineTypes` skips extern structs' fields (:295), and both are extern. Its doc comment ("may destroy an object") is what it really answers. |
| `CppEmitter.HasCountedParts` — CodeGen/CppEmitter.Types.cs:74 | the above plus `String`. **Still leaves out `Weak<T>`.** |
| `IrVerifier.IsHolder` — Ir/IrVerifier.Values.cs:660 | top level only: reference, type parameter, `String`, escaping function. This is a different question ("does the value hold what a reference into it points to"), also asked at PlacePath.cs:64, Lowering.Expressions.cs:194, and Binder.Statements.cs:432 (reference or `String`, without type parameters, which those reach by another path). |
| `EscapeFlow.HoldsReference` — Analysis/Escapes.cs:329 | reference, type parameter, or any function ("does a conversion keep identity"). Also used by StackSlots.cs:170. |
| `CyclicClasses.Hold` — Analysis/CyclicClasses.cs:114 | strong references held: reference, type parameter, any function. Agrees with `MayHoldReferences` in practice. |
| plain `IsReference` gates — Optimize/InPlaceElements.cs:52, Optimize/BorrowedLocals.cs:59 | class or interface only. |

What the disagreements cost:
- **Bug (C++ compile failure).** An enum's payload union gets written-out copy, move, and destroy
  only `if (HasCountedParts(s.Type))` (CppEmitter.Types.cs:165). A variant holding a `Weak<C>`
  (non-trivial C++ copy and destructor) gets none:
  ```
  enum E { None, Ref(Weak<C> w) }
  ...  E e = E.Ref(c); E f = e;
  ```
  MSVC: `'e_E::~e_E(void)': function was implicitly deleted because 'e_E' has a variant data member
  'e_E::v_Ref' with a non-trivial destructor`. The same gap leaves out `std::move` for a moved
  `Weak` in `OperandText` (CppEmitter.Ir.cs:416-420), which is only slower.
- **Missed optimization.** `LastUseMoves.Movable` (Optimize/LastUseMoves.cs:146) bails on
  `!type.MayHoldReferences`, so strings are never moved. The IR shows `take(copy s)` next to
  `takeObject(move c)`.
- **Missed optimization.** InPlaceElements.cs:52 and BorrowedLocals.cs:59 accept only class
  types, so `for (String name : names)` gives `let name: String = copy (*array)[...]` where the
  `C` loop gets `borrowed`.
- `MayHoldReferences` is right where the question is "may a release run code": IrEffects.cs:80
  and :103, Lowering.Expressions.cs:255 (`Kept`), CppEmitter.Ir.cs:442 (`vpl::keep`),
  CppEmitter.IrNesting.cs:302 (`CreatesOwner`), CppEmitter.Cycles.cs:37 and :77 (collector visits).

Single home, on `VplType`:
- `ReleaseMayRunCode`: today's `MayHoldReferences`, renamed to say what it answers.
- `IsCounted`: copying or destroying touches a count. That means references, type parameters,
  escaping functions, `String`, and `Weak`, through inline types; extern structs say whether they
  count through `StdlibSymbols`, not by name in the emitter.
- `HoldsSharedStorage` (today's `IsHolder`) and `KeepsIdentity` (today's `HoldsReference`), moved
  out of the verifier and Escapes, and used by PlacePath, lowering's `Receiver`, and the binder's
  hint too.

Callers that move: `HasCountedParts` and all its callers go to `IsCounted`, and so does the
LastUseMoves gate. Decide per optimization whether InPlaceElements and BorrowedLocals widen to
`IsCounted`: both emit `const T&`, which works for any `T`; the verifier's element rule
(`MayStoreOverElement`) is type-generic already.

## 3. "Can this place change while an operation uses it?" (a syntactic answer next to two real ones)

**Confirmed: release order depends on which shortcut matched.**

- `IrFootprint.MayChange` — Ir/IrAccess.cs:99. Conservative: any code that runs writes every
  `mut` field. Used by lowering's `Settle` (Lowering.Places.cs:215, 238).
- `Writes.CodeMayWrite` — Analysis/Writes.cs:175, through `Aliasing.MayChange` (Optimize/Aliasing.cs:27).
  Precise. Used by HoldElision and BorrowedLocals.
- `IrVerifier.CodeWrites` — Ir/IrVerifier.Values.cs:286. A second copy of the precise rule, on the
  verifier's own storage model (finding 5).
- `IrLowering.IsStable` — Ir/Lowering.Expressions.cs:269. Syntactic: `this`, a named local, or
  one non-`mut` field of one, with no lambda changing it. Used by `Kept` (:260), Lowering.Lambdas.cs:77,
  and Lowering.Constructors.cs:129.

`IsStable` ignores the instruction's own `ref` arguments. `Kept` passes a by-value argument with a
copy the statement keeps, so that the value is released when the statement completes (spec
03-memory-model.md, When references are released). It skips that copy for a stable place, and a
named local counts as stable even when the same call takes it by `ref`:

```
struct S { C c; }
int f(S a, ref S b) { b = S(new C("second")); print("f returns"); return 1; }
mut S x = S(new C("first"));
int n = f(x, ref x) + g();          // f returns / destroyed first / g runs
mut S[1] ys = [S(new C("third"))];
int m = f(ys[0], ref ys[0]) + g();  // f returns / g runs / destroyed third
```

The same source pattern releases the old value mid-statement for a local and at the statement's
end for an element. Both happen with and without `--no-opt`, so it's deterministic, but a
destructor's timing can no longer be read from the source as the spec promises. `Lowering.Chain`
(Places.cs:227) and `Aliasing.Chain` (Aliasing.cs:34) are also the same function written twice.

Single home: `IrFootprint.MayChange` stays the conservative answer, documented as "lowering's;
passes use `Writes.CodeMayWrite`". `IsStable` becomes that check over the effects of the
instruction being built (its `ref`/`addr` arguments and whether it runs code), or at least counts
the call's own `ref` arguments. One `Chain`, used by both lowering and Aliasing. The verifier's
`CodeWrites` calls `Writes.CodeMayWrite` once finding 5 lands.

## 4. "Is this field's default value zeroed storage?" vs "Does making the default run code?"

**Done in step 2:** `DefaultRunsCode` is on `VplType` next to `HasDefaultCode`, and `CheckBuilt`
uses `HasDefaultCode` (IrSelfTests `built_struct_nested_default_left_zeroed`). What follows is the
finding as it was.

**By reading: the verifier misses a check, and a doc comment states the wrong rule.**

- `IrLowering.HasDefaultCode` — Ir/Lowering.Constructors.cs:94. "More than zeroed storage": any
  field initializer, a generic struct, or a type parameter. It decides which fields `Built` stores,
  and the emitter's `make_default` (CppEmitter.Types.cs:64, :68).
- `IrEffects.DefaultRunsCode` — Ir/IrEffects.cs:89. "May run code": an initializer that isn't a
  literal or null.

Both questions are real, but `IrVerifier.CheckBuilt` (IrVerifier.cs:151) checks the zeroed-field
rule with the run-code one. Take `struct Vec { int x = 5; }` and `struct S { Vec v; }`: `S`'s
built constructors must store `v`, but the verifier would accept them leaving it zeroed, since
`Vec`'s default runs no code. The `IrZeroed` doc comment (Ir/Ir.Values.cs:291) says the same
wrong thing ("a field's zero is its default value only if its type's default runs no code").
Lowering is right today, so this is a verifier gap, not a miscompile. Home: both predicates on
`VplType` (or one in `IrEffects`, next to each other), with `CheckBuilt` and the comment switched
to `HasDefaultCode`.

## 5. "Where does a place's storage lie, and is its deref base an object?" (three storage models)

**Done in step 4:** `IrAccess` has paths and is the one model; the verifier resolves references
through its dependencies (`IrAccess.Target`'s `referents`) and asks `Writes.CodeMayWrite` with the
lambdas made so far. `IrLocal.Deref` answers what `*x` reaches, for `IrAccess`, `WriteSet.Add`,
`IrPlace.BaseType`, the verifier, and the emitter. Merging found two verifier gaps, neither
reachable from the source: unknown storage was taken never to be a frame local, and a write was
checked against only the first nullable a place lies in. `IrWrite.Within` was read by nothing and
is gone. What follows is the finding as it was.

**By reading: no reachable miscompile found, but the rules have drifted.**

- `IrAccess`/`IrStorage` (`Target`, `Overlaps`, `WrittenByCode`) — Ir/IrAccess.cs:20-72.
- The verifier's private `StorageKind`/`Resolved` (`Resolve`, `SameStorage`, `PathsOverlap`,
  `CodeWrites`) — Ir/IrVerifier.Values.cs:117-293.
- `WriteSet.Add`'s object test — Analysis/Writes.cs:64.
- `CppEmitter.IsObjectLocal` — CodeGen/CppEmitter.Ir.cs:362. The verifier's `derefable` — IrVerifier.Values.cs:419.

"Is the deref base an object?" has four answers:
- IrAccess.cs:49, Writes.cs:64, CppEmitter.Ir.cs:362: `Kind != Reference && Type.NonNull.IsReference`.
  That includes Slot locals.
- IrVerifier.Values.cs:234: `Kind == Hold || (Value or Borrowed && IsReference)`. That excludes
  Slot and includes every Hold, also of a String, a type parameter, or a closure.
- IrVerifier.Values.cs:419: Reference or Hold, or Value/Borrowed that is a reference or a pointer.

The verifier is precise (paths, plus `Writes.Of(...).MayWriteField`); IrAccess is conservative (a
frame local as a whole, `WrittenByCode`). That split is legitimate, since lowering can't read
analyses, but neither side says it is the precise or the conservative one. The disagreements
(Slot deref; a Hold of a type parameter dereferenced) aren't reached by today's lowering:
HoldElision's `IsDereferenced` keeps those holds, and slots are never dereferenced.

Single home: one storage model in `IrAccess` with optional path precision, and one
`IrPlace.IsObjectBase`. The verifier keeps only its dependency bookkeeping and asks the shared
model. Writes.cs:64 and CppEmitter.Ir.cs:362 move too.

## 6. "Can an object of this type be a Borrowable?" (identical copies)

`IrLowering.MayBeBorrowable` (Lowering.Places.cs:182) and `IrVerifier.MayBeBorrowable`
(IrVerifier.Values.cs:222) are byte-for-byte the same. Each builds its own `borrowableClasses`
(Lowering.cs:53, IrVerifier.cs:59). `Escapes.BorrowsLetEscape` (Escapes.cs:74) asks the
exact-type version with `IsSubtype`. No drift yet, but this one is a safety rule. Home:
`StdlibSymbols`/`BoundProgram`, computed once.

## 7. A loop element is recorded three times, and local roles are loose booleans

- **Step 3 deleted `IrProgram.LoopElements`:** `IrLocal.LoopElement` asks the symbol, which holds
  where it is declared. `IsElement` and the roles are still open. What follows is the finding as it was.
- `LocalSymbol.IsLoopElement` (Symbols.cs:16, set at Binder.ForEach.cs:210), copied into lowering's
  `loopElements` (Lowering.cs:288), then `IrProgram.LoopElements` (Ir.cs:19), then
  `IrLocal.IsElement` for the subset borrowed in place (Ir.cs:126, set at InPlaceElements.cs:56).
  `local.Symbol?.IsLoopElement` and its declaration location already answer the side table.
  BorrowedLocals.cs:58 and LastUseMoves.cs:147 exclude every loop element. For BorrowedLocals that
  is documented ("Loop elements are InPlaceElements's"). For LastUseMoves it isn't, and an element
  InPlaceElements rejected is a plain local that could still be moved (possibly a missed optimization).
- `IsTemporary`, `IsParameter`, `IsThis`, and `IsCapture` sit beside `Kind` as independent
  booleans with implicit valid combinations: a capture and `this` are also `IsParameter`
  (Lowering.Lambdas.cs:28, :33; Lowering.cs:44). Each pass re-lists its exclusions differently:
  BorrowedLocals.cs:57, LastUseMoves.cs:146, StackSlots.cs:186, IrVerifier.cs:229, IrVerifier.Values.cs:456.

Home: an `IrLocalRole` (Named, Temporary, Parameter, This, Capture) next to `Kind`. `IsElement`
becomes the role's "borrowed in place" and `LoopElements` is deleted.

## 8. "Is this a non-escaping function?" (two names, two meanings)

**Done in step 2:** `VplType.IsNonEscapingFunction` (non-null only) and `IsNullableNonEscapingFunction`.

- `Binder.IsNonEscapingFunction` (Binder.Escaping.cs:22) includes nullable function types.
  `CppEmitter.IsNonEscaping` (CppEmitter.Lambdas.cs:25) excludes them.
  `IsNullableNonEscaping` is defined in both (Binder.Escaping.cs:31, CppEmitter.Lambdas.cs:133).
- The pattern is also written inline: HoldElision.cs:167, LastUseMoves.cs:147,
  IrVerifier.Values.cs:664 and :726, Lowering.Expressions.cs:79 and :226, Lowering.Expressions.cs:157.

Probe: the binder never lets a nullable non-escaping value exist except as a parameter passed on
(`'??' cannot be applied to a nullable non-escaping function`; `'a' is a non-escaping parameter,
so it cannot be stored`), so the emitter's narrower meaning isn't reachable through a temporary.
Clutter, not a bug. Home: `VplType.IsNonEscapingFunction` (non-null only) and
`VplType.IsNullableNonEscapingFunction`.

## 9. Pre-IR mechanisms still alive

- `CyclicClasses` (Analysis/CyclicClasses.cs) and its `WholeProgram` (Analysis/WholeProgram.cs)
  still walk the bound tree: `BoundTree.Captures` and the bound lambdas. Every other analysis walks
  the IR's lambdas through `Callees`. Because the result is needed before lowering,
  `IrLowering` takes `Analyses` and reads `Cycles.IsCyclic(lambda)` to set `IrClosure.IsCyclic`
  (Lowering.Lambdas.cs:59). That contradicts CLAUDE.md's "lowering can't read analyses" and the
  rule that lowering decides nothing optional. Moving CyclicClasses onto the IR (closures'
  captures are `IrClosure.Captures`) lets the flag come from the analysis at emission time, and
  `WholeProgram` goes. `Overrides` then hangs off `Callees` alone.
- `Erase` (CyclicClasses.cs:52) and `FunctionValues.ErasedReference` (FunctionValues.cs:42) both
  erase reference nullability for "same C++ type", one recursively and one at the top only. Pick
  one when CyclicClasses moves.
- `Writes` is computed once from the lowered IR (Compilation.cs:80), and the verifier reuses it
  after every pass. That's sound only while each pass removes writes and runs and never adds any
  (HoldElision drops borrows; LastUseMoves' moves are of own locals, which summaries exclude;
  StackSlots' `end`s release what the scope end released before). Nothing states the invariant,
  so a pass that adds a write would be checked against stale summaries. Either recompute after
  each pass or say so on `Writes.Find`.

## 10. FlowAnalyzer vs the verifier's dataflow

**Done in step 4:** FlowAnalyzer is deleted and its checks are the verifier's, so there is one of
each of the facts below. What follows is the finding as it was.

The overlap is mostly a deliberate pair: FlowAnalyzer reports the user's errors on the bound tree,
and the verifier re-checks lowering's invariants, as CLAUDE.md asks. Matching facts:
- Definite assignment: `FlowAnalyzer.State.Assigned` vs `IrVerifier.State.Assigned`.
- Assigned-at-most-once: FlowAnalyzer's `MaybeAssigned` for non-`mut` locals vs the verifier's
  `MaybeAssigned` for temporaries.
- Pattern names assigned where they match: `CaseEntry`/`Patterns.Bindings` vs the verifier's `Outcomes`.
- "Must return on every path" (FlowAnalyzer.cs:111) vs "the end of the function is reachable"
  (IrVerifier.cs:116).
- Loops to a fixpoint in both.

These should stay two. The one real duplicate inside the pair is finding 1: narrowing, which the
binder decides and the IR verifier must agree with. Share that rule as one definition (or make
the verifier's check flow-sensitive the same way), and add a test that runs it both ways.

## 11. Clutter: the same walker, several times

- **The locals a statement declares** (five copies): `IrEffects.Ending` (IrEffects.cs:54),
  `IrBlock.DeclaresOwner` (Ir.cs:188), `Writes.OwnLocals` (Writes.cs:227), `StackSlots.Declared`
  (StackSlots.cs:282), and partly `IrWalk.OwnLocals` (IrAccess.cs:203). Home: `IrWalk.Declared`.
- **Does a statement give the local another value?** (three variants): `InPlaceElements.Changes`
  (InPlaceElements.cs:79; counts moves, through IrEffects.cs:69), `StackSlots.Replacements`
  (StackSlots.cs:196; walks lambdas and loop updates; moves don't count), and `LastUseMoves.Note`'s
  spoiling (LastUseMoves.cs:97). Home: `IrWalk.Rebinds(statement, local, countMoves)`.
- **What a call is given** (four copies): `IrEffects.Calling` (IrEffects.cs:156), `IrVerifier.CheckInUse`
  (IrVerifier.Values.cs:703), `LastUseMoves.Consumes` (LastUseMoves.cs:174), and
  `CppEmitter.FindRecursive` (CppEmitter.Lambdas.cs:163). Home: `IrWalk.Passed(rvalue)`.
- **Which local kind an rvalue makes** (**done in step 3:** `IrLet.Defines`): `IrLowering.Let` (Lowering.cs:136-151) vs `IrVerifier.CheckLet`
  (IrVerifier.cs:307-326). Drift is caught by the verifier, but they're still two copies. Home: one
  static on `IrLocalKind` or `IrRvalue`.
- **Which operations panic**: lowering decides by `IrBinary.IsChecked` and the conversion rule
  (Lowering.Expressions.cs:139, :162). The emitter re-derives it from types (`BinaryText`,
  `ConversionText`) and dereferences `Loc!` lazily. Drift would crash the compiler, not miscompile.
  The emitter could branch on `Loc is not null`.
- **Is this object a Borrowable whose borrow lets it escape** (Escapes.cs:74) vs finding 6: keep it,
  but next to `MayBeBorrowable`.

---

## Order

The order of work is the migration in [docs/design/architecture.md](../design/architecture.md),
which maps these findings onto its steps. This file only records what was found. Delete it once
every finding is done.
