# 0004. Doc comments

- Status: accepted (2026-11-01)
- Spec: the naming conventions' chapter (informative), when rewritten

## Decision

Documentation of a declaration is a `///` comment directly above it, written in Markdown. The
standard library documents all of its behavior this way ([0001](0001-document-set.md)); other
Wylow code follows the same convention.

```java
/// A growable array on the heap.
///
/// Adding or removing elements may move the others, so while a reference, span, or loop into the
/// list exists, the list is borrowed.
pub final class List<T> implements Borrowable, Spannable<T> {
    /// Returns a copy of the first element, or null if the list is empty.
    pub T? first() { ... }

    /// Removes and returns the element at `index`, moving the later elements down by one.
    ///
    /// # Panics
    ///
    /// If `index` is out of bounds, or the list is borrowed.
    @CallerLocation
    pub T removeAt(int index) { ... }
}
```

- **The first sentence is a summary**, ending with a period, which an index or tooltip shows alone.
  For a function it starts with a verb in the third person ("Returns", "Removes", "Makes"); for a
  type, field, or variant it is a noun phrase ("A growable array on the heap.").
- **Code is in backticks.** Parameters are named in the text (`` `index` ``); there are no
  `@param` or `@return` tags.
- **Fixed sections**, as `#` headings in this order, each present only when it applies:
  - `# Panics`: every condition under which the function panics. Required on every function that
    can panic, since a panic is observable behavior.
  - `# Errors`: what each error a returned `Result` can hold means.
  - `# Safety`: for an `unsafe` function, what the caller must guarantee.
  - `# Examples`.
- **Every `pub` declaration of the standard library has a doc comment.** A test checks this; it
  can also check that every function that can panic has a `# Panics` section, from the effects the
  analysis already finds.
- Doc comments say what a declaration does, never how; comments inside bodies (`//`) say why, as
  for the compiler's own code.

## Alternatives

- **Javadoc's `/** ... */` with `@param`, `@return`, `@throws` tags.** The classic Java form;
  the tags repeat what the signature and summary already say, and HTML markup is noisier than
  Markdown. Java itself added `///` Markdown comments in JDK 23 (JEP 467).
- **C#'s XML doc comments** (`/// <summary>`). Precise for tools, tiresome to write and read.
- **Plain `//` comments** (the standard library until now). Nothing distinguishes documentation
  from a remark, so no tool or test can find it.

## Why

`///` Markdown is now the form in both Java and Rust, so it is the familiar choice either way.
Javadoc's summary-sentence style reads well in an index. Rust's fixed sections make the facts a
caller most needs, when it panics, how it fails, what `unsafe` requires, always in the same place,
and checkable.
