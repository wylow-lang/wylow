using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    /// <summary>
    /// Where a pattern matches, which a reference binding in it needs: the place the matched value is
    /// in, or a field of it, whose mutability is the place's, or a variant's field, which never changes.
    /// </summary>
    readonly record struct MatchPlace(BoundExpression? Root, PlaceKind Kind, ReadOnlyReason Reason, bool VariantField, string Text)
    {
        public static MatchPlace Of(Binder binder, BoundExpression value)
        {
            var (kind, reason) = binder.ClassifyPlace(value);
            return new(value, kind, reason, VariantField: false, TextOf(value.Location));
        }

        /// <summary>A field of a struct pattern's value, which is as changeable as the value itself.</summary>
        public MatchPlace AsField() => this with { Root = null };

        /// <summary>A variant's field, which is not changed in place.</summary>
        public MatchPlace AsVariantField() => this with { Root = null, VariantField = true };
    }

    BoundExpression BindIs(IsExpr e)
    {
        var operand = BindExpression(e.Operand);
        if (operand.Type == WylType.Error || operand.Type == WylType.Void)
        {
            DeclareErrorBindings(e.Pattern);
            return operand.Type == WylType.Void
                ? ErrorExpression(Catalog.IsOnVoid(e.Operand.Location))
                : new BoundErrorExpression(e.Location);
        }

        if (e.Pattern is ReferencePatternSyntax reference)
            return BindReferencePattern(e, operand, reference);
        if (IsNullPattern(e.Pattern))
            return ErrorExpression(Catalog.NullPatternInIs(e.Pattern.Location));
        // Other patterns match the value a reference refers to, once it is known to refer to one.
        operand = NarrowReference(operand);
        if (operand.Type.IsNullableReference)
        {
            DeclareErrorBindings(e.Pattern);
            return ErrorExpression(MayReferToNothing(e.Operand.Location, operand));
        }
        if (CheckMatchable(operand).Type == WylType.Error)
        {
            DeclareErrorBindings(e.Pattern);
            return new BoundErrorExpression(e.Location);
        }
        bool isTypeTest = e.Pattern is TypePatternSyntax || (e.Pattern is VariantPatternSyntax { Enum: null, Fields: null } && !operand.Type.IsEnum);
        if (isTypeTest && !operand.Type.IsReference && !operand.Type.IsNullableValue)
        {
            DeclareErrorBindings(e.Pattern);
            return ErrorExpression(Catalog.TypeTestOnValue(e.Operand.Location, operand.Type.ToString()));
        }
        int errors = diagnostics.ErrorCount;
        var pattern = BindPattern(e.Pattern, operand.Type, canBind: true, MatchPlace.Of(this, operand));
        // A type test that always passes is allowed: it states the type, as an upcast would.
        if (diagnostics.ErrorCount == errors && pattern is not BoundTypePattern && Patterns.AlwaysMatches(pattern, currentFunction))
            Error(Catalog.PatternAlwaysMatches(e.Pattern.Location));
        return new BoundIs(e.Location, operand, pattern);
    }

    /// <summary>
    /// <c>x is ref T name</c>, which tests that a <c>ref?</c> or <c>in?</c> refers to a place, and
    /// names it as a <c>ref</c> or <c>in</c> local would be, for the statement.
    /// </summary>
    BoundExpression BindReferencePattern(IsExpr e, BoundExpression operand, ReferencePatternSyntax syntax)
    {
        var keyword = FunctionSymbol.RefPrefix(syntax.RefKind).Trim();
        var target = syntax.Type is null ? null : BindType(syntax.Type, allowVoid: false);
        var referenced = (operand.Type as NullableReferenceType)?.Referenced;
        var text = TextOf(operand.Location);
        if (referenced is null)
            Error(Catalog.ReferencePatternOnValue(operand.Location, keyword, text, operand.Type.ToString()));
        else if (target is not null && target != WylType.Error && target != referenced)
            Error(Catalog.ReferencePatternType(syntax.Type!.Location, text, referenced.ToString(), target.ToString()));
        else if (CheckReferredPlace(operand, syntax.RefKind, $"'{keyword}' binding", mayBeNull: false, throughReference: true) && target != WylType.Error)
        {
            var binding = new LocalSymbol(syntax.Name, referenced.NullableReference(syntax.RefKind), isParameter: false, syntax.RefKind) { IsReferenceBinding = true };
            Declare(binding, syntax.NameLocation);
            return new BoundIs(e.Location, operand, new BoundReferencePattern(syntax.Location, operand.Type, binding));
        }
        DeclareErrorBindings(syntax);
        return new BoundErrorExpression(e.Location);
    }

    /// <summary>Binds a pattern matching values of <paramref name="type"/> and declares its names in the current scope.</summary>
    BoundPattern BindPattern(PatternSyntax syntax, WylType type, bool canBind, MatchPlace place)
    {
        if (type == WylType.Error)
        {
            DeclareErrorBindings(syntax);
            return new BoundDiscardPattern(syntax.Location, type);
        }
        switch (syntax)
        {
            case DiscardPatternSyntax:
                return new BoundDiscardPattern(syntax.Location, type);
            case VarPatternSyntax v:
                return new BoundBindingPattern(v.Location, type, DeclareBinding(v.Name, type, v.NameLocation, canBind));
            case ConstantPatternSyntax c:
                return BindConstantPattern(c, type);
            case TypePatternSyntax t:
                return BindTypePattern(t.Location, t.Type, t.Name, t.NameLocation, type, canBind);
            // Only a variant if the value is an enum: otherwise a lone name is a type test, as in `animal is Dog`.
            case VariantPatternSyntax { Enum: null, Fields: null } v when !type.IsEnum:
                return BindTypePattern(v.Location, new TypeSyntax(v.NameLocation, v.Name, []), null, v.NameLocation, type, canBind);
            case VariantPatternSyntax { Enum: null, Fields: { } fields } v when type.NonNull.IsStruct:
                return BindStructPattern(v.Location, new TypeSyntax(v.NameLocation, v.Name, []), fields, type, canBind, place);
            case StructPatternSyntax s:
                return BindStructPattern(s.Location, s.Type, s.Fields, type, canBind, place);
            case VariantPatternSyntax v:
                return BindVariantPattern(v, type, canBind, place);
            case ReferencePatternSyntax r:
                return BindReferenceBinding(r, type, canBind, place);
            default:
                throw new InvalidOperationException($"unexpected pattern {syntax.GetType().Name}");
        }
    }

    /// <summary>
    /// <c>in var name</c> or <c>ref var name</c> in a pattern: a reference to the part it matches, or
    /// to the whole matched value (spec <c>patterns.references</c>). A variant's field is bound with
    /// <c>in</c> only, and <c>ref</c> needs a place that can be changed.
    /// </summary>
    BoundPattern BindReferenceBinding(ReferencePatternSyntax r, WylType type, bool canBind, MatchPlace place)
    {
        var keyword = r.RefKind == RefKind.Ref ? "ref" : "in";
        if (place.VariantField && r.RefKind == RefKind.Ref)
        {
            Error(Catalog.VariantFieldReference(r.Location));
            DeclareErrorBindings(r);
            return new BoundDiscardPattern(r.Location, type);
        }
        if (r.Type is { } written)
        {
            var target = BindType(written, allowVoid: false);
            if (target != WylType.Error && target != type)
            {
                Error(Catalog.ReferencePatternType(written.Location, TextOf(r.Location), type.ToString(), target.ToString()));
                DeclareErrorBindings(r);
                return new BoundDiscardPattern(r.Location, type);
            }
        }
        if (r.RefKind == RefKind.Ref)
        {
            Diagnostic? problem = place.Kind switch
            {
                PlaceKind.Temporary => Catalog.ReferenceToTemporary(r.Location, "a 'ref' binding", null),
                PlaceKind.ReadOnly => Catalog.RefToReadOnly(r.Location, keyword, place.Text, throughReference: false, place.Reason, ""),
                _ => null,
            };
            if (problem is not null)
            {
                Error(problem);
                DeclareErrorBindings(r);
                return new BoundDiscardPattern(r.Location, type);
            }
        }
        return new BoundReferencePattern(r.Location, type, DeclareBinding(r.Name, type, r.NameLocation, canBind, r.RefKind));
    }

    LocalSymbol DeclareBinding(string name, WylType type, Location location, bool canBind, RefKind refKind = RefKind.None)
    {
        if (!canBind)
            Error(Catalog.BindingInSeveralPatterns(location));
        var local = refKind == RefKind.None
            ? new LocalSymbol(name, type, isParameter: false)
            : new LocalSymbol(name, type.NullableReference(refKind), isParameter: false, refKind) { IsPatternBinding = true };
        Declare(local, location);
        return local;
    }

    /// <summary>Declares a broken pattern's names, so that their uses don't report them as undefined.</summary>
    void DeclareErrorBindings(PatternSyntax syntax)
    {
        switch (syntax)
        {
            case VarPatternSyntax v:
                Declare(new LocalSymbol(v.Name, WylType.Error, isParameter: false), v.NameLocation);
                break;
            case TypePatternSyntax { Name: { } name } t:
                Declare(new LocalSymbol(name, WylType.Error, isParameter: false), t.NameLocation);
                break;
            case ReferencePatternSyntax r:
                Declare(new LocalSymbol(r.Name, WylType.Error, isParameter: false), r.NameLocation);
                break;
            case VariantPatternSyntax { Fields: { } fields }:
                foreach (var field in fields)
                    DeclareErrorBindings(field);
                break;
            case StructPatternSyntax s:
                foreach (var field in s.Fields)
                    DeclareErrorBindings(field);
                break;
        }
    }

    BoundPattern ErrorPattern(PatternSyntax syntax, WylType type, Diagnostic diagnostic)
    {
        Error(diagnostic);
        DeclareErrorBindings(syntax);
        return new BoundDiscardPattern(syntax.Location, type);
    }

    BoundPattern BindConstantPattern(ConstantPatternSyntax c, WylType type)
    {
        if (c.Value is NullLiteralExpr)
            return type.IsNullable ? new BoundNullPattern(c.Location, type) : ErrorPattern(c, type, Catalog.NullNeverMatches(c.Location, type.ToString()));
        var value = c.Value switch
        {
            IntLiteralExpr literal => BindIntegerLiteral(literal, c.Location, negated: false),
            UnaryExpr { Operand: IntLiteralExpr literal } => BindIntegerLiteral(literal, c.Location, negated: true),
            BoolLiteralExpr literal => new BoundLiteral(c.Location, WylType.Bool, literal.Value),
            CharLiteralExpr literal => new BoundLiteral(c.Location, WylType.Char, literal.Value),
            _ => ErrorExpression(Catalog.LiteralPatternKind(c.Location)),
        };
        if (value is not BoundLiteral literalValue)
            return new BoundDiscardPattern(c.Location, type);
        if (literalValue.Type == type)
            return new BoundConstantPattern(c.Location, literalValue);
        if (type.IsInteger && IsConvertibleConstant(literalValue, type))
            return new BoundConstantPattern(c.Location, BoundLiteral.Number(c.Location, type, literalValue.Integer!.Value));
        return ErrorPattern(c, type, Catalog.PatternNeverMatches(c.Location, TextOf(c.Location), type.ToString()));
    }

    /// <summary>
    /// For a reference, a test of its runtime type. For a nullable value, a test that it is not null.
    /// For another value type, which has no subtypes, the pattern only states the type, which must
    /// be exactly the value's.
    /// </summary>
    BoundPattern BindTypePattern(Location location, TypeSyntax typeSyntax, string? name, Location nameLocation, WylType type, bool canBind)
    {
        var target = BindType(typeSyntax, allowVoid: false);
        LocalSymbol? Binding(WylType bindingType) => name is null ? null : DeclareBinding(name, bindingType, nameLocation, canBind);
        if (target == WylType.Error)
        {
            Binding(WylType.Error);
            return new BoundDiscardPattern(location, type);
        }

        if (type.IsReference)
        {
            var from = type.NonNull;
            if (!target.IsReference || (from.IsClass && target.IsClass && !Conversions.IsSubtype(from, target, currentFunction) && !Conversions.IsSubtype(target, from, currentFunction)))
            {
                Binding(WylType.Error);
                Error(Catalog.PatternNeverMatches(typeSyntax.Location, target.ToString(), from.ToString()));
                return new BoundDiscardPattern(location, type);
            }
            return new BoundTypePattern(location, type, target, Binding(target));
        }

        // A nullable value is a value of its type exactly when it is not null.
        if (type.IsNullableValue && target == type.NonNull)
            return new BoundTypePattern(location, type, target, Binding(target));
        if (target != type)
        {
            Binding(WylType.Error);
            Error(Catalog.PatternNeverMatches(typeSyntax.Location, target.ToString(), type.ToString()));
            return new BoundDiscardPattern(location, type);
        }
        return Binding(target) is { } local ? new BoundBindingPattern(location, type, local) : new BoundDiscardPattern(location, type);
    }

    BoundPattern BindVariantPattern(VariantPatternSyntax v, WylType type, bool canBind, MatchPlace place)
    {
        var written = v.Enum is null ? v.Name : $"{TextOf(v.Enum.Location)}.{v.Name}";
        if (!type.IsEnum)
            return ErrorPattern(v, type, Catalog.PatternNeverMatches(v.Location, written, type.ToString()));

        var s = type.Symbol!;
        if (v.Enum is { } enumSyntax)
        {
            // `Result.Ok(...)` may leave out the type arguments, which the value determines.
            if (!Names(enumSyntax, type))
                return ErrorPattern(v, type, Catalog.PatternNeverMatches(v.Location, written, type.ToString()));
        }
        if (s.FindVariant(v.Name) is not { } variant)
            return ErrorPattern(v, type, Catalog.NoSuchVariantInPattern(v.Location, s.Name, v.Name));

        var map = TypeMap.For(type);
        if (v.Fields is null)
            return new BoundVariantPattern(v.Location, type, variant, [.. variant.Fields.Select(f => new BoundDiscardPattern(v.Location, map.Apply(f.Type)))]);
        if (variant.Fields.Count == 0)
            return ErrorPattern(v, type, Catalog.VariantPatternWithoutData(v.Location, variant.Name));
        if (v.Fields.Count != variant.Fields.Count)
        {
            int count = variant.Fields.Count;
            return ErrorPattern(v, type, Catalog.PatternFieldCount(v.Location, variant.Name, count, v.Fields.Count));
        }
        var fields = v.Fields.Zip(variant.Fields, (field, symbol) => BindPattern(field, map.Apply(symbol.Type), canBind, place.AsVariantField())).ToList();
        return new BoundVariantPattern(v.Location, type, variant, fields);
    }

    /// <summary>
    /// Whether a pattern's type names the matched value's type. Type arguments may be left out,
    /// since the value determines them, as in <c>Result.Ok(var v)</c>.
    /// </summary>
    bool Names(TypeSyntax syntax, WylType type) =>
        syntax is { TypeArguments.Count: 0, Modules.Count: 0 } && LookupTypeParameter(syntax.Name) is null
            ? LookupType(syntax.Name, syntax.Location) == type.Symbol
            : BindType(syntax, allowVoid: false) is var named && (named == type || named == WylType.Error);

    /// <summary>A struct's fields, each matching its pattern; on a nullable struct, also a test that it isn't null.</summary>
    BoundPattern BindStructPattern(Location location, TypeSyntax typeSyntax, IReadOnlyList<PatternSyntax> fieldSyntax, WylType type, bool canBind, MatchPlace place)
    {
        var value = type.NonNull;
        var syntax = new StructPatternSyntax(location, typeSyntax, fieldSyntax);
        if (!value.IsStruct || !Names(typeSyntax, value))
            return ErrorPattern(syntax, type, Catalog.PatternNeverMatches(syntax.Location, TextOf(typeSyntax.Location), type.ToString()));
        var s = value.Symbol!;
        if (fieldSyntax.Count != s.Fields.Count)
        {
            int count = s.Fields.Count;
            return ErrorPattern(syntax, type, Catalog.PatternFieldCount(syntax.Location, s.Name, count, fieldSyntax.Count));
        }
        var map = TypeMap.For(value);
        var fields = fieldSyntax.Zip(s.Fields, (field, symbol) =>
        {
            CheckVisible(field.Location, symbol.Reach, $"field '{s.Name}.{symbol.Name}'");
            return BindPattern(field, map.Apply(symbol.Type), canBind, place.AsField());
        }).ToList();
        return new BoundStructPattern(location, type, fields);
    }

    static bool IsNullPattern(PatternSyntax pattern) => pattern is ConstantPatternSyntax { Value: NullLiteralExpr };

    /// <summary>
    /// The enum named by the target of <c>Enum.Variant</c>. A generic enum written without type
    /// arguments takes them from <paramref name="expected"/> if that is the same enum, and otherwise
    /// has a null type, which is inferred from the variant's fields.
    /// </summary>
    (TypeSymbol Enum, WylType? Type)? AsEnumName(Expr target, WylType? expected)
    {
        if (AsTypeSymbol(target) is not var (s, name) || s.Kind != TypeKind.Enum)
            return null;
        if (name.TypeArguments.Count == 0 && s.IsGeneric)
        {
            CheckVisible(name.Location, s.Reach, $"enum '{s.Name}'");
            return (s, expected?.Symbol == s ? expected : null);
        }
        if (target is NameExpr)
            return (s, BindTypeName(name.Name, name.TypeArguments, name.Location));
        var arguments = name.TypeArguments.Select(a => BindType(a, allowVoid: false)).ToList();
        return (s, arguments.Contains(WylType.Error) ? WylType.Error : Construct(s, arguments, name.Location));
    }

    /// <summary>A variant of the expected enum written without its enum, as in <c>return Ok(value);</c>.</summary>
    static VariantSymbol? ExpectedVariant(string name, WylType? expected) =>
        expected is { IsEnum: true } ? expected.Symbol!.FindVariant(name) : null;

    /// <summary>An undefined name or function, with how to create the variant it names, if it is one's name.</summary>
    Diagnostic UndefinedName(Location at, string name, string? kind = null)
    {
        IEnumerable<TypeSymbol> types = [.. FileNames(at.Source).Types.Values, .. ModuleNames(at.Source.Module).Types.Values, .. stdlib.LanguageTypes];
        var owner = types.FirstOrDefault(t => t.Kind == TypeKind.Enum && t.FindVariant(name) is not null);
        return Catalog.UndefinedName(at, kind, name, owner?.Name, owner?.IsGeneric == true);
    }

    /// <summary>
    /// Creates a variant. <paramref name="type"/> is null for a generic enum whose type arguments
    /// must be inferred; <paramref name="pending"/> is null when no parentheses were written.
    /// </summary>
    BoundExpression BindVariant(Location location, TypeSymbol s, WylType? type, VariantSymbol variant, PendingArguments? pending)
    {
        if (type == WylType.Error)
            return new BoundErrorExpression(location);
        var arguments = pending?.Bind([type is null ? new Candidate(variant.InferringConstructor, null) : new Candidate(variant.Constructor, type)]);
        if (arguments is not null && AnyError(arguments))
            return new BoundErrorExpression(location);
        var name = $"{s.Name}.{variant.Name}";
        if (arguments is null && variant.Fields.Count > 0)
            return ErrorExpression(Catalog.VariantNeedsData(location, name));
        if (arguments is not null && variant.Fields.Count == 0)
            return ErrorExpression(Catalog.VariantWithoutData(location, name));
        arguments ??= [];
        if (arguments.Count != variant.Fields.Count)
        {
            int count = variant.Fields.Count;
            return ErrorExpression(Catalog.VariantValueCount(location, name, count, arguments.Count));
        }

        MethodReference? constructor;
        if (type is not null)
        {
            constructor = Resolve(location, $"variant '{name}'", [new Candidate(variant.Constructor, type)], null, arguments);
        }
        else
        {
            var inferable = variant.Fields.SelectMany(f => f.Type.ContainedTypeParameters).ToHashSet();
            if (s.TypeParameters.FirstOrDefault(p => !inferable.Contains(p)) is { } missing)
            {
                return ErrorExpression(Catalog.VariantTypeArgument(location, missing.Name, s.Name, variant.Name));
            }
            constructor = Resolve(location, $"variant '{name}'", [new Candidate(variant.InferringConstructor, null)], null, arguments);
            if (constructor is not null && !CheckTypeArguments(constructor.TypeArguments, location))
                return new BoundErrorExpression(location);
            if (constructor is not null)
            {
                type = s.Construct(constructor.TypeArguments);
                QueueBoundChecks(s.TypeParameters, constructor.TypeArguments, location);
            }
        }
        if (constructor is null)
            return new BoundErrorExpression(location);
        return new BoundVariant(location, type!, variant, ConvertArguments(constructor, arguments));
    }
}
