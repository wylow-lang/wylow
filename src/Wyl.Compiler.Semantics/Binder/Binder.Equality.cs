using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

/// <summary>
/// The <c>equals</c> and <c>hash</c> the compiler writes for a struct or enum that implements
/// <c>Equatable</c> or <c>Hashable</c> of itself without writing them (spec <c>library.equatable</c>).
/// The methods are written as syntax at the type's name, so that the binder declares, checks, and
/// binds them as it does a method the program wrote, in the program's own file.
/// </summary>
public sealed partial class Binder
{
    const string EqualsName = "equals";
    const string HashName = "hash";
    const string OtherName = "other";
    const string HasherName = "hasher";

    /// <summary>
    /// The local that the written <c>hash</c> adds to, which it copies back to its parameter at the
    /// end. The verifier cannot tell that a <c>ref</c> parameter never refers into a field of a type
    /// parameter's type, but it knows that a local of the function never does.
    /// </summary>
    const string StateName = "state";

    /// <summary>The methods to write for the type, after its fields and variants are declared.</summary>
    List<FunctionDecl> WrittenEquality(TypeSymbol s)
    {
        if (s.Kind is not (TypeKind.Struct or TypeKind.Enum) || s.IsCppClass || s.IsUnion
            || stdlib.Equatable is not { } equatable || stdlib.Hashable is not { } hashable)
            return [];
        var supertypes = Conversions.Supertypes(s.Type, function: null).ToList();
        bool isEquatable = supertypes.Contains(equatable.Construct([s.Type]));
        bool isHashable = supertypes.Contains(hashable.Construct([s.Type]));
        if (!isEquatable)
            return [];
        var writesEquals = s.Declaration.Methods.FirstOrDefault(m => m.Name == EqualsName);
        var writesHash = s.Declaration.Methods.FirstOrDefault(m => m.Name == HashName);
        // A method that a reported error keeps the compiler from writing is declared with an empty
        // body, so that the type does not also miss an interface method. The program never runs.
        if (isHashable && (writesEquals is null) != (writesHash is null))
        {
            var written = writesEquals ?? writesHash!;
            Error(Catalog.EqualityHalfWritten(written.NameLocation, s.Name, written.Name, written == writesEquals ? HashName : EqualsName));
            return [writesEquals is null ? WriteEquals(s, unwritable: true) : WriteHash(s, unwritable: true)];
        }
        if (writesEquals is not null)
            return [];
        // Hashable extends Equatable, so one check of the fields covers both methods.
        bool unwritable = !FieldsImplement(s, isHashable ? hashable : equatable);
        return isHashable ? [WriteEquals(s, unwritable), WriteHash(s, unwritable)] : [WriteEquals(s, unwritable)];
    }

    /// <summary>Whether every field's type implements the interface of itself, reporting each one that does not.</summary>
    bool FieldsImplement(TypeSymbol s, TypeSymbol iface)
    {
        bool all = true;
        foreach (var (name, type, location) in ValueFields(s))
        {
            var required = iface.Construct([type]);
            if (type == WylType.Error || stdlib.SatisfiesBound(type, required, function: null))
                continue;
            Error(Catalog.WrittenEqualityField(location, name, type.ToString(), required.ToString(), s.Declaration.NameLocation, s.Name, iface.Construct([s.Type]).ToString()));
            all = false;
        }
        return all;
    }

    /// <summary>The fields that equality compares: a struct's own, or every field of every variant of an enum.</summary>
    IEnumerable<(string Name, WylType Type, Location Location)> ValueFields(TypeSymbol s) =>
        s.Kind == TypeKind.Enum
            ? s.Variants.SelectMany(v => v.Fields.Select(f => (f.Name, f.Type, f.Declaration.NameLocation)))
            : s.Fields.Select(f => (f.Name, f.Type, f.Declaration.NameLocation));

    FunctionDecl WriteEquals(TypeSymbol s, bool unwritable)
    {
        var at = s.Declaration.NameLocation;
        var other = new ParameterSyntax(at, RefKind.In, SelfType(s), OtherName);
        List<Stmt> body = unwritable ? [new ReturnStmt(at, new BoolLiteralExpr(at, false))]
            : s.Kind == TypeKind.Enum
            ? [new SwitchStmt(at, new ThisExpr(at), [.. s.Variants.Select(v => EqualsCase(v, at))])]
            : [new ReturnStmt(at, All(at, s.Fields.Select(f => CallEquals(at, Field(at, new ThisExpr(at), f.Name), Field(at, Name(at, OtherName), f.Name)))))];
        return Method(at, "bool", EqualsName, other, body);
    }

    /// <summary><c>case V(in var a0, ...) -> { return other is V(in var b0, ...) &amp;&amp; a0.equals(b0) &amp;&amp; ...; }</c></summary>
    static SwitchCase EqualsCase(VariantSymbol variant, Location at)
    {
        var fields = variant.Fields.Select((_, i) => i).ToList();
        var matched = new IsExpr(at, Name(at, OtherName), VariantPattern(at, variant, "b"));
        var comparisons = fields.Select(i => CallEquals(at, Name(at, $"a{i}"), Name(at, $"b{i}")));
        var condition = All(at, [matched, .. comparisons]);
        return new SwitchCase(at, [VariantPattern(at, variant, "a")], new BlockStmt(at, [new ReturnStmt(at, condition)]), null);
    }

    FunctionDecl WriteHash(TypeSymbol s, bool unwritable)
    {
        var at = s.Declaration.NameLocation;
        var hasherType = new TypeSyntax(at, "Hasher", []) { Modules = [new PathName("std", at)] };
        var hasher = new ParameterSyntax(at, RefKind.Ref, hasherType, HasherName);
        if (unwritable)
            return Method(at, "void", HashName, hasher, []);
        List<Stmt> body = [new VarDeclStmt(at, hasherType, StateName, at, Name(at, HasherName)) { IsMutable = true }];
        if (s.Kind == TypeKind.Enum)
            body.Add(new SwitchStmt(at, new ThisExpr(at), [.. s.Variants.Select((v, index) => HashCase(v, index, at))]));
        else
            body.AddRange(s.Fields.Select(f => CallHash(at, Field(at, new ThisExpr(at), f.Name))));
        body.Add(new AssignStmt(at, Name(at, HasherName), TokenKind.Assign, at, Name(at, StateName)));
        return Method(at, "void", HashName, hasher, body);
    }

    /// <summary><c>case V(in var a0, ...) -> { state.add(index); a0.hash(ref state); ... }</c></summary>
    static SwitchCase HashCase(VariantSymbol variant, int index, Location at)
    {
        var addIndex = new ExprStmt(at, new CallExpr(at, Member(at, Name(at, StateName), "add"), [new IntLiteralExpr(at, (ulong)index, IntegerSuffix.None)]));
        var statements = new List<Stmt> { addIndex };
        statements.AddRange(variant.Fields.Select((_, i) => CallHash(at, Name(at, $"a{i}"))));
        return new SwitchCase(at, [VariantPattern(at, variant, "a")], new BlockStmt(at, statements), null);
    }

    /// <summary>A public method of the type, as <c>Equatable</c> and <c>Hashable</c> are public.</summary>
    static FunctionDecl Method(Location at, string returnType, string name, ParameterSyntax parameter, List<Stmt> body) =>
        new(at, Modifiers.None, new TypeSyntax(at, returnType, []), name, at, [], [parameter], new BlockStmt(at, body))
        {
            Visibility = Visibility.Public,
        };

    /// <summary>The type itself, with its own type parameters as its type arguments.</summary>
    static TypeSyntax SelfType(TypeSymbol s) =>
        new(s.Declaration.NameLocation, s.Name, [.. s.TypeParameters.Select(p => new TypeSyntax(s.Declaration.NameLocation, p.Name, []))]);

    /// <summary><c>V</c>, or <c>V(in var p0, in var p1, ...)</c> for a variant with fields.</summary>
    static VariantPatternSyntax VariantPattern(Location at, VariantSymbol variant, string prefix) =>
        new(at, null, variant.Name, at, variant.Declaration.Fields is null
            ? null
            : [.. variant.Fields.Select((_, i) => new ReferencePatternSyntax(at, RefKind.In, null, $"{prefix}{i}", at))]);

    static Expr All(Location at, IEnumerable<Expr> conditions) =>
        conditions.Aggregate((Expr?)null, (all, next) => all is null ? next : new BinaryExpr(at, all, TokenKind.AmpersandAmpersand, at, next))
        ?? new BoolLiteralExpr(at, true);

    static Expr CallEquals(Location at, Expr left, Expr right) => new CallExpr(at, Member(at, left, EqualsName), [right]);

    static Stmt CallHash(Location at, Expr value) =>
        new ExprStmt(at, new CallExpr(at, Member(at, value, HashName), [new RefArgExpr(at, Name(at, StateName))]));

    static Expr Field(Location at, Expr target, string name) => Member(at, target, name);

    static Expr Member(Location at, Expr target, string name) => new MemberAccessExpr(at, target, name, at, []);

    static Expr Name(Location at, string name) => new NameExpr(at, name, []);
}
