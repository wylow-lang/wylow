# 0033. Source files are strict UTF-8, and columns count characters

- Status: accepted (2026-11-02)
- Spec: `lexical.source`, `errors.panics`

## Decision

- **A source file that is not UTF-8 is an error** (`WYL0436`), reported at its first ill-formed
  byte. A byte order mark at the start is allowed and is not part of the text. The manifest is read
  the same way.
- **A column counts characters (code points) from 1.** A character above U+FFFF counts once; a
  combining mark is a character of its own. Panics and diagnostics use the same column.

## Alternatives

- **Decoding leniently**, replacing each ill-formed sequence with U+FFFD (what the compiler did,
  through .NET's
  [`File.ReadAllText`](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.readalltext)).
  A file saved in another encoding then compiles, with U+FFFD in its string literals, which the
  program prints: a change of meaning nobody asked for.
- **Accepting other encodings by their byte order mark**, as `File.ReadAllText` also did for UTF-16
  and UTF-32. Two encodings of one program, for no gain; a String is UTF-8 anyway
  ([0011](0011-string-value-type.md)).
- **Columns in UTF-16 code units** (what the compiler did, from .NET's strings), as the
  [Language Server Protocol](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#textDocuments)
  counts by default. An emoji then moves every later column by two, and a panic's location, which
  is observable, would depend on the compiler's string representation.
- **Columns in UTF-8 bytes.** Deterministic, but a column then depends on how text is encoded
  rather than on what a reader sees.
- **Columns in grapheme clusters.** Closest to what a reader sees, but which characters form a
  cluster changes with Unicode versions.

## Why

The text of a program is its meaning, so reading it is not a place to guess: a file that is not
UTF-8 is a mistake the author fixes by saving it again. Code points are what chapter 2 calls
characters, so a column means the same in the spec, in diagnostics, and in panics, and no Unicode
version changes it.
