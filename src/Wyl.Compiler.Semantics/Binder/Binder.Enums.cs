using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    void DeclareVariants(TypeSymbol s)
    {
        foreach (var decl in s.Declaration.Variants)
        {
            var fields = new List<FieldSymbol>();
            foreach (var parameter in decl.Fields ?? [])
            {
                if (fields.Any(f => f.Name == parameter.Name))
                    Error(Catalog.DuplicateField(parameter.Location, parameter.Name, fields.First(f => f.Name == parameter.Name).Declaration.NameLocation));
                else
                    TakesLanguageName(parameter.Name, parameter.Location);
                // A variant's fields are as visible as the enum.
                var fieldDecl = new FieldDecl(parameter.Location, Modifiers.None, parameter.Type, parameter.Name, parameter.Location, null)
                {
                    Visibility = s.Declaration.Visibility,
                };
                foreach (var annotation in parameter.Annotations)
                    ReportMisplacedAnnotation(annotation);
                var fieldType = BindType(parameter.Type, allowVoid: false);
                CheckStorable(fieldType, parameter.Type.Location, "the type of a variant's field");
                fields.Add(new FieldSymbol(parameter.Name, fieldType, s, fieldDecl));
            }
            if (s.FindVariant(decl.Name) is not null)
            {
                Error(Catalog.DuplicateVariant(decl.NameLocation, decl.Name));
                continue;
            }
            TakesLanguageName(decl.Name, decl.NameLocation);
            s.Variants.Add(new VariantSymbol(decl.Name, s, s.Variants.Count, decl, fields));
        }

        foreach (var repr in s.Declaration.Annotations.Where(a => a.Name == "Repr"))
        {
            if (repr.Arguments is not [TypeSyntax underlyingSyntax])
            {
                Error(Catalog.ReprArguments(repr.Location));
                continue;
            }
            var underlying = BindType(underlyingSyntax, allowVoid: false);
            if (s.UnderlyingType is not null)
                Error(Catalog.ReprTwice(repr.Location));
            else if (s.HasData)
                Error(Catalog.ReprWithData(repr.Location));
            else if (underlying != WylType.Error && !underlying.IsInteger)
                Error(Catalog.ReprNonInteger(underlyingSyntax.Location, underlying.ToString()));
            else
                s.UnderlyingType = underlying;
        }

        Int128 next = 0;
        var used = new Dictionary<Int128, VariantSymbol>();
        foreach (var variant in s.Variants)
        {
            if (variant.Declaration.Value is { } valueSyntax)
            {
                if (s.HasData)
                {
                    Error(Catalog.VariantValueWithData(valueSyntax.Location));
                    continue;
                }
                s.UnderlyingType ??= WylType.Int;
                if (VariantValue(valueSyntax, s.UnderlyingType) is not { } value)
                    continue;
                next = value;
            }
            variant.Value = next;
            if (used.TryGetValue(next, out var other))
                Error(Catalog.DuplicateVariantValue(variant.Declaration.NameLocation, variant.Name, other.Name, other.Declaration.NameLocation));
            used.TryAdd(next, variant);
            next++;
        }
        if (s.UnderlyingType is { } type && s.Variants.FirstOrDefault(v => !type.HeldRange!.Value.Contains(v.Value)) is { } tooLarge)
            Error(Catalog.VariantValueTooLarge(tooLarge.Declaration.NameLocation, tooLarge.Name, type.ToString()));
        if (!s.HasData)
        {
            foreach (var method in s.Declaration.Methods.Where(m => m.Name == TryFrom))
                Error(Catalog.TryFromDeclared(method.NameLocation, s.Name));
        }
    }

    Int128? VariantValue(Expr syntax, WylType underlying)
    {
        var (literal, negated) = syntax switch
        {
            IntLiteralExpr l => (l, false),
            UnaryExpr { Operator: TokenKind.Minus, Operand: IntLiteralExpr l } => (l, true),
            _ => ((IntLiteralExpr?)null, false),
        };
        if (literal is null)
        {
            Error(Catalog.VariantValueNotLiteral(syntax.Location));
            return null;
        }
        Int128 value = negated ? -(Int128)literal.Value : literal.Value;
        if (!underlying.HeldRange!.Value.Contains(value))
        {
            Error(Catalog.VariantLiteralTooLarge(syntax.Location, underlying.ToString()));
            return null;
        }
        return value;
    }
}
