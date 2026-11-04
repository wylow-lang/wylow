using Wyl.Compiler.Text;

namespace Wyl.Compiler.Diagnostics;

/// <summary>Why a null check does not narrow a variable, which the message gives as a note.</summary>
public enum NarrowingLost { None, RefVariable, AssignedByLambda, CheckedOutsideLambda }

// Chapter 16: flow, which the IR verifier reports on the lowered program.
public static partial class Catalog
{
    [Error(394, "flow.definite-assignment")]
    public static Diagnostic MayBeUnassigned(Location at, string name) =>
        Make(at, $"'{name}' might not have been assigned");

    [Error(395, "flow.assign-once")]
    public static Diagnostic MayBeAssignedTwice(Location at, string name) =>
        Make(at, $"'{name}' might already have been assigned").Help("declare it 'mut' to assign it more than once");

    [Error(396, "flow.narrowing")]
    public static Diagnostic CaseNullNeverMatches(Location at, string name) =>
        Make(at, "'case null' can never match").Note($"'{name}' is not null here");

    /// <param name="what">For a 'ref' variable or parameter, how the note names it (<c>'ref' parameter</c>).</param>
    [Error(397, "flow.narrowing")]
    public static Diagnostic MayBeNull(Location at, string name, NarrowingLost lost, string what = "") =>
        lost switch
        {
            NarrowingLost.RefVariable => Make(at, $"'{name}' might be null")
                .Note($"a {what} is not narrowed by null checks, since another reference to its variable could change it")
                .Help("copy it to a local first, or use '!'"),
            NarrowingLost.AssignedByLambda => Make(at, $"'{name}' might be null")
                .Note("a lambda assigns it, so null checks do not narrow it")
                .Help("copy it to a local first, or use '!'"),
            NarrowingLost.CheckedOutsideLambda => Make(at, $"'{name}' might be null")
                .Note("null checks outside a lambda do not narrow inside it")
                .Help("check it inside the lambda, or copy it to a local first"),
            _ => Make(at, $"'{name}' might be null").Help($"check it with 'if ({name} != null)', or use '?.' or '!'"),
        };

    /// <param name="byLambda">Whether a lambda that may assign the variable runs there, instead of the variable being passed by 'ref'.</param>
    [Error(411, "memory.nullable-references")]
    public static Diagnostic NullableValueInUse(Location at, string name, bool byLambda) =>
        Make(at, $"'{name}' might be set to null while its value is in use")
            .Note(byLambda ? $"a lambda that may assign '{name}' can run here" : $"'{name}' is passed by 'ref' here")
            .InsideNullable(name);

    [Error(401, "flow.returns")]
    public static Diagnostic MissingReturn(Location at, string? function) =>
        Make(at, function is null ? "lambda must return a value on every path" : $"'{function}' must return a value on every path");

    [Error(398, "flow.constructors")]
    public static Diagnostic FieldUnassignedAtReturn(Location at, string field) =>
        Make(at, $"field '{field}' must be assigned before the constructor returns");

    [Error(399, "flow.constructors")]
    public static Diagnostic FieldMayBeUnassigned(Location at, string field) =>
        Make(at, $"field '{field}' might not have been assigned");

    [Error(400, "flow.constructors")]
    public static Diagnostic ThisBeforeFieldsAssigned(Location at, string unassigned) =>
        Make(at, "'this' cannot be used until every field is assigned").Note($"field '{unassigned}' might not have been assigned");

    [Error(440, "flow.moves")]
    public static Diagnostic UseAfterMove(Location at, string name, Location? movedAt) =>
        movedAt is { } move
            ? Make(at, $"'{name}' is used after being moved").Note(move, $"'{name}' was moved here")
            : Make(at, $"'{name}' is used after being moved");
}
