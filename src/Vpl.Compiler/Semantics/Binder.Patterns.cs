using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

public sealed partial class Binder
{
    BoundExpression BindIs(IsExpr e)
    {
        var operand = BindExpression(e.Operand);
        if (operand.Type == VplType.Error || operand.Type == VplType.Void)
        {
            DeclareErrorBindings(e.Pattern);
            return operand.Type == VplType.Void
                ? ErrorExpression(e.Operand.Location, "'is' cannot test a 'void' value")
                : new BoundErrorExpression(e.Location);
        }

        if (CheckMatchable(operand).Type == VplType.Error)
        {
            DeclareErrorBindings(e.Pattern);
            return new BoundErrorExpression(e.Location);
        }
        bool isTypeTest = e.Pattern is TypePatternSyntax || (e.Pattern is VariantPatternSyntax { Enum: null, Fields: null } && !operand.Type.IsEnum);
        if (e.Pattern is DiscardPatternSyntax or VarPatternSyntax)
            Error(e.Pattern.Location, "this pattern always matches, so 'is' would always be true");
        else if (isTypeTest && !operand.Type.IsReference && !operand.Type.IsNullableValue)
        {
            DeclareErrorBindings(e.Pattern);
            return ErrorExpression(e.Operand.Location, $"'is' can only test the type of class or interface values, not '{operand.Type}'");
        }
        var pattern = BindPattern(e.Pattern, operand.Type, canBind: true);
        return new BoundIs(e.Location, operand, pattern);
    }

    /// <summary>Binds a pattern matching values of <paramref name="type"/> and declares its names in the current scope.</summary>
    BoundPattern BindPattern(PatternSyntax syntax, VplType type, bool canBind)
    {
        if (type == VplType.Error)
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
            case VariantPatternSyntax v:
                return BindVariantPattern(v, type, canBind);
            default:
                throw new InvalidOperationException($"unexpected pattern {syntax.GetType().Name}");
        }
    }

    LocalSymbol DeclareBinding(string name, VplType type, Location location, bool canBind)
    {
        if (!canBind)
            Error(location, "a case with several patterns cannot bind names");
        var local = new LocalSymbol(name, type, isParameter: false);
        Declare(local, location);
        return local;
    }

    /// <summary>Declares a broken pattern's names, so that their uses don't report them as undefined.</summary>
    void DeclareErrorBindings(PatternSyntax syntax)
    {
        switch (syntax)
        {
            case VarPatternSyntax v:
                Declare(new LocalSymbol(v.Name, VplType.Error, isParameter: false), v.NameLocation);
                break;
            case TypePatternSyntax { Name: { } name } t:
                Declare(new LocalSymbol(name, VplType.Error, isParameter: false), t.NameLocation);
                break;
            case VariantPatternSyntax { Fields: { } fields }:
                foreach (var field in fields)
                    DeclareErrorBindings(field);
                break;
        }
    }

    BoundPattern ErrorPattern(PatternSyntax syntax, VplType type, string message)
    {
        Error(syntax.Location, message);
        DeclareErrorBindings(syntax);
        return new BoundDiscardPattern(syntax.Location, type);
    }

    BoundPattern BindConstantPattern(ConstantPatternSyntax c, VplType type)
    {
        var value = c.Value switch
        {
            IntLiteralExpr literal => BindIntegerLiteral(literal, c.Location, negated: false),
            UnaryExpr { Operand: IntLiteralExpr literal } => BindIntegerLiteral(literal, c.Location, negated: true),
            BoolLiteralExpr literal => new BoundLiteral(c.Location, VplType.Bool, literal.Value),
            NullLiteralExpr => ErrorExpression(c.Location, "'null' cannot be a pattern; compare with '== null', or use 'default'"),
            _ => ErrorExpression(c.Location, "only integer and 'bool' literals can be patterns"),
        };
        if (value is not BoundLiteral literalValue)
            return new BoundDiscardPattern(c.Location, type);
        if (literalValue.Type == type)
            return new BoundConstantPattern(c.Location, literalValue);
        if (type == VplType.Long && literalValue.Value is int small)
            return new BoundConstantPattern(c.Location, new BoundLiteral(c.Location, VplType.Long, (long)small));
        return ErrorPattern(c, type, $"a '{type}' can never be '{TextOf(c.Location)}'");
    }

    /// <summary>
    /// For a reference, a test of its runtime type. For a nullable value, a test that it is not null.
    /// For another value type, which has no subtypes, the pattern only states the type, which must
    /// be exactly the value's.
    /// </summary>
    BoundPattern BindTypePattern(Location location, TypeSyntax typeSyntax, string? name, Location nameLocation, VplType type, bool canBind)
    {
        var target = BindType(typeSyntax, allowVoid: false);
        LocalSymbol? Binding(VplType bindingType) => name is null ? null : DeclareBinding(name, bindingType, nameLocation, canBind);
        if (target == VplType.Error)
        {
            Binding(VplType.Error);
            return new BoundDiscardPattern(location, type);
        }

        if (type.IsReference)
        {
            var from = type.NonNull;
            if (!target.IsReference || (from.IsClass && target.IsClass && !Conversions.IsSubtype(from, target) && !Conversions.IsSubtype(target, from)))
            {
                Binding(VplType.Error);
                Error(typeSyntax.Location, $"a '{from}' can never be a '{target}'");
                return new BoundDiscardPattern(location, type);
            }
            return new BoundTypePattern(location, type, target, Binding(target));
        }

        // A nullable value is a value of its type exactly when it is not null.
        if (type.IsNullableValue && target == type.NonNull)
            return new BoundTypePattern(location, type, target, Binding(target));
        if (target != type)
        {
            Binding(VplType.Error);
            Error(typeSyntax.Location, $"a '{type}' can never be a '{target}'");
            return new BoundDiscardPattern(location, type);
        }
        return Binding(target) is { } local ? new BoundBindingPattern(location, type, local) : new BoundDiscardPattern(location, type);
    }

    BoundPattern BindVariantPattern(VariantPatternSyntax v, VplType type, bool canBind)
    {
        var written = v.Enum is null ? v.Name : $"{TextOf(v.Enum.Location)}.{v.Name}";
        if (!type.IsEnum)
            return ErrorPattern(v, type, $"a '{type}' can never be '{written}', since '{type}' is not an enum");

        var s = type.Symbol!;
        if (v.Enum is { } enumSyntax)
        {
            // `Result.Ok(...)` may leave out the type arguments, which the value determines.
            bool sameEnum = enumSyntax.TypeArguments.Count == 0 && LookupTypeParameter(enumSyntax.Name) is null
                ? types.GetValueOrDefault(enumSyntax.Name) == s
                : BindType(enumSyntax, allowVoid: false) is var named && (named == type || named == VplType.Error);
            if (!sameEnum)
                return ErrorPattern(v, type, $"a '{type}' can never be '{written}'");
        }
        if (s.FindVariant(v.Name) is not { } variant)
            return ErrorPattern(v, type, $"'{s.Name}' has no variant '{v.Name}'");

        var map = TypeMap.For(type);
        if (v.Fields is null)
            return new BoundVariantPattern(v.Location, type, variant, [.. variant.Fields.Select(f => new BoundDiscardPattern(v.Location, map.Apply(f.Type)))]);
        if (variant.Fields.Count == 0)
            return ErrorPattern(v, type, $"'{variant.Name}' carries no data; match it without parentheses");
        if (v.Fields.Count != variant.Fields.Count)
        {
            int count = variant.Fields.Count;
            return ErrorPattern(v, type, $"'{variant.Name}' has {count} field{(count == 1 ? "" : "s")}, but the pattern lists {v.Fields.Count}");
        }
        var fields = v.Fields.Zip(variant.Fields, (field, symbol) => BindPattern(field, map.Apply(symbol.Type), canBind)).ToList();
        return new BoundVariantPattern(v.Location, type, variant, fields);
    }

    /// <summary>Sets <paramref name="broken"/> if a pattern has an error, which would make coverage errors misleading.</summary>
    List<BoundPattern> BindCasePatterns(SwitchCase c, VplType type, ref bool broken)
    {
        int errors = diagnostics.Count;
        List<BoundPattern> patterns = [.. c.Patterns.Select(p => BindPattern(p, type, canBind: c.Patterns.Count == 1))];
        broken |= diagnostics.Count > errors;
        return patterns;
    }

    BoundExpression BindSwitchValue(Expr syntax)
    {
        var value = BindExpression(syntax);
        if (value.Type == VplType.Void)
            return ErrorExpression(syntax.Location, "cannot switch on a 'void' value");
        // Patterns match values, not null: a nullable local is narrowed, as for a member access.
        if (value.Type.IsNullableValue)
        {
            value = AssumeNonNull(value);
            if (value.Type.IsNullableValue)
                return ErrorExpression(syntax.Location, $"cannot switch on a nullable '{value.Type}'; check it for null first, or use '!' or '??'");
        }
        return CheckMatchable(value);
    }

    /// <summary>A matched value is read while patterns bind its parts, which a span's view could not be held through.</summary>
    BoundExpression CheckMatchable(BoundExpression value) =>
        value.Type.IsRefStruct ? ErrorExpression(value.Location, $"a '{value.Type}' cannot be matched against patterns") : value;

    BoundStatement BindSwitch(SwitchStmt s)
    {
        var value = BindSwitchValue(s.Value);
        var cases = new List<BoundSwitchCase<BoundStatement>>();
        bool broken = value.Type == VplType.Error;
        foreach (var c in s.Cases)
        {
            scope = new Scope(scope);
            var patterns = BindCasePatterns(c, value.Type, ref broken);
            cases.Add(new BoundSwitchCase<BoundStatement>(c.Location, patterns, BindStatement(c.Body!)));
            scope = scope.Parent;
        }
        CheckCases(s.Location, value.Type, s.Cases, [.. cases.Select(c => c.Patterns)], broken);
        return new BoundSwitch(s.Location, value, cases);
    }

    static bool IsNoReturn(BoundExpression expression) => expression is BoundCall { Method.Symbol.IsNoReturn: true };

    BoundExpression BindSwitchExpression(SwitchExpr e, VplType? expected)
    {
        var value = BindSwitchValue(e.Value);
        var unbound = new List<(Location Location, List<BoundPattern> Patterns, BoundExpression? Value, Scope Scope)>();
        bool broken = value.Type == VplType.Error;
        foreach (var c in e.Cases)
        {
            scope = new Scope(scope);
            var patterns = BindCasePatterns(c, value.Type, ref broken);
            // Without a target type, a lambda takes another case's function type, bound below.
            unbound.Add((c.Location, patterns, expected is null && c.Value is LambdaExpr ? null : BindExpression(c.Value!, expected), scope));
            scope = scope.Parent!;
        }
        var otherType = unbound.Select(b => b.Value?.Type).FirstOrDefault(t => t is { IsFunction: true });
        var bound = unbound.Zip(e.Cases, (b, c) =>
        {
            if (b.Value is { } caseValue)
                return (b.Location, b.Patterns, Value: caseValue);
            var enclosing = scope;
            scope = b.Scope;
            caseValue = BindExpression(c.Value!, otherType);
            scope = enclosing;
            return (b.Location, b.Patterns, Value: caseValue);
        }).ToList();
        CheckCases(e.Location, value.Type, e.Cases, [.. bound.Select(b => b.Patterns)], broken);

        // A case that cannot complete, such as `default -> panic(...)`, needs no value.
        var producing = bound.Select(b => b.Value).Where(v => !IsNoReturn(v)).ToList();
        foreach (var caseValue in producing.Where(v => v.Type == VplType.Void))
            Error(caseValue.Location, "a switch expression's case must produce a value");
        if (value.Type == VplType.Error || producing.Any(v => v.Type == VplType.Error || v.Type == VplType.Void))
            return new BoundErrorExpression(e.Location);
        if (producing.Count == 0)
            return ErrorExpression(e.Location, "a switch expression needs a case that produces a value");

        var type = expected ?? CommonType(producing);
        if (type is null)
        {
            var found = string.Join(", ", producing.Select(v => $"'{v.Type}'").Distinct());
            return ErrorExpression(e.Location, $"the cases of the switch have incompatible types {found}");
        }
        if (type.IsRefStruct)
            return ErrorExpression(e.Location, $"a switch expression cannot produce a '{type}', since the result would view any of its cases; use a switch statement");
        if (type.IsNullable && type.NonNull.IsFunction)
            return ErrorExpression(e.Location, $"'({type.NonNull})?' is not supported yet; nullable functions come with escaping functions");
        var cases = bound.Select(b => new BoundSwitchCase<BoundExpression>(b.Location, b.Patterns, IsNoReturn(b.Value) ? b.Value : Convert(b.Value, type))).ToList();
        if (type.IsFunction)
            cases.ForEach(CheckCaseCaptures);
        return new BoundSwitchExpression(e.Location, value, cases, type);
    }

    /// <summary>
    /// A switch expression that chooses a function makes the functions it chooses from outside its
    /// cases, so their lambdas can't use the names a case declares.
    /// </summary>
    void CheckCaseCaptures(BoundSwitchCase<BoundExpression> c)
    {
        var declared = c.Patterns.SelectMany(Patterns.Bindings).Concat(IsBindings(c.Body)).ToHashSet();
        var use = BoundTree.FunctionOptions(c.Body).OfType<BoundLambda>()
            .SelectMany(BoundTree.AllExpressions).OfType<BoundLocalAccess>()
            .FirstOrDefault(a => declared.Contains(a.Local));
        if (use is not null)
            Error(use.Location, $"a lambda chosen by a switch expression cannot use '{use.Local.Name}', which its case declares; use a switch statement");
    }

    /// <summary>The names declared by <c>is</c> patterns in an expression, but not inside its lambdas.</summary>
    static IEnumerable<LocalSymbol> IsBindings(BoundExpression expression) => expression switch
    {
        BoundLambda => [],
        BoundIs pattern => [.. IsBindings(pattern.Operand), .. Patterns.Bindings(pattern.Pattern)],
        _ => BoundTree.Children(expression).SelectMany(IsBindings),
    };

    /// <summary>The first case's type, or a later one, that every case converts to; a type may become nullable for a <c>null</c> case.</summary>
    VplType? CommonType(IReadOnlyList<BoundExpression> values)
    {
        var candidates = values
            .Select(v => v.Type)
            .Where(t => t != VplType.Null)
            .SelectMany(t => t.CanBeNullable ? [t, t.Nullable] : new[] { t })
            .Distinct();
        return candidates.FirstOrDefault(t => values.All(v => CanConvert(v, t)));
    }

    /// <summary>Checks that <c>default</c> comes last, that every case can match something, and that the cases cover every value.</summary>
    void CheckCases(Location location, VplType type, IReadOnlyList<SwitchCase> syntax, IReadOnlyList<IReadOnlyList<BoundPattern>> patterns, bool broken)
    {
        for (int i = 0; i < syntax.Count - 1; i++)
        {
            if (syntax[i].IsDefault)
                Error(syntax[i].Location, "'default' must be the last case");
        }
        if (broken)
            return;

        var space = new MatchSpace(type);
        for (int i = 0; i < syntax.Count; i++)
        {
            if (syntax[i].IsDefault)
            {
                if (!space.Add(null))
                    Error(syntax[i].Location, "'default' can never be reached: the cases above match every value");
                continue;
            }
            foreach (var pattern in patterns[i])
            {
                if (!space.Add(pattern))
                    Error(pattern.Location, "this case can never be reached: the cases above match every value it matches");
            }
        }
        if (space.Missing() is { } missing)
        {
            Error(location, missing == "_"
                ? $"the switch does not handle every '{type}'; add a 'default' case"
                : $"the switch does not handle every '{type}': '{missing}' is not covered");
        }
    }

    /// <summary>
    /// The enum named by the target of <c>Enum.Variant</c>. A generic enum written without type
    /// arguments takes them from <paramref name="expected"/> if that is the same enum, and otherwise
    /// has a null type, which is inferred from the variant's fields.
    /// </summary>
    (TypeSymbol Enum, VplType? Type)? AsEnumName(Expr target, VplType? expected)
    {
        if (target is not NameExpr name || IsValueName(name.Name) || !types.TryGetValue(name.Name, out var s) || s.Kind != TypeKind.Enum)
            return null;
        if (name.TypeArguments.Count == 0 && s.IsGeneric)
            return (s, expected?.Symbol == s ? expected : null);
        return (s, BindTypeName(s.Name, name.TypeArguments, name.Location));
    }

    /// <summary>A variant of the expected enum written without its enum, as in <c>return Ok(value);</c>.</summary>
    static VariantSymbol? ExpectedVariant(string name, VplType? expected) =>
        expected is { IsEnum: true } ? expected.Symbol!.FindVariant(name) : null;

    /// <summary>A hint for an undefined name that is the name of a variant.</summary>
    string VariantHint(string name)
    {
        if (types.Values.FirstOrDefault(t => t.Kind == TypeKind.Enum && t.FindVariant(name) is not null) is not { } owner)
            return "";
        return owner.IsGeneric
            ? $"; to create the '{owner.Name}' variant, write '{owner.Name}<...>.{name}', or use it where a '{owner.Name}' is expected"
            : $"; to create the '{owner.Name}' variant, write '{owner.Name}.{name}'";
    }

    /// <summary>
    /// Creates a variant. <paramref name="type"/> is null for a generic enum whose type arguments
    /// must be inferred; <paramref name="pending"/> is null when no parentheses were written.
    /// </summary>
    BoundExpression BindVariant(Location location, TypeSymbol s, VplType? type, VariantSymbol variant, PendingArguments? pending)
    {
        if (type == VplType.Error)
            return new BoundErrorExpression(location);
        var arguments = pending?.Bind([type is null ? new Candidate(variant.InferringConstructor, null) : new Candidate(variant.Constructor, type)]);
        if (arguments is not null && AnyError(arguments))
            return new BoundErrorExpression(location);
        var name = $"{s.Name}.{variant.Name}";
        if (arguments is null && variant.Fields.Count > 0)
            return ErrorExpression(location, $"'{name}' carries data; create it with '{name}(...)'");
        if (arguments is not null && variant.Fields.Count == 0)
            return ErrorExpression(location, $"'{name}' carries no data; write it without parentheses");
        arguments ??= [];
        if (arguments.Count != variant.Fields.Count)
        {
            int count = variant.Fields.Count;
            return ErrorExpression(location, $"'{name}' has {count} field{(count == 1 ? "" : "s")}, but {arguments.Count} value{(arguments.Count == 1 ? " was" : "s were")} given");
        }

        MethodReference? constructor;
        if (type is not null)
        {
            constructor = Resolve(location, $"variant '{name}'", [new Candidate(variant.Constructor, type)], null, arguments);
        }
        else
        {
            var inferable = variant.Fields.SelectMany(f => TypeParametersIn(f.Type)).ToHashSet();
            if (s.TypeParameters.FirstOrDefault(p => !inferable.Contains(p)) is { } missing)
            {
                return ErrorExpression(location,
                    $"cannot infer type argument '{missing.Name}' of '{s.Name}' from '{variant.Name}'; use it where a '{s.Name}' is expected, or write '{s.Name}<...>.{variant.Name}'");
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

    static IEnumerable<TypeParameterSymbol> TypeParametersIn(VplType type)
    {
        var t = type.NonNull;
        if (t.TypeParameter is { } parameter)
            return [parameter];
        IEnumerable<VplType> parts = [.. t.TypeArguments, .. t.FunctionParameters.Select(p => p.Type)];
        if (t.ElementType is { } element)
            parts = parts.Append(element);
        if (t.FunctionReturnType is { } returnType)
            parts = parts.Append(returnType);
        return parts.SelectMany(TypeParametersIn);
    }
}
