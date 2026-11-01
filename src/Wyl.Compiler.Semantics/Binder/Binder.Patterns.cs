using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    BoundExpression BindIs(IsExpr e)
    {
        var operand = BindExpression(e.Operand);
        if (operand.Type == WylType.Error || operand.Type == WylType.Void)
        {
            DeclareErrorBindings(e.Pattern);
            return operand.Type == WylType.Void
                ? ErrorExpression(e.Operand.Location, "'is' cannot test a 'void' value")
                : new BoundErrorExpression(e.Location);
        }

        if (e.Pattern is ReferencePatternSyntax reference)
            return BindReferencePattern(e, operand, reference);
        if (IsNullPattern(e.Pattern))
            return ErrorExpression(e.Pattern.Location, "'null' is a pattern only in a 'case'; compare with '== null'");
        // Other patterns match the value a reference refers to, once it is known to refer to one.
        operand = NarrowReference(operand);
        if (operand.Type.IsNullableReference)
        {
            DeclareErrorBindings(e.Pattern);
            return ErrorExpression(e.Operand.Location, MayReferToNothingMessage(operand));
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
            return ErrorExpression(e.Operand.Location, $"'is' can only test the type of class or interface values, not '{operand.Type}'");
        }
        int errors = diagnostics.Count;
        var pattern = BindPattern(e.Pattern, operand.Type, canBind: true);
        // A type test that always passes is allowed: it states the type, as an upcast would.
        if (diagnostics.Count == errors && pattern is not BoundTypePattern && Patterns.AlwaysMatches(pattern))
            Error(e.Pattern.Location, "this pattern always matches, so 'is' would always be true");
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
            Error(operand.Location, $"'is {keyword}' tests a reference that may refer to nothing, and '{text}' has type '{operand.Type}'");
        else if (target is not null && target != WylType.Error && target != referenced)
            Error(syntax.Type!.Location, $"'{text}' refers to a value of type '{referenced}', not '{target}'");
        else if (CheckReferredPlace(operand, syntax.RefKind, $"{WithArticle(keyword)} binding", mayBeNull: false, throughReference: true) && target != WylType.Error)
        {
            var binding = new LocalSymbol(syntax.Name, referenced.NullableReference(syntax.RefKind), isParameter: false, syntax.RefKind) { IsReferenceBinding = true };
            Declare(binding, syntax.NameLocation);
            return new BoundIs(e.Location, operand, new BoundReferencePattern(syntax.Location, operand.Type, binding));
        }
        DeclareErrorBindings(syntax);
        return new BoundErrorExpression(e.Location);
    }

    /// <summary>Binds a pattern matching values of <paramref name="type"/> and declares its names in the current scope.</summary>
    BoundPattern BindPattern(PatternSyntax syntax, WylType type, bool canBind)
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
                return BindStructPattern(v.Location, new TypeSyntax(v.NameLocation, v.Name, []), fields, type, canBind);
            case StructPatternSyntax s:
                return BindStructPattern(s.Location, s.Type, s.Fields, type, canBind);
            case VariantPatternSyntax v:
                return BindVariantPattern(v, type, canBind);
            case ReferencePatternSyntax r:
                return ErrorPattern(r, type, $"a '{FunctionSymbol.RefPrefix(r.RefKind).Trim()}' pattern can only test a reference with 'is', as in 'if (x is {FunctionSymbol.RefPrefix(r.RefKind)}T name)'");
            default:
                throw new InvalidOperationException($"unexpected pattern {syntax.GetType().Name}");
        }
    }

    LocalSymbol DeclareBinding(string name, WylType type, Location location, bool canBind)
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

    BoundPattern ErrorPattern(PatternSyntax syntax, WylType type, string message)
    {
        Error(syntax.Location, message);
        DeclareErrorBindings(syntax);
        return new BoundDiscardPattern(syntax.Location, type);
    }

    BoundPattern BindConstantPattern(ConstantPatternSyntax c, WylType type)
    {
        if (c.Value is NullLiteralExpr)
            return type.IsNullable ? new BoundNullPattern(c.Location, type) : ErrorPattern(c, type, $"a '{type}' is never null, so 'null' can never match it");
        var value = c.Value switch
        {
            IntLiteralExpr literal => BindIntegerLiteral(literal, c.Location, negated: false),
            UnaryExpr { Operand: IntLiteralExpr literal } => BindIntegerLiteral(literal, c.Location, negated: true),
            BoolLiteralExpr literal => new BoundLiteral(c.Location, WylType.Bool, literal.Value),
            CharLiteralExpr literal => new BoundLiteral(c.Location, WylType.Char, literal.Value),
            _ => ErrorExpression(c.Location, "only integer, character, and 'bool' literals can be patterns"),
        };
        if (value is not BoundLiteral literalValue)
            return new BoundDiscardPattern(c.Location, type);
        if (literalValue.Type == type)
            return new BoundConstantPattern(c.Location, literalValue);
        if (type.IsInteger && IsConvertibleConstant(literalValue, type))
            return new BoundConstantPattern(c.Location, BoundLiteral.Number(c.Location, type, literalValue.Integer!.Value));
        return ErrorPattern(c, type, $"a '{type}' can never be '{TextOf(c.Location)}'");
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
            if (!target.IsReference || (from.IsClass && target.IsClass && !Conversions.IsSubtype(from, target) && !Conversions.IsSubtype(target, from)))
            {
                Binding(WylType.Error);
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
            Binding(WylType.Error);
            Error(typeSyntax.Location, $"a '{type}' can never be a '{target}'");
            return new BoundDiscardPattern(location, type);
        }
        return Binding(target) is { } local ? new BoundBindingPattern(location, type, local) : new BoundDiscardPattern(location, type);
    }

    BoundPattern BindVariantPattern(VariantPatternSyntax v, WylType type, bool canBind)
    {
        var written = v.Enum is null ? v.Name : $"{TextOf(v.Enum.Location)}.{v.Name}";
        if (!type.IsEnum)
            return ErrorPattern(v, type, $"a '{type}' can never be '{written}', since '{type}' is neither an enum nor a struct");

        var s = type.Symbol!;
        if (v.Enum is { } enumSyntax)
        {
            // `Result.Ok(...)` may leave out the type arguments, which the value determines.
            if (!Names(enumSyntax, type))
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

    /// <summary>
    /// Whether a pattern's type names the matched value's type. Type arguments may be left out,
    /// since the value determines them, as in <c>Result.Ok(var v)</c>.
    /// </summary>
    bool Names(TypeSyntax syntax, WylType type) =>
        syntax is { TypeArguments.Count: 0, Modules.Count: 0 } && LookupTypeParameter(syntax.Name) is null
            ? LookupType(syntax.Name, syntax.Location) == type.Symbol
            : BindType(syntax, allowVoid: false) is var named && (named == type || named == WylType.Error);

    /// <summary>A struct's fields, each matching its pattern; on a nullable struct, also a test that it isn't null.</summary>
    BoundPattern BindStructPattern(Location location, TypeSyntax typeSyntax, IReadOnlyList<PatternSyntax> fieldSyntax, WylType type, bool canBind)
    {
        var value = type.NonNull;
        var syntax = new StructPatternSyntax(location, typeSyntax, fieldSyntax);
        if (!value.IsStruct || !Names(typeSyntax, value))
            return ErrorPattern(syntax, type, $"a '{type}' can never be a '{TextOf(typeSyntax.Location)}'");
        var s = value.Symbol!;
        if (fieldSyntax.Count != s.Fields.Count)
        {
            int count = s.Fields.Count;
            return ErrorPattern(syntax, type, $"'{s.Name}' has {count} field{(count == 1 ? "" : "s")}, but the pattern lists {fieldSyntax.Count}");
        }
        var map = TypeMap.For(value);
        var fields = fieldSyntax.Zip(s.Fields, (field, symbol) =>
        {
            CheckVisible(field.Location, symbol.Reach, $"field '{s.Name}.{symbol.Name}'");
            return BindPattern(field, map.Apply(symbol.Type), canBind);
        }).ToList();
        return new BoundStructPattern(location, type, fields);
    }

    static bool IsNullPattern(PatternSyntax pattern) => pattern is ConstantPatternSyntax { Value: NullLiteralExpr };

    /// <summary>
    /// A case's patterns: over a nullable value, <c>null</c> matches the value and the others its
    /// non-null type. Sets <paramref name="broken"/> if a pattern has an error, which would make
    /// coverage errors misleading.
    /// </summary>
    List<BoundPattern> BindCasePatterns(SwitchCase c, WylType type, ref bool broken)
    {
        int errors = diagnostics.Count;
        List<BoundPattern> patterns = [.. c.Patterns.Select(p => BindPattern(p, IsNullPattern(p) ? type : type.NonNull, canBind: c.Patterns.Count == 1))];
        if (c.Default is { } listed && !c.Patterns.All(IsNullPattern))
            Error(listed, "only 'null' can be listed with 'default', as in 'case null, default'");
        broken |= diagnostics.Count > errors;
        return patterns;
    }

    /// <summary>
    /// The value a switch matches. With a <c>case null</c> it is taken as it is, nullable or not;
    /// without one, patterns match values, not null, so a nullable local is narrowed, as for a member access.
    /// </summary>
    BoundExpression BindSwitchValue(Expr syntax, IReadOnlyList<SwitchCase> cases)
    {
        var value = BindExpression(syntax);
        if (value.Type == WylType.Void)
            return ErrorExpression(syntax.Location, "cannot switch on a 'void' value");
        if (value.Type == WylType.Null)
            return ErrorExpression(syntax.Location, "cannot switch on 'null', which has no type; give it a type first");
        if (cases.Any(c => c.Patterns.Any(IsNullPattern)))
        {
            if (value.Type.IsNullableReference)
                return ErrorExpression(syntax.Location, MayReferToNothingMessage(value));
            return CheckMatchable(value);
        }
        if (value.Type.MayBeNull)
        {
            value = AssumeNonNull(value);
            if (value.Type.IsNullableReference)
                return ErrorExpression(syntax.Location, $"cannot switch on '{value.Type}'; check it for null first, or use '!'");
            if (value.Type.IsNullable)
                return ErrorExpression(syntax.Location, $"cannot switch on a nullable '{value.Type}' without a 'case null'; check it for null first, or use '!' or '??'");
        }
        return CheckMatchable(value);
    }

    /// <summary>A matched value is read while patterns bind its parts, which a span's view could not be held through.</summary>
    BoundExpression CheckMatchable(BoundExpression value) =>
        value.Type.IsRefStruct ? ErrorExpression(value.Location, $"a '{value.Type}' cannot be matched against patterns") : value;

    BoundStatement BindSwitch(SwitchStmt s)
    {
        var value = BindSwitchValue(s.Value, s.Cases);
        var cases = new List<BoundSwitchCase<BoundStatement>>();
        bool broken = value.Type == WylType.Error;
        foreach (var c in s.Cases)
        {
            scope = new Scope(scope);
            var patterns = BindCasePatterns(c, value.Type, ref broken);
            var guard = BindGuard(c);
            cases.Add(new BoundSwitchCase<BoundStatement>(c.Location, patterns, c.IsDefault, guard, BindStatement(c.Body!)));
            scope = scope.Parent;
        }
        CheckCases(s.Location, value.Type, s.Cases, [.. cases.Select(c => c.Patterns)], broken);
        return new BoundSwitch(s.Location, value, cases);
    }

    /// <summary>
    /// A case's guard, bound in the case's scope after its patterns, so that it sees the names they
    /// bind and the case sees the names an <c>is</c> in it binds.
    /// </summary>
    BoundExpression? BindGuard(SwitchCase c)
    {
        if (c.Guard is not { } syntax)
            return null;
        if (c.IsDefault)
            Error(syntax.Location, "a case with 'default' cannot have a guard, since 'default' matches whatever the cases above don't");
        else if (c.Patterns.Any(IsNullPattern))
            Error(syntax.Location, "a case with 'null' cannot have a guard; test the condition inside the case");
        return BindCondition(syntax);
    }

    /// <summary>
    /// Whether a case counts toward exhaustiveness and hides later cases: one with a guard doesn't,
    /// since its condition may be false. One whose guard is an error counts, so as to add no more errors.
    /// </summary>
    static bool CountsAsMatching(SwitchCase c) => c.Guard is null || c.IsDefault || c.Patterns.Any(IsNullPattern);

    static bool IsNoReturn(BoundExpression expression) => expression is BoundCall { Method.Symbol.IsNoReturn: true };

    /// <summary>
    /// <paramref name="mayWait"/>: a call argument whose cases include lambdas without a function type
    /// is left pending until overload resolution gives it one (see <see cref="Choose"/>).
    /// </summary>
    BoundExpression BindSwitchExpression(SwitchExpr e, WylType? expected, bool mayWait = false)
    {
        var value = BindSwitchValue(e.Value, e.Cases);
        var options = new List<(SwitchCase Syntax, List<BoundPattern> Patterns, BoundExpression? Guard, BoundExpression Value, Scope Scope)>();
        bool broken = value.Type == WylType.Error;
        foreach (var c in e.Cases)
        {
            scope = new Scope(scope);
            var patterns = BindCasePatterns(c, value.Type, ref broken);
            var guard = BindGuard(c);
            options.Add((c, patterns, guard, BindOption(c.Value!, expected), scope));
            scope = scope.Parent!;
        }
        CheckCases(e.Location, value.Type, e.Cases, [.. options.Select(o => o.Patterns)], broken);
        return Choose(e.Location, [.. options.Select(o => o.Value)], expected, mayWait, type =>
        {
            // Lambdas are bound in their case's scope, so that using a name it declares is reported as such.
            var enclosing = scope;
            var bound = options.Select(o =>
            {
                scope = o.Scope;
                return new BoundSwitchCase<BoundExpression>(o.Syntax.Location, o.Patterns, o.Syntax.IsDefault, o.Guard, FinishOption(o.Value, type));
            }).ToList();
            scope = enclosing;
            return MakeSwitchExpression(e.Location, value, bound, expected ?? type);
        });
    }

    BoundExpression MakeSwitchExpression(
        Location location,
        BoundExpression value,
        List<BoundSwitchCase<BoundExpression>> bound,
        WylType? expected)
    {
        // A case that cannot complete, such as `default -> panic(...)`, needs no value.
        var producing = bound.Select(b => b.Body).Where(v => !IsNoReturn(v)).ToList();
        foreach (var caseValue in producing.Where(v => v.Type == WylType.Void))
            Error(caseValue.Location, "a switch expression's case must produce a value");
        if (value.Type == WylType.Error || producing.Any(v => v.Type == WylType.Error || v.Type == WylType.Void))
            return new BoundErrorExpression(location);
        if (producing.Count == 0)
            return ErrorExpression(location, "a switch expression needs a case that produces a value");

        var type = expected ?? CommonType(producing);
        if (type is null)
        {
            var found = string.Join(", ", producing.Select(v => $"'{v.Type}'").Distinct());
            return ErrorExpression(location, $"the cases of the switch have incompatible types {found}");
        }
        if (type.IsRefStruct)
            return ErrorExpression(location, $"a switch expression cannot produce a '{type}', since the result would view any of its cases; use a switch statement");
        if (type.IsNullableReference)
            return ErrorExpression(location, "a switch expression cannot produce a reference; use a switch statement with a variable for each case");
        // A nullable non-escaping choice would refer to a function made only for the choice.
        if (type.IsNullableNonEscapingFunction)
            type = EscapingForm(type);
        var cases = bound.Select(b => IsNoReturn(b.Body) ? b : b with { Body = Convert(b.Body, type) }).ToList();
        if (type.IsNonEscapingFunction)
            cases.ForEach(CheckCaseCaptures);
        return new BoundSwitchExpression(location, value, cases, type);
    }

    /// <summary>
    /// A switch expression that chooses a non-escaping function makes the functions it chooses from
    /// outside its cases, so their lambdas can't use the names a case declares.
    /// </summary>
    void CheckCaseCaptures(BoundSwitchCase<BoundExpression> c)
    {
        var declared = c.Patterns.SelectMany(Patterns.Bindings).Concat(c.Guard is { } guard ? IsBindings(guard) : []).Concat(IsBindings(c.Body)).ToHashSet();
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
    WylType? CommonType(IReadOnlyList<BoundExpression> values)
    {
        var candidates = values
            .Select(v => v.Type)
            .Where(t => t != WylType.Null)
            .SelectMany(t => t.CanBeNullable ? [t, t.Nullable] : new[] { t })
            .Distinct();
        // A non-escaping function that can't be converted is reported as that, once the type is chosen.
        return candidates.FirstOrDefault(t => values.All(v => CanConvert(v, t) || IsEscapingMismatch(v.Type, t)));
    }

    /// <summary>Checks that <c>default</c> comes last, that every case can match something, and that the cases cover every value.</summary>
    void CheckCases(Location location, WylType type, IReadOnlyList<SwitchCase> syntax, IReadOnlyList<IReadOnlyList<BoundPattern>> patterns, bool broken)
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
            foreach (var pattern in patterns[i])
            {
                if (!space.Add(pattern, counts: CountsAsMatching(syntax[i])))
                    Error(pattern.Location, "this case can never be reached: the cases above match every value it matches");
            }
            if (syntax[i].Default is { } written && !space.Add(null))
                Error(written, "'default' can never be reached: the cases above match every value");
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

    /// <summary>A hint for an undefined name that is the name of a variant.</summary>
    string VariantHint(string name, SourceText from)
    {
        IEnumerable<TypeSymbol> types = [.. FileNames(from).Types.Values, .. ModuleNames(from.Module).Types.Values, .. stdlib.LanguageTypes];
        if (types.FirstOrDefault(t => t.Kind == TypeKind.Enum && t.FindVariant(name) is not null) is not { } owner)
            return "";
        return owner.IsGeneric
            ? $"; to create the '{owner.Name}' variant, write '{owner.Name}<...>.{name}', or use it where a '{owner.Name}' is expected"
            : $"; to create the '{owner.Name}' variant, write '{owner.Name}.{name}'";
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
            var inferable = variant.Fields.SelectMany(f => f.Type.ContainedTypeParameters).ToHashSet();
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
}
