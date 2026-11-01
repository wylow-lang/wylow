using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

/// <summary>
/// Visibility (spec 06-syntax.md §6.2): whether a use may name a declaration, which every lookup
/// asks <see cref="Reach.Includes"/>, and the rules that keep a visible declaration usable where it
/// is visible.
/// </summary>
public sealed partial class Binder
{
    void CheckVisible(Location use, Reach reach, string description)
    {
        if (!reach.Includes(use.Source))
            Error(use, $"{description} is visible {reach.Describe()}");
    }

    /// <summary>
    /// The overloads a call in <paramref name="use"/> may choose from: those visible there, as in
    /// Java. If none is, all of them, so that the one chosen is reported as not visible rather than
    /// as missing.
    /// </summary>
    static IReadOnlyList<Candidate> VisibleFrom(IReadOnlyList<Candidate> candidates, SourceText use)
    {
        var visible = candidates.Where(c => c.Symbol.Reach.Includes(use)).ToList();
        return visible.Count > 0 ? visible : candidates;
    }

    /// <summary>How an error names a visibility that must be written.</summary>
    static string Written(Visibility visibility) =>
        visibility == Visibility.File ? "no visibility" : $"'{SyntaxFacts.VisibilityText(visibility)}'";

    /// <summary>
    /// A visible signature names only types visible as far (§6.2): a caller must be able to name
    /// what it passes and gets. Only a type visible from the declaration is compared, since one that
    /// isn't is already an error where it is named, and within a package the levels are ordered.
    /// </summary>
    void CheckSignatures(TypeSymbol s)
    {
        var reach = s.Reach;
        if (s.BaseType is { } baseType)
            CheckNamedTypes(s.Declaration.BaseType!.Location, reach, baseType, $"{SyntaxFacts.KindName(s.Kind)} '{s.Name}'", "base class");
        for (int i = 0; i < s.Interfaces.Count && i < s.Declaration.Interfaces.Count; i++)
            CheckNamedTypes(s.Declaration.Interfaces[i].Location, reach, s.Interfaces[i], $"{SyntaxFacts.KindName(s.Kind)} '{s.Name}'", "interface");
        CheckBoundTypes(s.Declaration.TypeParameters, s.TypeParameters, reach, $"{SyntaxFacts.KindName(s.Kind)} '{s.Name}'");

        foreach (var field in s.Fields)
            CheckNamedTypes(field.Declaration.Type.Location, field.Reach, field.Type, $"field '{s.Name}.{field.Name}'", "type");
        foreach (var variant in s.Variants)
        {
            foreach (var field in variant.Fields)
                CheckNamedTypes(field.Declaration.Type.Location, field.Reach, field.Type, $"variant '{s.Name}.{variant.Name}'", "field type");
        }
        foreach (var member in s.Members.Where(m => m.Declaration is not null))
        {
            var what = member.IsConstructor ? $"constructor '{member.Signature}'"
                : member.IsIndexer ? $"indexer of '{s.Name}'"
                : $"method '{s.Name}.{member.Name}'";
            CheckFunctionSignature(member, what);
        }
    }

    void CheckFunctionSignature(FunctionSymbol function, string what)
    {
        var decl = function.Declaration!;
        for (int i = 0; i < function.Parameters.Count && i < decl.Parameters.Count; i++)
            CheckNamedTypes(decl.Parameters[i].Type.Location, function.Reach, function.Parameters[i].Type, what, "parameter type");
        if (decl.ReturnType is { } returnType)
            CheckNamedTypes(returnType.Location, function.Reach, function.ReturnType, what, "return type");
        CheckBoundTypes(decl.TypeParameters, function.TypeParameters, function.Reach, what);
    }

    void CheckBoundTypes(IReadOnlyList<TypeParameterSyntax> syntax, IReadOnlyList<TypeParameterSymbol> symbols, Reach reach, string what)
    {
        for (int i = 0; i < syntax.Count && i < symbols.Count; i++)
        {
            foreach (var bound in symbols[i].Bounds)
                CheckNamedTypes(syntax[i].Location, reach, bound, what, "type parameter's bound");
        }
    }

    void CheckNamedTypes(Location location, Reach reach, WylType type, string what, string role)
    {
        if (NarrowestNamed(type) is { } named && named.Reach.Level < reach.Level && named.Reach.Includes(location.Source))
        {
            var naming = named == type.Symbol && type.TypeArguments.Count == 0 ? $"'{type}'" : $"'{type}', which names '{named.Name}',";
            Error(location, $"{what} is visible {reach.Describe()}, but its {role} {naming} is visible {named.Reach.Describe()}");
        }
    }

    /// <summary>The least visible declared type that <paramref name="type"/> names, if any.</summary>
    static TypeSymbol? NarrowestNamed(WylType type)
    {
        var narrowest = type.Symbol;
        foreach (var part in type.Parts)
        {
            if (NarrowestNamed(part) is { } inner && (narrowest is null || inner.Reach.Level < narrowest.Reach.Level))
                narrowest = inner;
        }
        return narrowest;
    }
}
