using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    List<TypeParameterSymbol> DeclareTypeParameters(IReadOnlyList<TypeParameterSyntax> syntax, IReadOnlyList<TypeParameterSymbol> outer)
    {
        var result = new List<TypeParameterSymbol>();
        foreach (var parameter in syntax)
        {
            if (result.Any(p => p.Name == parameter.Name))
                Error(Catalog.DuplicateTypeParameter(parameter.Location, parameter.Name));
            else if (outer.Any(p => p.Name == parameter.Name))
                Error(Catalog.HidingTypeParameter(parameter.Location, parameter.Name));
            else
                TakesLanguageName(parameter.Name, parameter.Location);
            result.Add(new TypeParameterSymbol(parameter.Name));
        }
        return result;
    }

    /// <summary>Binds bounds with <see cref="typeParameters"/> already in scope, so a bound may mention them.</summary>
    void BindBounds(IReadOnlyList<TypeParameterSyntax> syntax, IReadOnlyList<TypeParameterSymbol> symbols)
    {
        for (int i = 0; i < syntax.Count; i++)
        {
            var parameter = symbols[i];
            foreach (var boundSyntax in syntax[i].Bounds)
                BindBound(boundSyntax, () => parameter.IsCopyableBound = true, () => parameter.IsSendableBound = true, parameter.Bounds.Add);
        }
    }

    /// <summary>
    /// Binds one bound of a type parameter: the property <c>copyable</c> or <c>sendable</c>, or an
    /// interface. A bound that is neither is reported.
    /// </summary>
    void BindBound(TypeSyntax syntax, Action copyable, Action sendable, Action<WylType> implements)
    {
        if (syntax is { Shape: TypeShape.Named, Modules.Count: 0, TypeArguments.Count: 0, IsNullable: false })
        {
            switch (syntax.Name)
            {
                case "copyable":
                    copyable();
                    return;
                case "sendable":
                    sendable();
                    return;
            }
        }
        var bound = BindType(syntax, allowVoid: false);
        if (bound == WylType.Error)
            return;
        if (!bound.IsInterface || bound.IsNullable)
            Error(Catalog.NonInterfaceBound(syntax.Location, bound.ToString()));
        else
            implements(bound);
    }

    /// <summary>
    /// Binds a member's <c>where</c> clauses, which bound its type's type parameters for this
    /// member alone (spec <c>generics.bounds</c>).
    /// </summary>
    void BindWhere(FunctionDecl decl, FunctionSymbol function, TypeSymbol owner)
    {
        if (decl.Where.Count == 0)
            return;
        if (owner.Kind == TypeKind.Interface)
        {
            Error(Catalog.WhereNotAllowed(decl.Where[0].Location, "be used on a method of an interface"));
            return;
        }
        if (function.IsVirtual || function.IsOverride || function.IsAbstract)
        {
            var modifier = function.IsVirtual ? "virtual" : function.IsOverride ? "override" : "abstract";
            Error(Catalog.WhereNotAllowed(decl.Where[0].Location, $"be used on an '{modifier}' method", "a call through the base class or interface could not check it"));
            return;
        }
        foreach (var clause in decl.Where)
        {
            var parameter = owner.TypeParameters.FirstOrDefault(p => p.Name == clause.Parameter);
            if (parameter is null)
            {
                bool own = function.TypeParameters.Any(p => p.Name == clause.Parameter);
                Error(Catalog.WhereParameterUnknown(clause.ParameterLocation, clause.Parameter, owner.Name, own));
                continue;
            }
            foreach (var boundSyntax in clause.Bounds)
            {
                BindBound(boundSyntax, () => function.CopyableParameters.Add(parameter), () => function.SendableParameters.Add(parameter),
                    bound => function.MemberInterfaceBounds.Add(new MemberInterfaceBound(parameter, bound)));
            }
        }
    }
}
