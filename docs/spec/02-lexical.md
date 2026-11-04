<a id="lexical"></a>
# 2. Lexical structure

A source file is a sequence of characters, which this chapter divides into tokens: identifiers,
keywords, literals, and operators and punctuation, separated by whitespace and comments. Its
productions are lexical ([Grammar](01-introduction.md#introduction.notation.grammar)).

<a id="lexical.source"></a>
## 2.1 Source text

A source file is a file whose name ends in `.wyl`. Its contents are text in UTF-8; a byte order
mark at its start is not part of the text. The text is a sequence of Unicode characters (code
points).

It is a compile-time error if a source file's bytes are not UTF-8: if any part of them is not a
well-formed UTF-8 sequence. A UTF-16 file that starts with a byte order mark is such a file.

A line ends after a line feed (U+000A). Lines are numbered from 1. A character's **column** is its
position in its line, counted in characters from 1: a character above U+FFFF counts once, and a
combining mark counts as a character of its own. A location in the program, such as a panic's
([Panics](18-errors.md#errors.panics)), is a line and a column.

These classes of characters are used by the productions of this chapter:

```ebnf
Letter       = "A" … "Z" | "a" … "z" .
DecimalDigit = "0" … "9" .
HexDigit     = "0" … "9" | "A" … "F" | "a" … "f" .
BinaryDigit  = "0" | "1" .
```

<a id="lexical.tokens"></a>
## 2.2 Tokens and whitespace

A **token** is an identifier, a keyword, a literal, or an operator or punctuation token. A
character with the Unicode `White_Space` property is **whitespace**. Whitespace and comments
separate tokens and have no other meaning.

The text is divided into tokens from its start. At each point, after any whitespace and comments,
the next token is the longest sequence of characters that forms a token.

It is a compile-time error if a character outside a comment, a character literal, or a string
literal starts no token, such as `#`, a `$` not followed by `"`, or a letter outside `Letter`.

**Example:**

```java statements
bool atLeast = x>=-1;   // the tokens 'x', '>=', '-', '1'
bool below = x<-1;      // the tokens 'x', '<', '-', '1'
int café = 1;           // error[WYL0002]
```

<a id="lexical.comments"></a>
## 2.3 Comments

A **line comment** starts with `//` and runs to the end of its line. A **block comment** starts
with `/*` and ends at the first `*/` after it. A block comment may span lines; it does not nest,
so a `/*` inside one has no meaning. Neither is recognized inside a character literal, a string
literal, or the hole of an interpolated string ([Interpolated strings](#lexical.literals.interpolated)).

It is a compile-time error if a block comment is not closed before the end of the file.

<a id="lexical.comments.doc"></a>
### 2.3.1 Doc comments

A line comment that starts with `///` is a **doc comment**. Doc comments on the lines directly
above a declaration document it. A doc comment is a comment: it does not change what a program
means.

> **Note:** A doc comment is written in Markdown. Its first sentence summarizes the declaration:
> for a function, starting with a verb in the third person ("Returns", "Removes"); for a type,
> field, or variant, as a noun phrase. Code is written in backticks. The sections `# Panics`,
> `# Errors`, `# Safety`, and `# Examples` follow, in that order, each where it applies: every
> condition under which a function panics, what each error of a returned `Result` means, what the
> caller of an `unsafe` function guarantees, and examples. The standard library documents every
> `pub` declaration this way.

**Example:**

```java
class Items<T> {
    /// Removes and returns the element at `index`, moving the later elements down by one.
    ///
    /// # Panics
    ///
    /// If `index` is out of bounds.
    pub T removeAt(int index) { ... }
}
```

<a id="lexical.identifiers"></a>
## 2.4 Identifiers

```ebnf
Identifier = ( Letter | "_" ) { Letter | DecimalDigit | "_" } .
```

A keyword or reserved word ([Keywords](#lexical.keywords)) is not an identifier, and neither is `_`
alone. Identifiers are case-sensitive: two identifiers are the same name if they are the same
sequence of characters.

<a id="lexical.keywords"></a>
## 2.5 Keywords

These are the **keywords**:

```
_          abstract   bool       break      byte       case       char       class
continue   default    double     else       enum       escaping   extends    extern
false      final      float      fn         for        if         implements import
in         int        interface  is         long       loop       mut        new
nint       nuint      null       operator   override   pub        ref        return
sbyte      short      static     struct     super      switch     this       true
try        uint       ulong      unsafe     ushort     var        virtual    void
when       while
```

These are **reserved words**:

```
const      defer      discard    package    private    protected  public     shared
unique
```

It is a compile-time error if a token is a reserved word, except `package` in `pub(package)`
([Visibility](03-programs.md#programs.visibility)).

The identifiers `as` and `module` have a meaning only in an import
([Imports](03-programs.md#programs.imports)) and in `pub(module)`
([Visibility](03-programs.md#programs.visibility)). Elsewhere they are ordinary identifiers.

<a id="lexical.literals"></a>
## 2.6 Literals

A **literal** is a token that denotes a value: an integer, floating-point, character, or string
literal, or an interpolated string. The keywords `true` and `false` denote the two values of
`bool`, and `null` the null value ([Nullable types](04-types.md#types.nullable)).

```ebnf
Literal = IntegerLiteral | FloatLiteral | CharLiteral | StringLiteral | InterpolatedString
        | "true" | "false" | "null" .
```

An integer or floating-point literal is a **number literal**. The letters, digits, and `_` that
directly follow a number literal's digits are its **suffix**.

- It is a compile-time error if a number literal's suffix is not one that
  [Integer literals](#lexical.literals.integer) or
  [Floating-point literals](#lexical.literals.float) gives for it.
- A `_` between digits separates them and has no other meaning. It is a compile-time error if a
  `_` in a number literal does not have a digit on each side, ignoring the other `_` next to it:
  `1__000` is valid, and `1_`, `0x_FF`, `1_.5`, and `1_e5` are not.

<a id="lexical.literals.integer"></a>
### 2.6.1 Integer literals

```ebnf
IntegerLiteral = ( DecimalNumeral | HexNumeral | BinaryNumeral ) [ IntegerSuffix ] .
DecimalNumeral = DecimalDigit { { "_" } DecimalDigit } .
HexNumeral     = "0" ( "x" | "X" ) HexDigit { { "_" } HexDigit } .
BinaryNumeral  = "0" ( "b" | "B" ) BinaryDigit { { "_" } BinaryDigit } .
IntegerSuffix  = "u" | "U" | "l" | "L" | ( "u" | "U" ) ( "l" | "L" ) .
```

An integer literal's value is its digits read in base 10, 16, or 2. A decimal numeral that starts
with `0` is still decimal. Its type is given by its suffix alone, never by its value or base, so
`0xFFFFFFFF` is an `int` literal:

| Suffix | Type |
|---|---|
| none | `int` |
| `u`, `U` | `uint` |
| `l`, `L` | `long` |
| `ul`, `uL`, `Ul`, `UL` | `ulong` |

- It is a compile-time error if a hexadecimal or binary literal has no digits after its prefix.
- It is a compile-time error if a literal's value is 2<sup>64</sup> or more. Whether the value fits
  in the literal's type is the rule of [Integer constants](05-conversions.md#conversions.constants).

**Example:**

```java statements
long big = 3_000_000_000L;
uint mask = 0xFFFF_FFFFu;
int flags = 0b1010;
int wrong = 0xFFFFFFFF;          // error[WYL0080]
```

<a id="lexical.literals.float"></a>
### 2.6.2 Floating-point literals

```ebnf
FloatLiteral = DecimalNumeral "." DecimalNumeral [ Exponent ] [ FloatSuffix ]
             | DecimalNumeral Exponent [ FloatSuffix ]
             | DecimalNumeral FloatSuffix .
Exponent     = ( "e" | "E" ) [ "+" | "-" ] DecimalNumeral .
FloatSuffix  = "f" | "F" | "d" | "D" .
```

A floating-point literal with the suffix `f` or `F` is a `float`; any other is a `double`. Its value
is the decimal number it writes, rounded to the nearest value of its type, with ties going to the
value whose last bit is 0.

It is a compile-time error if that number, rounded, is larger than the largest finite value of the
literal's type.

A `.` is part of a floating-point literal only with a digit on each side: `1.` and `.5` are not
literals, and in `1..5` the `.` belongs to the `..` token.

**Example:**

```java statements
double d = 1.5;
float f = 1.5f;
float wrong = 1.5;              // error[WYL0079]
float big = 1e39f;              // error[WYL0009]
double fine = 1e39;
```

<a id="lexical.literals.char"></a>
### 2.6.3 Character literals

A **character literal** is a `'`, then one character or one escape
([Escapes](#lexical.literals.escapes)), then a `'`. The character may be anything but `'`, `\`, a
line feed, or a carriage return. Its type is `char`, and its value is the character's code point.

- It is a compile-time error if a character literal holds no character (`''`).
- It is a compile-time error if a character literal holds more than one character or escape.
- It is a compile-time error if a character literal is not closed before the end of its line.

<a id="lexical.literals.string"></a>
### 2.6.4 String literals

A **string literal** is a `"`, then any number of characters and escapes, then a `"`. The characters
may be anything but `"`, `\`, a line feed, or a carriage return. Its type is `String`
([`String`](04-types.md#types.string)), and its value is its characters, with each escape replaced
by the character it stands for.

It is a compile-time error if a string literal is not closed before the end of its line.

<a id="lexical.literals.escapes"></a>
### 2.6.5 Escapes

An **escape** is a `\` and what follows it, in a character or string literal:

| Escape | Character |
|---|---|
| `\n` | Line feed, U+000A |
| `\t` | Tab, U+0009 |
| `\r` | Carriage return, U+000D |
| `\0` | U+0000 |
| `\\` | `\` |
| `\"` | `"` |
| `\'` | `'` |
| `\u{` *h* `}` | The code point whose value is *h*, written as 1 to 6 hexadecimal digits |

- It is a compile-time error if a `\` is followed by anything but the escapes above.
- It is a compile-time error if a `\u` is not followed by `{`, 1 to 6 hexadecimal digits, and `}`.
- It is a compile-time error if the value of a `\u{...}` escape is not a Unicode scalar value: if it
  is above `10FFFF` or from `D800` to `DFFF`.

**Example:**

```java statements
String s = "tab\there, smile \u{1F600}";
char quote = '\'';
char bad = '\u{D800}';          // error[WYL0016]
```

<a id="lexical.literals.interpolated"></a>
### 2.6.6 Interpolated strings

An **interpolated string** is a `$` followed directly by a string literal, whose text may contain
holes. In its text, `{{` stands for `{` and `}}` for `}`. Any other `{` opens a **hole**, which ends
at the `}` that matches it.

A hole holds tokens, which only spaces and tabs separate: a hole contains no comment and no line
break. Its tokens may include string literals and interpolated strings. Within a hole, `(` and `)`,
`[` and `]`, and `{` and `}` nest, and a `:` token outside all of them starts the hole's **format
specifier**: the characters from after the `:` to the hole's `}`, read as characters, not tokens.

- It is a compile-time error if the text has a `}` that is neither part of `}}` nor the end of a
  hole.
- It is a compile-time error if a format specifier reaches the string's closing `"` without its
  hole's `}`.
- It is a compile-time error if a `?` token outside all brackets of a hole comes before the `:`
  that starts its format specifier. A conditional expression in a hole is written in parentheses.

What a hole's tokens mean, and how its value is formatted, is given in
[String interpolation](13-expressions.md#expressions.interpolation).

**Example:**

```java statements
String a = $"{count} items, {{braces}}";
String b = $"{price:F2} each";
String c = $"{(ok ? "yes" : "no")}";
String d = $"{ok ? 1 : 2}";            // error[WYL0018]
```

<a id="lexical.operators"></a>
## 2.7 Operators and punctuation

These are the operator and punctuation tokens:

```
(    )    {    }    [    ]    ;    ,    .    ..   ..=  :    ::   @
?    ?.   ??   ->
+    -    *    /    %    &    |    ^    ~    !    &&   ||   <<   >>
<    >    <=   >=   ==   !=   ++   --
=    +=   -=   *=   /=   %=   &=   |=   ^=   <<=  >>=
```

Where a `>` closes a list of type arguments, a `>>` token is read as two `>` tokens
([Type arguments](11-generics.md#generics.arguments)).

<a id="lexical.naming"></a>
## 2.8 Naming conventions

> **Note:** Wylow code follows these conventions, and an implementation may warn about a name that
> doesn't. They are not rules of the language.
>
> | Kind | Convention | Example |
> |---|---|---|
> | Types: classes, structs, enums, interfaces | `PascalCase` | `OrderQueue` |
> | Enum variants | `PascalCase` | `Status.Pending` |
> | Methods, fields, locals, parameters | `camelCase` | `addItem`, `isEmpty` |
> | Constants: `static` fields | `UPPER_SNAKE_CASE` | `MAX_SIZE` |
> | Packages and modules, and the directories that are modules | `snake_case` | `text_format::parse` |
> | Files | `snake_case`, named for what they hold | `order_queue.wyl` |
>
> Declarations in an `extern` block, and the variants of an enum with `@Repr`, keep the spelling of
> the C or C++ names they bind ([Interop](21-interop.md#interop)).
