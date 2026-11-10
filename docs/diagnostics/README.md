# Diagnostics

How the compiler's errors and warnings are written, and where their codes are listed. Why it is
this way: [decision 0003](../design/decisions/0003-diagnostics.md).

## Form

```
src/parser.wyl:12:9: error[WYL0382]: 'buffer' is used after being moved
src/parser.wyl:10:15: note: 'buffer' was moved here
```

- A diagnostic is a severity (`error` or `warning`), a code, a location, and a **primary message**,
  followed by any number of attached lines: `note:` explains, `help:` suggests a fix. Each line has
  its own location, so an editor can jump to it; an attached line without a better location uses
  the primary one.
- **Codes** are `WYL` and four digits, numbered in order. A code is never reused. Each has a page,
  `WYLnnnn.md` in this directory: what it means, a short erroneous example, how to fix it, and the
  spec section that makes it an error, or that a warning is about.
- **Warnings** never make a program invalid, and the spec does not list them. Each warning has a
  name, such as `unqualified-call`, which `@SuppressWarnings` takes to turn it off. The catalog gives
  the name in the warning's `[Warning]` attribute, and the warning's page states it.

## Codes and pages

The catalog (`src/Wyl.Compiler.Syntax/Diagnostics/Catalog*.cs`, by spec chapter) is the list of
codes: each factory method gives its code, its spec section, and its message. A code's page is
`WYLnnnn.md` here: what it means, a short example, how to fix it, and the spec section. The page's
first `wyl` block is a whole program that produces that code and no other error or warning; the
catalog test compiles it. A code only a package can produce, such as a manifest's, has a package as its example
instead: each of its blocks names its file after the language, as in ```` ```wylcfg package.wylcfg ````
and ```` ```wyl src/main.wyl ````, and the test writes them out and compiles the package. Codes whose
page is not written are in `without-pages.txt`, which only shrinks. A code stays there when no
example can produce it alone: `WYL0022` (the interpolated string leaves a value another rule
rejects), `WYL0029` and `WYL0030` (a `dependencies` entry is itself `WYL0031`), `WYL0425` (an
extern method outside an extern struct is also `WYL0426`), `WYL0428`, `WYL0429`, and `WYL0430` (an
extern struct in a program is also `WYL0427`), and `WYL0436` (it is about a file's bytes, and an
example is text).

Write the page as prose for a reader who is learning the rule, not as notes for the implementer.
Give every sentence a subject and a full verb, and name the thing again rather than pointing at the
previous sentence. The writing pass, and the rule behind it, are in `CLAUDE.md`.

## The primary message

- **One statement of what is wrong**, about the code, in the present tense: `'x' is used after
  being moved`, `cannot return a value from 'void' function 'f'`. Not why the rule exists, not how
  to fix it: those are notes and help. The fixed templates below keep their own forms.
- **Lowercase first letter, no final period.** One sentence, no semicolons.
- **The rule as it stands, never its status**: no "not implemented yet", "not supported", "for
  now". A restriction that is planned to be lifted is stated as a restriction; the plan is in the
  roadmap (issue #6).
- **Formal**: no contractions (`cannot`, `does not`, `is not`), no "you", no "illegal" or
  "invalid", no "Wylow" (the reader knows which language they're writing). Not "a name", "a
  thing": name the kind of entity (`function`, `field`, `parameter`, `type`, `module`) when it
  isn't obvious from the code quoted.
- **Quote code in single quotes**: names, keywords, operators, types, and expressions as written
  (`'?.'`, `'m.peek(0)'`, `'List<Node>'`). Quote nothing else.
- **Name types by their name alone**, as code that imported them would: `Node`, `List<Node>`.
  Only when another type of the program has the same name is each named by its package and module
  path (`app::model::Node`, `std::List`); a lone file's package has no name, so it gives the module
  path alone. File-private types of one name in one module add their file:
  `app::model::Node (tree.wyl)`. This applies to runtime diagnostics and panic messages too.
- **Name functions** by name, or by signature where overloads make the name ambiguous:
  `'insert'`, `'insert(int, Node)'`.

## Fixed templates

One wording per family, so similar errors read alike:

| Family | Template |
|---|---|
| Type mismatch | `expected 'int', found 'String'`, prefixed by what was checked where useful: `argument 1 of 'insert': expected 'int', found 'String'` |
| Missing token | `expected ';' after import`, `expected a type, found '='` |
| Unknown name | `cannot find 'x' in this scope`, `cannot find type 'Node'` |
| Duplicate | `'Node' is already defined in this module`, with `note: previous definition here` |
| Forbidden operation | `cannot <verb> ...`: `cannot call 'mut' method 'push' on an immutable value` |
| Possibly unassigned | `'x' might not have been assigned` |

## Notes and help

- `note:` adds a fact the reader needs to understand the error: where something was declared,
  moved, or borrowed, what each ambiguous candidate is, which rule applies. Point at the code it
  is about.
- `help:` suggests a concrete change: `help: use '.'`, `help: write 'pub static'`. Show code in
  quotes rather than describing it.
- A message explains no more than a reader needs to fix the code; the code's page explains the
  rule. At most one `help:`; notes as many as there are relevant places.

## Worked examples

| Before | After |
|---|---|
| `a 'ref' parameter cannot have a default value, since it must refer to a variable` | `'ref' parameter 'x' cannot have a default value`<br>`note: a 'ref' argument must be a variable` |
| `'?.' is only needed on nullable values, and 'Point' is not nullable; use '.'` | `'?.' is used on a value of non-nullable type 'Point'`<br>`help: use '.'` |
| `'x' is used after it is moved` | `'x' is used after being moved`<br>`note: 'x' was moved here` (at the move) |
| `call is ambiguous between ...` | `call to 'f' is ambiguous`<br>`note: candidate 'f(int)'` (at each declaration) |
| `returns a value from a function returning nothing` | `cannot return a value from 'void' function 'f'` |
| `argument 1 doesn't fit parameter 'int index'` | `argument 1 of 'insert': expected 'int', found 'String'`<br>`note: parameter 'index' declared here` |
| `assignment is a statement in Wylow and cannot be used inside an expression` | `assignment cannot be used as an expression` |
| `the visibility comes first among modifiers, as in 'pub static'` | `visibility must come before other modifiers`<br>`help: write 'pub static'` |
| `cannot take a 'ref' to what 'm.peek(0)' refers to, because 'peek(int)' returns 'in'; use 'in' instead` | `cannot bind 'ref' to read-only result of 'm.peek(0)'`<br>`note: 'peek(int)' returns 'in'`<br>`help: bind with 'in'` |
| `recursive constructor invocation: this 'this(...)' chain calls the constructor it starts from` | `constructor 'Order(int)' calls itself through 'this(...)'`<br>`note: 'Order(int)' calls 'Order(int, int)' here` (at each step) |
| `top-level variables and constants are not supported yet; declare 'x' inside a function or type` | `variables cannot be declared at the top level`<br>`help: declare 'x' inside a function or type` |
| `'std' is the standard library's name` | `package name 'std' is reserved for the standard library` |
| `extern functions are only supported in the standard library for now` | `extern functions can only be declared in the standard library` |
