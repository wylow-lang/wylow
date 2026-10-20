using System.Globalization;
using System.Text;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.CodeGen;

public sealed partial class CppEmitter
{
    static string ParameterType(VplType type, RefKind refKind) => refKind switch
    {
        RefKind.Ref => CppType(type) + "&",
        RefKind.In => $"const {CppType(type)}&",
        _ => CppType(type),
    };

    string Parameters(FunctionSymbol function) =>
        string.Join(", ", function.Parameters.Select(p => $"{ParameterType(p.Type, p.RefKind)} {LocalName(p)}"));

    string Parameters(MethodReference method) =>
        string.Join(", ", method.Symbol.Parameters.Select((p, i) => $"{ParameterType(method.ParameterType(i), p.RefKind)} {LocalName(p)}"));

    string MemberDeclaration(FunctionSymbol member)
    {
        var owner = member.ContainingType!;
        if (member.IsConstructor)
        {
            return owner.Kind == TypeKind.Struct
                ? $"static {TypeName(owner)} make({Parameters(member)})"
                : $"{TypeName(owner)}({Parameters(member)})";
        }
        if (member.IsDestructor)
            return $"~{TypeName(owner)}() override";
        var signature = $"{CppType(member.ReturnType)} {MethodName(member)}({Parameters(member)})";
        if (member.IsStatic)
            return "static " + signature;
        if (member.IsVirtual)
            return "virtual " + signature;
        if (member.IsOverride)
            return signature + (member.IsFinal ? " override final" : " override");
        return signature + (member.IsReadOnly ? " const" : "");
    }

    string DefinitionSignature(FunctionSymbol function)
    {
        if (function.ContainingType is not { } s)
            return $"{CppType(function.ReturnType)} {FunctionName(function)}({Parameters(function)})";
        var owner = ObjectType(s.Type);
        if (function.IsConstructor)
        {
            return s.Kind == TypeKind.Struct
                ? $"{owner} {owner}::make({Parameters(function)})"
                : $"{owner}::{TypeName(s)}({Parameters(function)})";
        }
        if (function.IsDestructor)
            return $"{owner}::~{TypeName(s)}()";
        var signature = $"{CppType(function.ReturnType)} {owner}::{MethodName(function)}({Parameters(function)})";
        return function.IsReadOnly ? signature + " const" : signature;
    }

    void EmitFunction(BoundFunction function)
    {
        var symbol = function.Symbol;
        currentType = symbol.ContainingType;
        currentFunction = symbol;
        usedLocalNames.Clear();
        usedLocalNames.UnionWith(symbol.Parameters.Select(LocalName));
        bool isClassMember = currentType is { Kind: TypeKind.Class or TypeKind.Interface };
        context = isClassMember && !symbol.IsStatic ? Context.ClassMember
            : symbol.IsConstructor ? Context.StructConstructor
            : symbol.IsInstanceMethod ? Context.Method
            : Context.Free;

        if (currentType is { IsGeneric: true } owner)
            Line(TemplatePrefix(owner.TypeParameters).TrimEnd());
        if (symbol.IsGeneric)
            Line(TemplatePrefix(symbol.TypeParameters).TrimEnd());
        Line(DefinitionSignature(symbol));

        var statements = function.Body.Statements;
        if (symbol.IsConstructor && isClassMember && statements.FirstOrDefault() is BoundSuperCall super)
        {
            // Braces evaluate the arguments left to right.
            var arguments = string.Join(", ", super.Arguments.Select(a =>
                !IsStable(a) && MayHoldReferences(a.Type) ? $"vpl::keep({Expression(a)})" : Expression(a)));
            Line($"    : {ObjectType(super.Constructor.Owner!)}{{{arguments}}}");
            statements = [.. statements.Skip(1)];
        }

        Line("{");
        indent++;
        if (symbol.IsConstructor && isClassMember)
        {
            foreach (var field in currentType!.Fields)
            {
                if (field.Initializer is { } initializer)
                {
                    DeclarePatternLocals(initializer);
                    Line($"this->{FieldName(field)} = {Expression(initializer)};");
                }
                else if (!field.IsWeak && NeedsDefaultCode(field.Type) && !field.Type.IsTypeParameter)
                    Line($"this->{FieldName(field)} = {DefaultValue(field.Type)};");
            }
        }
        else if (symbol.IsConstructor)
        {
            var type = currentType!.Type;
            Line($"{CppType(type)} self = {DefaultValue(type)};");
        }
        foreach (var statement in statements)
            Statement(statement);
        if (symbol.IsConstructor && !isClassMember)
        {
            Line("return self;");
        }
        else if (symbol.ReturnType != VplType.Void)
        {
            var body = function.Body.Location;
            Line($"vpl::unreachable({SourceLocation(body with { Span = new TextSpan(body.Span.End - 1, 1) })});");
        }
        indent--;
        Line("}");
        context = Context.Free;
        currentType = null;
    }

    void Statement(BoundStatement statement)
    {
        switch (statement)
        {
            case BoundBlock block:
                Line("{");
                indent++;
                foreach (var s in block.Statements)
                    Statement(s);
                indent--;
                Line("}");
                break;
            case BoundVariableDeclaration declaration:
                DeclarePatternLocals(declaration.Initializer);
                HoistTry(declaration.Initializer);
                var initializer = declaration.Initializer is null ? "{}" : " = " + Expression(declaration.Initializer);
                Line($"{CppType(declaration.Local.Type)} {LocalName(declaration.Local)}{initializer};");
                EndTry(declaration.Initializer);
                break;
            case BoundAssignment assignment when FindTry(assignment.Value) is not null:
            {
                DeclarePatternLocals(assignment.Value);
                // The object holding the target is read before the value, as in any assignment.
                var root = PinRoot(assignment.Target);
                if (root is not null)
                {
                    var pin = NewTemp();
                    Line($"auto {pin} = {Expression(root)};");
                    replacements[root] = pin;
                }
                HoistTry(assignment.Value);
                Line(Store(assignment) + ";");
                EndTry(assignment.Value);
                if (root is not null)
                    replacements.Remove(root);
                break;
            }
            case BoundAssignment assignment:
                DeclarePatternLocals(assignment.Value);
                Line(SimpleStatement(statement) + ";");
                break;
            // The unused value is released by the end of the statement, as other temporaries are.
            case BoundExpressionStatement { Expression: var expression } when FindTry(expression) is not null:
                Line("{");
                indent++;
                DeclarePatternLocals(expression);
                HoistTry(expression);
                EndTry(expression);
                indent--;
                Line("}");
                break;
            case BoundExpressionStatement expressionStatement:
                DeclarePatternLocals(expressionStatement.Expression);
                Line(SimpleStatement(statement) + ";");
                break;
            case BoundSwitch switchStatement:
                Switch(switchStatement);
                break;
            case BoundIf ifStatement:
                OpenPatternScope(ifStatement.Condition);
                Line($"if ({Expression(ifStatement.Condition)})");
                Statement(ifStatement.Then);
                if (ifStatement.Else is not null)
                {
                    Line("else");
                    Statement(ifStatement.Else);
                }
                ClosePatternScope(ifStatement.Condition);
                break;
            case BoundWhile whileStatement:
                OpenPatternScope(whileStatement.Condition);
                Line($"while ({Expression(whileStatement.Condition)})");
                Statement(whileStatement.Body);
                ClosePatternScope(whileStatement.Condition);
                break;
            case BoundFor forStatement:
                Line("{");
                indent++;
                if (forStatement.Initializer is not null)
                    Statement(forStatement.Initializer);
                DeclarePatternLocals(forStatement.Condition);
                var condition = forStatement.Condition is null ? "" : Expression(forStatement.Condition);
                DeclarePatternLocals(forStatement.Update switch
                {
                    BoundAssignment assignment => assignment.Value,
                    BoundExpressionStatement expressionStatement => expressionStatement.Expression,
                    _ => null,
                });
                var update = forStatement.Update is null ? "" : SimpleStatement(forStatement.Update);
                Line($"for (; {condition}; {update})");
                Statement(forStatement.Body);
                indent--;
                Line("}");
                break;
            case BoundReturn returnStatement:
                DeclarePatternLocals(returnStatement.Value);
                HoistTry(returnStatement.Value);
                if (context == Context.StructConstructor)
                    Line("return self;");
                else
                    Line(returnStatement.Value is null ? "return;" : $"return {Expression(returnStatement.Value)};");
                EndTry(returnStatement.Value);
                break;
            case BoundBreak:
                Line("break;");
                break;
            case BoundContinue:
                Line("continue;");
                break;
            default:
                throw new InvalidOperationException($"unexpected statement {statement.GetType().Name}");
        }
    }

    /// <summary>The <c>try</c> that is a statement's whole value, under any conversion of its result.</summary>
    static BoundTry? FindTry(BoundExpression? value) => value switch
    {
        BoundTry attempt => attempt,
        BoundConversion conversion => FindTry(conversion.Operand),
        _ => null,
    };

    /// <summary>
    /// C++ cannot return from inside an expression, so a statement's <c>try</c> runs first: an
    /// <c>Err</c> returns, and the statement then uses the <c>Ok</c> value. Since the <c>try</c> is
    /// the whole value, nothing else in the statement is evaluated before it, except an
    /// assignment's target object, which the caller reads first.
    /// </summary>
    void HoistTry(BoundExpression? value)
    {
        if (FindTry(value) is not { } attempt)
            return;
        var result = attempt.Operand.Type.Symbol!;
        var (ok, err) = (result.FindVariant("Ok")!, result.FindVariant("Err")!);
        var temp = NewTemp();
        Line($"auto {temp} = {Expression(attempt.Operand)};");
        Line($"if ({temp}.tag == {TagValue(err)})");
        Line("{");
        Line($"    return {CppType(currentFunction!.ReturnType)}::{VariantFactory(err)}(std::move({temp}.{UnionMember(err)}.{FieldName(err.Fields[0])}));");
        Line("}");
        replacements[attempt] = $"std::move({temp}.{UnionMember(ok)}.{FieldName(ok.Fields[0])})";
    }

    void EndTry(BoundExpression? value)
    {
        if (FindTry(value) is { } attempt)
            replacements.Remove(attempt);
    }

    static IEnumerable<LocalSymbol> PatternLocals(BoundExpression? expression) => expression switch
    {
        null => [],
        BoundIs pattern => [.. PatternLocals(pattern.Operand), .. Patterns.Bindings(pattern.Pattern)],
        // A switch expression declares its cases' names inside itself.
        BoundSwitchExpression switchExpression => PatternLocals(switchExpression.Value),
        _ => Children(expression).SelectMany(PatternLocals),
    };

    void DeclarePatternLocals(BoundExpression? expression)
    {
        foreach (var local in PatternLocals(expression))
            Line($"{CppType(local.Type)} {LocalName(local)}{{}};");
    }

    // Pattern names in a condition are scoped to the statement, as in VPL.
    void OpenPatternScope(BoundExpression condition)
    {
        if (!PatternLocals(condition).Any())
            return;
        Line("{");
        indent++;
        DeclarePatternLocals(condition);
    }

    void ClosePatternScope(BoundExpression condition)
    {
        if (!PatternLocals(condition).Any())
            return;
        indent--;
        Line("}");
    }

    string SimpleStatement(BoundStatement statement) => statement switch
    {
        BoundAssignment assignment => Assignment(assignment),
        BoundExpressionStatement { IsDiscard: true } discard => $"static_cast<void>({Expression(discard.Expression)})",
        BoundExpressionStatement expressionStatement => Expression(expressionStatement.Expression),
        _ => throw new InvalidOperationException($"unexpected simple statement {statement.GetType().Name}"),
    };

    /// <summary>
    /// C++17 evaluates the right side of '=' before the left. That only matters when the target
    /// goes through an object that the value's evaluation could replace; that object is then
    /// pinned first, which also keeps it alive for the store.
    /// </summary>
    string Assignment(BoundAssignment assignment)
    {
        var root = PinRoot(assignment.Target);
        if (root is null || !(HasEffects(assignment.Value) || Writes(assignment.Value)))
            return Store(assignment);

        var pin = NewTemp();
        var rootText = Expression(root);
        replacements[root] = pin;
        var store = Store(assignment);
        replacements.Remove(root);
        return $"[&] {{ auto {pin} = {rootText}; {store}; }}()";
    }

    string Store(BoundAssignment assignment)
    {
        var target = Place(assignment.Target);
        var value = Expression(assignment.Value);
        var type = assignment.Target.Type;
        bool plain = type.IsReference || !(type.IsStructOrEnum || type.IsTypeParameter) || assignment.Target is BoundFieldAccess { Field.IsWeak: true };
        return plain ? $"{target} = {value}" : $"vpl::assign({target}, {value})";
    }
}
