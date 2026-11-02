using System.Reflection;
using System.Runtime.CompilerServices;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Diagnostics;

/// <summary>Which operands of a failed operation are nullable, which decides the fix a message suggests.</summary>
public enum NullableOperand { None, Value, Reference }

/// <summary>Where 'this', fields, and instance methods are unavailable, which the message names.</summary>
public enum NoThisContext { Static, FieldInitializer, SuperArguments, ChainArguments, DefaultValue }

/// <summary>Why a place cannot be changed, which a message gives as a note.</summary>
public enum ReadOnlyKind { None, ReadOnlyThis, ImmutableField, LambdaCopy, InVariable, InParameter, ImmutableVariable, ImmutableParameter, ReturnsIn }

/// <summary>A <see cref="ReadOnlyKind"/> and the field, variable, parameter, or method it names.</summary>
public readonly record struct ReadOnlyReason(ReadOnlyKind Kind, string Name = "");

/// <summary>Why an escaping lambda cannot capture a local.</summary>
public enum CaptureProblem { Reference, View, NonEscaping, Mutable }

/// <summary>A parameter that is 'escaping' in only one of two signatures that must match, and whether in the first.</summary>
public readonly record struct EscapingDifference(string Parameter, bool EscapingHere);

/// <summary>Why a value that does not convert implicitly could be made to, which the message suggests.</summary>
public enum ConversionHelp { None, Cast, NullCheck, SpanInsideNullable, SpanOfTemporaryArray, SpanOfReadOnlyArray }

/// <summary>Declares a catalog method's code and the spec section, by anchor, that makes it an error.</summary>
[AttributeUsage(AttributeTargets.Method)]
sealed class ErrorAttribute(int number, string anchor) : Attribute
{
    public int Number { get; } = number;
    public string Anchor { get; } = anchor;
}

/// <summary>
/// Every diagnostic the compiler reports, one factory method each, split by spec chapter
/// (decision 0003, wording by <c>docs/diagnostics/README.md</c>). A method's
/// <see cref="ErrorAttribute"/> gives its code and spec section; its body gives the message and its
/// notes. The catalog test checks the codes, anchors, pages, and that a test produces each code.
/// </summary>
public static partial class Catalog
{
    static readonly Dictionary<string, DiagnosticDescriptor> descriptors = Describe();

    /// <summary>Every code in use.</summary>
    public static IReadOnlyCollection<DiagnosticDescriptor> All => descriptors.Values;

    /// <summary>Codes no longer emitted, which are never given to another diagnostic.</summary>
    public static IReadOnlyList<int> Retired { get; } = [];

    static Dictionary<string, DiagnosticDescriptor> Describe()
    {
        var found = new Dictionary<string, DiagnosticDescriptor>(StringComparer.Ordinal);
        foreach (var method in typeof(Catalog).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.GetCustomAttribute<ErrorAttribute>() is not { } error)
                continue;
            // One method per name, so that a call finds its descriptor by name alone.
            if (!found.TryAdd(method.Name, new DiagnosticDescriptor(error.Number, DiagnosticSeverity.Error, error.Anchor, method.Name)))
                throw new InvalidOperationException($"catalog method '{method.Name}' is overloaded");
        }
        return found;
    }

    /// <summary>How far a declaration reaches: <c>everywhere</c>, <c>only in its file (list.wyl)</c>, ...</summary>
    static string Reaching(Visibility level, SourceText? source) => level switch
    {
        Visibility.Public => "everywhere",
        Visibility.Package => source!.Module.Package.Name is { } package ? $"only in package '{package}'" : "only in this package",
        Visibility.Module => $"only in {ModuleName(source!.Module)}",
        _ => $"only in its file ({source!.Name})",
    };

    /// <summary>How a message names a module: <c>module 'app::model'</c>, <c>the root module of 'app'</c>.</summary>
    static string ModuleName(Text.Module module) => (module.IsRoot, module.Package.Name) switch
    {
        (true, null) => "the root module",
        (true, { } package) => $"the root module of '{package}'",
        (false, null) => $"module '{string.Join("::", module.Path)}'",
        (false, { } package) => $"module '{package}::{string.Join("::", module.Path)}'",
    };

    /// <summary>How a message names a visibility that must be written.</summary>
    static string Written(Visibility visibility) =>
        visibility == Visibility.File ? "no visibility" : $"'{SyntaxFacts.VisibilityText(visibility)}'";

    static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    static Diagnostic WithNullableHelp(this Diagnostic diagnostic, NullableOperand nullable) => nullable switch
    {
        NullableOperand.Value => diagnostic.Help("check it for null first, or use '!' or '??'"),
        NullableOperand.Reference => diagnostic.Help("check it for null first, or use '!'"),
        _ => diagnostic,
    };

    /// <summary>Nothing keeps referring into a nullable value, which assigning its variable null destroys.</summary>
    static Diagnostic InsideNullable(this Diagnostic diagnostic, string variable) =>
        diagnostic.Note($"assigning 'null' to '{variable}' would destroy its value").Help("copy the value to a variable first");

    static Diagnostic Because(this Diagnostic diagnostic, ReadOnlyReason reason) => reason.Kind switch
    {
        ReadOnlyKind.ReadOnlyThis => diagnostic.Note("'this' is read-only in a method that is not 'mut'"),
        ReadOnlyKind.ImmutableField => diagnostic.Note($"field '{reason.Name}' is not 'mut'"),
        ReadOnlyKind.LambdaCopy => diagnostic.Note($"an escaping lambda only has a copy of '{reason.Name}'"),
        ReadOnlyKind.InVariable => diagnostic.Note($"'{reason.Name}' is an 'in' variable"),
        ReadOnlyKind.InParameter => diagnostic.Note($"'{reason.Name}' is an 'in' parameter"),
        ReadOnlyKind.ImmutableVariable => diagnostic.Note($"variable '{reason.Name}' is not 'mut'"),
        ReadOnlyKind.ImmutableParameter => diagnostic.Note($"parameter '{reason.Name}' is not 'mut'"),
        ReadOnlyKind.ReturnsIn => diagnostic.Note($"'{reason.Name}' returns 'in'"),
        _ => diagnostic,
    };

    /// <summary>For a place that is an indexer's result, the type that assigns its elements with <c>operator []=</c> instead of storing them.</summary>
    static Diagnostic Elementless(this Diagnostic diagnostic, string? type) =>
        type is null ? diagnostic : diagnostic.Note($"'{type}' has no element storage and assigns its elements with 'operator []='");

    /// <summary>A change to a temporary value, which is gone after the statement.</summary>
    static Diagnostic Temporary(this Diagnostic diagnostic) =>
        diagnostic.Note("its changes would be lost").Help("store it in a 'mut' variable first");

    static Diagnostic Escaping(this Diagnostic diagnostic, EscapingDifference? difference, string other) => difference switch
    {
        { EscapingHere: true } d => diagnostic.Note($"parameter '{d.Parameter}' is 'escaping' here but not in {other}"),
        { } d => diagnostic.Note($"parameter '{d.Parameter}' is 'escaping' in {other} but not here"),
        null => diagnostic,
    };

    static Diagnostic Make(Location at, string message, [CallerMemberName] string name = "") =>
        new(descriptors.TryGetValue(name, out var descriptor) ? descriptor : throw new InvalidOperationException($"catalog method '{name}' has no [Error]"),
            at, message, []);
}
