using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Diagnostics;

// Chapters 16 to 19: flow, memory, errors, and concurrency.
public static partial class Catalog
{
    [Error(412, "memory.nullable-references")]
    public static Diagnostic MayReferToNothing(Location at, string text, string test) =>
        Make(at, $"'{text}' might refer to nothing").Help($"check it with '{test}', or use '!'");

    [Error(419, "memory.borrows.borrowable")]
    public static Diagnostic BorrowHookCall(Location at, string name) =>
        Make(at, $"'{name}' cannot be called directly").Note("the compiler calls it around every borrow");

    [Error(405, "memory.references")]
    public static Diagnostic ReferenceTypeMismatch(Location at, string what, string declared, string referenced) =>
        Make(at, $"{what} of type '{declared}' cannot refer to a '{referenced}'")
            .Note("a reference has exactly the type of what it refers to");

    [Error(406, "memory.references")]
    public static Diagnostic ReferenceIntoNullable(Location at, string what, string variable) =>
        Make(at, $"{what} cannot refer into nullable '{variable}'").InsideNullable(variable);

    [Error(407, "memory.references")]
    public static Diagnostic ReferenceToTemporary(Location at, string what, string? elementless) =>
        Make(at, $"{what} must refer to a variable, field, or element").Note("it cannot refer to a temporary value").Elementless(elementless);

    [Error(408, "memory.references")]
    public static Diagnostic RefToReadOnly(Location at, string keyword, string text, bool throughReference, ReadOnlyReason reason, string suffix) =>
        Make(at, throughReference ? $"cannot bind '{keyword}' to what '{text}' refers to, which is read-only" : $"cannot bind '{keyword}' to read-only '{text}'")
            .Because(reason).Help($"bind with 'in{suffix}'");

    [Error(409, "memory.references")]
    public static Diagnostic ReferenceToPassedWithoutCopying(Location at, string keyword, string type) =>
        Make(at, $"'{keyword}' cannot be used with type '{type}'").Note("functions are already passed without copying");

    [Error(410, "memory.references")]
    public static Diagnostic ReferenceToNullableVariable(Location at, string what, string name) =>
        Make(at, $"{what} cannot refer to nullable variable '{name}'").Help("pass it by 'ref' to a function instead");

    [Error(414, "memory.second-class")]
    public static Diagnostic SecondClassUninitialized(Location at, string type) =>
        Make(at, $"variable of type '{type}' must be initialized where it is declared").Note("it cannot be reassigned");

    [Error(415, "memory.second-class")]
    public static Diagnostic ViewOfTemporary(Location at, string type) =>
        Make(at, $"'{type}' cannot view a temporary value").Note("the value is gone after this statement")
            .Help("store the value in a variable first");

    [Error(416, "memory.second-class")]
    public static Diagnostic ViewInsideNullable(Location at, string type, string variable) =>
        Make(at, $"'{type}' cannot view inside nullable '{variable}'").InsideNullable(variable);

    [Error(417, "memory.second-class")]
    public static Diagnostic ReassignView(Location at, string text, string type) =>
        Make(at, $"cannot reassign '{text}'").Note($"a '{type}' views what it was initialized with for its whole scope")
            .Help("declare a new variable instead");

    [Error(421, "errors.unused")]
    public static Diagnostic UnusedResult(Location at, string name) =>
        Make(at, $"'Result' returned by '{name}' is not used").Help("handle it, or discard it with '_ = ...'");

    [Error(420, "memory.borrows.borrowable")]
    public static Diagnostic BorrowableNonClass(Location at) =>
        Make(at, "only classes can be 'Borrowable'").Note("a value is borrowed through whatever holds it");

    [Error(413, "memory.nullable-references")]
    public static Diagnostic NullableReferenceNotStorable(Location at, string type, string what, string keyword) =>
        Make(at, $"'{type}' cannot be {what}").Note($"only a '{keyword}?' variable or parameter can hold it");

    [Error(418, "memory.second-class")]
    public static Diagnostic ViewNotStorable(Location at, string type, string what) =>
        Make(at, $"'{type}' cannot be {what}").Note("like a 'ref', it can only be a local variable or parameter");

    [Error(402, "memory.weak")]
    public static Diagnostic WeakOfNullable(Location at, string target) =>
        Make(at, "'Weak' cannot take a nullable type").Note("a weak reference can always be null").Help($"write 'Weak<{target}>'");

    [Error(403, "memory.weak")]
    public static Diagnostic WeakOfValue(Location at, string target) =>
        Make(at, $"'Weak' type argument: expected a class or interface type, found '{target}'");

    /// <param name="what">The part of a place that would be moved: <c>a field out of 'x'</c>, <c>out of 'this'</c>, and so on.</param>
    [Error(441, "memory.moves")]
    public static Diagnostic MoveOutOfPlace(Location at, string what, string type) =>
        Make(at, $"cannot move {what}").Note($"'{type}' is not copyable");

    [Error(443, "memory.moves")]
    public static Diagnostic MoveWhileUsed(Location at, string name, string keyword) =>
        Make(at, $"cannot move '{name}' while it is passed by '{keyword}'").Note("the call uses the value where it is");
}
