<a id="errors"></a>
# 18. Errors and panics

<a id="errors.panics"></a>
## 18.1 Panics

A program panics when one of the checks this specification describes fails, or when it calls
`panic(message)` ([`std::panic`](a-library.md#library.panic)). The program then ends, as
[Panics](01-introduction.md#introduction.panics) describes: no destructor or deferred statement
runs, and no other thread continues. A panic can't be caught.

The location of a panic is the location of the operation that failed, or, inside a function marked
`@CallerLocation`, the location of the call to that function
([`@CallerLocation`](22-annotations.md#annotations.caller-location)). It is written as the file's
path under the package's `src/` directory, a line, and a column: `geometry/shapes.wyl:3:5`.

<a id="errors.result"></a>
## 18.2 `Result`

<a id="errors.unused"></a>
## 18.3 Unused results

<a id="errors.handling"></a>
## 18.4 Handling results
