using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Syntax;

public sealed partial class Parser
{
    sealed class Members
    {
        public readonly List<FieldDecl> Fields = [];
        public readonly List<FunctionDecl> Methods = [];
        public DestructorDecl? Destructor;
    }

    TypeDecl ParseTypeDecl(int start, ModifierList modifiers, TypeKind kind)
    {
        Advance();
        // The standard library declares a primitive type's members in an extern struct of its name;
        // the binder rejects one outside it.
        var name = kind == TypeKind.Struct && (modifiers.Flags & Modifiers.Extern) != 0 && SyntaxFacts.IsPrimitiveTypeKeyword(Current.Kind) && !At(TokenKind.VoidKeyword)
            ? Advance()
            : Expect(TokenKind.Identifier, kind switch
        {
            TypeKind.Struct => "a struct name",
            TypeKind.Class => "a class name",
            TypeKind.Enum => "an enum name",
            _ => "an interface name",
        });
        var typeParameters = ParseTypeParameters();

        if (kind == TypeKind.Enum && At(TokenKind.Colon))
        {
            int colon = position;
            Advance();
            ParseType(allowNullable: false);
            var type = source.Text[tokens[colon + 1].Span.Start..Previous.Span.End];
            Report(Catalog.EnumIntegerTypeSyntax(Loc(TextSpan.FromBounds(tokens[colon].Span.Start, Previous.Span.End)), type, name.Text));
        }

        TypeSyntax? baseType = null;
        var interfaces = new List<TypeSyntax>();
        if (At(TokenKind.ExtendsKeyword))
        {
            var extends = Advance();
            if (kind == TypeKind.Interface)
            {
                do
                    interfaces.Add(ParseType());
                while (Accept(TokenKind.Comma));
            }
            else
            {
                if (kind is TypeKind.Struct or TypeKind.Enum)
                    Report(kind == TypeKind.Struct ? Catalog.StructExtends(Loc(extends.Span)) : Catalog.EnumExtends(Loc(extends.Span)));
                baseType = ParseType();
            }
        }
        if (At(TokenKind.ImplementsKeyword))
        {
            var implements = Advance();
            if (kind == TypeKind.Interface)
                Report(Catalog.InterfaceImplements(Loc(implements.Span)));
            do
                interfaces.Add(ParseType());
            while (Accept(TokenKind.Comma));
        }

        Expect(TokenKind.OpenBrace, "'{'");
        var variants = kind == TypeKind.Enum ? ParseVariants() : [];
        var members = new Members();
        ParseMembers(kind, name.Text, modifiers.Flags, members);
        Expect(TokenKind.CloseBrace, "'}'");
        return new TypeDecl(LocationFrom(start), modifiers.Flags, kind, name.Text, Loc(name.Span), typeParameters,
            kind == TypeKind.Enum ? null : baseType, interfaces, members.Fields, members.Methods, members.Destructor, variants)
        {
            Visibility = modifiers.Visibility,
        };
    }

    /// <summary>Variants separated by commas, then, if members follow, a ';'.</summary>
    List<VariantDecl> ParseVariants()
    {
        var variants = new List<VariantDecl>();
        while (At(TokenKind.Identifier) || SyntaxFacts.IsVisibilityKeyword(Current.Kind))
        {
            if (SyntaxFacts.IsVisibilityKeyword(Current.Kind))
            {
                int keyword = Current.Span.Start;
                ParseVisibility();
                Report(Catalog.VariantVisibility(Loc(TextSpan.FromBounds(keyword, Previous.Span.End))));
                continue;
            }
            int start = position;
            var name = Advance();
            var fields = At(TokenKind.OpenParen) ? ParseParameterList(allowRefKinds: false) : null;
            var value = Accept(TokenKind.Assign) ? ParseExpression() : null;
            variants.Add(new VariantDecl(LocationFrom(start), name.Text, Loc(name.Span), fields, value));
            if (!Accept(TokenKind.Comma))
                break;
        }
        if (variants.Count == 0)
            Report(Catalog.Expected(Loc(Current.Span), "an enum variant"));
        if (!Accept(TokenKind.Semicolon) && !At(TokenKind.CloseBrace))
            Report(Catalog.Expected(Loc(new TextSpan(Previous.Span.End, 0)), "',' or '}' after an enum variant, or ';' before the enum's methods"));
        return variants;
    }

    const Modifiers InheritanceModifiers = Modifiers.Virtual | Modifiers.Override | Modifiers.Abstract | Modifiers.Final;

    /// <summary>
    /// A type's members, in any order, each with its own visibility or none, which is the file. An
    /// interface's methods take none: they are as visible as the interface.
    /// </summary>
    void ParseMembers(TypeKind kind, string typeName, Modifiers typeModifiers, Members members)
    {
        while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
        {
            int before = position;
            if (SyntaxFacts.IsVisibilityKeyword(Current.Kind) && Peek(1).Kind == TokenKind.OpenBrace)
                ParseVisibilityBlock();
            else
                ParseMember(kind, typeName, typeModifiers, members);
            if (position == before)
                Advance();
        }

        // A block, as the language once had, is reported once and its members parsed as members.
        void ParseVisibilityBlock()
        {
            var keyword = Advance();
            Advance();
            Report(Catalog.VisibilityBlock(Loc(keyword.Span)));
            while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
            {
                int before = position;
                ParseMember(kind, typeName, typeModifiers, members);
                if (position == before)
                    Advance();
            }
            Expect(TokenKind.CloseBrace, "'}'");
        }
    }

    // Reported by the member's declaration, as a variant after the enum's methods.
    bool IsLateVariant(TypeKind kind) =>
        kind == TypeKind.Enum && At(TokenKind.Identifier) && Peek(1).Kind is TokenKind.Comma or TokenKind.Semicolon or TokenKind.CloseBrace;

    bool IsDestructorStart() => At(TokenKind.Tilde) && Peek(1).Kind == TokenKind.Identifier && Peek(2).Kind == TokenKind.OpenParen;

    void ParseMember(TypeKind kind, string typeName, Modifiers typeModifiers, Members members)
    {
        var annotations = ParseAnnotations();
        var (methods, fields) = (members.Methods.Count, members.Fields.Count);
        ParseMemberDeclaration(kind, typeName, typeModifiers, members);
        if (annotations.Count == 0)
            return;
        for (int i = methods; i < members.Methods.Count; i++)
            members.Methods[i] = members.Methods[i] with { Annotations = annotations };
        for (int i = fields; i < members.Fields.Count; i++)
            members.Fields[i] = members.Fields[i] with { Annotations = annotations };
    }

    void ParseMemberDeclaration(TypeKind kind, string typeName, Modifiers typeModifiers, Members members)
    {
        int start = position;
        var modifiers = ParseModifiers();
        DisallowShared(modifiers);
        switch (kind)
        {
            case TypeKind.Struct:
                Disallow(modifiers, InheritanceModifiers, token => Catalog.ModifierOnStructMember(Loc(token.Span), token.Text));
                break;
            case TypeKind.Interface:
                if (modifiers.VisibilitySpan is { } visibility)
                    Report(Catalog.InterfaceMemberVisibility(Loc(visibility)));
                Disallow(modifiers, ~Modifiers.Mut, token => Catalog.ModifierOnInterfaceMember(Loc(token.Span), token.Text));
                modifiers = modifiers with { Visibility = Visibility.File };
                break;
            case TypeKind.Enum:
                Disallow(modifiers, InheritanceModifiers, token => Catalog.ModifierOnEnumMember(Loc(token.Span), token.Text));
                Disallow(modifiers, Modifiers.Mut, token => Catalog.MutEnumMethod(Loc(token.Span)));
                break;
        }

        if (IsLateVariant(kind))
        {
            Report(Catalog.LateVariant(Loc(Current.Span)));
            SkipDeclaration();
            return;
        }

        if (IsDestructorStart())
        {
            ParseDestructor(start, kind, typeName, typeModifiers, modifiers, members);
            return;
        }

        if (At(TokenKind.Identifier) && Current.Text == typeName && Peek(1).Kind == TokenKind.OpenParen)
        {
            Disallow(modifiers, Modifiers.Static | Modifiers.Mut | Modifiers.Extern | InheritanceModifiers, token => Catalog.ModifierOnConstructor(Loc(token.Span), token.Text));
            var constructorName = Advance();
            if (kind == TypeKind.Interface)
                Report(Catalog.InterfaceConstructor(Loc(constructorName.Span)));
            else if (kind == TypeKind.Enum)
                Report(Catalog.EnumConstructor(Loc(constructorName.Span)));
            var constructor = ParseFunctionRest(start, modifiers, null, constructorName);
            RequireBody(constructor);
            members.Methods.Add(constructor);
            return;
        }

        int typeStart = position;
        var (returnRefKind, mayBeNull) = ParseRefKind();
        // A '(' is a grouped function type, or the old spelling, which ParseType reports.
        if (!SyntaxFacts.IsTypeStart(Current.Kind) && !At(TokenKind.OpenParen))
        {
            Report(At(TokenKind.ReservedKeyword) ? Catalog.ReservedKeyword(Loc(Current.Span), Current.Text) : Catalog.Expected(Loc(Current.Span), "a field, method, or constructor"));
            SkipDeclaration();
            return;
        }

        var type = ParseType();
        if (mayBeNull)
            type = NullableReferenceType(typeStart, returnRefKind, type);
        if (At(TokenKind.OperatorKeyword))
        {
            int operatorStart = position;
            Advance();
            if (!Accept(TokenKind.OpenBracket) || !Accept(TokenKind.CloseBracket))
            {
                Report(Catalog.OperatorOverloading(Loc(TextSpan.FromBounds(tokens[operatorStart].Span.Start, Current.Span.End))));
                SkipDeclaration();
                return;
            }
            var operatorName = Accept(TokenKind.Assign) ? SyntaxFacts.IndexerSetterName : SyntaxFacts.IndexerName;
            CheckMethodModifiers(kind, modifiers);
            var indexerName = LocationFrom(operatorStart);
            var indexerParameters = ParseParameterList(allowRefKinds: true);
            var indexerWhere = ParseWhereClauses();
            var indexerBody = Accept(TokenKind.Semicolon) ? null : ParseBlock();
            AddMethod(kind, modifiers, members, new FunctionDecl(LocationFrom(start), modifiers.Flags, type, operatorName,
                indexerName, [], indexerParameters, indexerBody) { ReturnRefKind = returnRefKind, Visibility = modifiers.Visibility, Where = indexerWhere });
            return;
        }
        if (At(TokenKind.ThisKeyword) && Peek(1).Kind == TokenKind.OpenBracket)
        {
            Report(Catalog.ThisIndexer(Loc(Current.Span)));
            SkipDeclaration();
            return;
        }

        var name = Expect(TokenKind.Identifier, "a member name");
        if (At(TokenKind.OpenParen) || At(TokenKind.Less))
        {
            CheckMethodModifiers(kind, modifiers);
            AddMethod(kind, modifiers, members, ParseFunctionRest(start, modifiers, type, name) with { ReturnRefKind = returnRefKind });
            return;
        }
        if (At(TokenKind.OpenBrace))
        {
            Report(Catalog.PropertyBody(Loc(name.Span), name.Text));
            Advance();
            for (int depth = 1; depth > 0 && !At(TokenKind.EndOfFile); Advance())
                depth += At(TokenKind.OpenBrace) ? 1 : At(TokenKind.CloseBrace) ? -1 : 0;
            return;
        }
        if (returnRefKind != RefKind.None)
        {
            Report(Catalog.ReferenceField(Loc(name.Span)));
            if (mayBeNull)
                type = type.Element!;
        }

        if (kind == TypeKind.Interface)
            Report(Catalog.InterfaceField(Loc(name.Span)));
        else if (kind == TypeKind.Enum)
            Report(Catalog.EnumField(Loc(name.Span)));
        if (kind == TypeKind.Struct)
            Disallow(modifiers, Modifiers.Mut, token => Catalog.MutStructField(Loc(token.Span)));
        Disallow(modifiers, InheritanceModifiers | Modifiers.Unsafe | Modifiers.Extern, token => Catalog.ModifierOnField(Loc(token.Span), token.Text));
        int errors = diagnostics.Count;
        while (true)
        {
            var initializer = Accept(TokenKind.Assign) ? ParseExpression() : null;
            members.Fields.Add(new FieldDecl(LocationFrom(start), modifiers.Flags, type, name.Text, Loc(name.Span), initializer) { Visibility = modifiers.Visibility });
            if (!Accept(TokenKind.Comma))
                break;
            name = Expect(TokenKind.Identifier, "a field name");
        }
        FinishStatement(errors);
    }

    void AddMethod(TypeKind kind, ModifierList modifiers, Members members, FunctionDecl method)
    {
        var what = method.Name is SyntaxFacts.IndexerName or SyntaxFacts.IndexerSetterName ? "indexers" : "methods";
        if (kind == TypeKind.Interface)
        {
            if (method.Body is not null)
                Report(Catalog.InterfaceMethodBody(method.NameLocation, what));
        }
        else if ((modifiers.Flags & Modifiers.Abstract) != 0)
        {
            if (method.Body is not null)
                Report(Catalog.AbstractMethodBody(method.NameLocation, what));
        }
        else
        {
            RequireBody(method);
        }
        members.Methods.Add(method);
    }

    void CheckMethodModifiers(TypeKind kind, ModifierList modifiers)
    {
        var flags = modifiers.Flags;
        if (kind == TypeKind.Class)
            Disallow(modifiers, Modifiers.Mut, token => Catalog.MutClassMethod(Loc(token.Span)));
        if ((flags & Modifiers.Static) != 0)
        {
            Disallow(modifiers, Modifiers.Mut | InheritanceModifiers, token => Catalog.ModifierOnStaticMethod(Loc(token.Span), token.Text));
            return;
        }
        bool seenDispatch = false;
        foreach (var (flag, token) in modifiers.Tokens)
        {
            if (flag is not (Modifiers.Virtual or Modifiers.Override or Modifiers.Abstract))
                continue;
            if (seenDispatch)
                Report(Catalog.ConflictingInheritanceModifiers(Loc(token.Span)));
            seenDispatch = true;
        }
        if ((flags & Modifiers.Final) != 0 && (flags & Modifiers.Override) == 0)
            Disallow(modifiers, Modifiers.Final, token => Catalog.FinalWithoutOverride(Loc(token.Span)));
    }

    void ParseDestructor(int start, TypeKind kind, string typeName, Modifiers typeModifiers, ModifierList modifiers, Members members)
    {
        if (modifiers.VisibilitySpan is { } visibility)
            Report(Catalog.DestructorVisibility(Loc(visibility)));
        Disallow(modifiers, ~Modifiers.None, token => Catalog.ModifierOnDestructor(Loc(token.Span), token.Text));
        Advance();
        var name = Advance();
        if (kind == TypeKind.Struct && (typeModifiers & Modifiers.Unique) == 0)
            Report(Catalog.DestructorOnNonUniqueStruct(Loc(name.Span)));
        else if (kind is TypeKind.Enum or TypeKind.Interface)
            Report(Catalog.DestructorOutsideClass(Loc(name.Span), kind == TypeKind.Enum ? "enums" : "interfaces"));
        else if (name.Text != typeName)
            Report(Catalog.DestructorName(Loc(name.Span), typeName));
        else if (members.Destructor is not null)
            Report(Catalog.SecondDestructor(Loc(name.Span), typeName));
        Advance();
        if (!At(TokenKind.CloseParen))
        {
            Report(Catalog.DestructorParameters(Loc(Current.Span)));
            while (!At(TokenKind.CloseParen) && !At(TokenKind.OpenBrace) && !At(TokenKind.EndOfFile))
                Advance();
        }
        Expect(TokenKind.CloseParen, "')'");
        var body = ParseBlock();
        bool allowed = kind == TypeKind.Class || kind == TypeKind.Struct && (typeModifiers & Modifiers.Unique) != 0;
        if (allowed && name.Text == typeName && members.Destructor is null)
            members.Destructor = new DestructorDecl(LocationFrom(start), Loc(name.Span), body);
    }

    FunctionDecl ParseFunctionRest(int start, ModifierList modifiers, TypeSyntax? returnType, Token name)
    {
        // A constructor is recognized only by its name and '(', so it has no type parameters.
        var typeParameters = ParseTypeParameters();
        var parameters = ParseParameterList(allowRefKinds: true);
        var where = ParseWhereClauses();
        var body = Accept(TokenKind.Semicolon) ? null : ParseBlock();
        return new FunctionDecl(LocationFrom(start), modifiers.Flags, returnType, name.Text, Loc(name.Span), typeParameters, parameters, body)
        {
            Visibility = modifiers.Visibility,
            Where = where,
        };
    }

    /// <summary>
    /// A member's <c>where</c> clauses. The word is contextual, so a name stays usable: nothing
    /// else may follow a parameter list but a body, a ';', or this.
    /// </summary>
    List<TypeBoundSyntax> ParseWhereClauses()
    {
        var clauses = new List<TypeBoundSyntax>();
        if (!At(TokenKind.Identifier) || Current.Text != "where")
            return clauses;
        Advance();
        do
        {
            int start = position;
            var parameter = Expect(TokenKind.Identifier, "a type parameter name");
            Expect(TokenKind.ExtendsKeyword, "'extends'");
            var bounds = new List<TypeSyntax>();
            do
                bounds.Add(ParseType());
            while (Accept(TokenKind.Ampersand));
            clauses.Add(new TypeBoundSyntax(LocationFrom(start), parameter.Text, Loc(parameter.Span), bounds));
        } while (Accept(TokenKind.Comma));
        return clauses;
    }

    /// <summary>Function parameters, or with <paramref name="allowRefKinds"/> false, the fields of an enum variant.</summary>
    List<ParameterSyntax> ParseParameterList(bool allowRefKinds)
    {
        Expect(TokenKind.OpenParen, "'('");
        var parameters = new List<ParameterSyntax>();
        // A receiver, `this T name`, may come first (spec functions.extensions).
        if (allowRefKinds && At(TokenKind.ThisKeyword))
        {
            parameters.Add(ParseReceiverParameter());
            if (At(TokenKind.CloseParen))
            {
                Advance();
                return parameters;
            }
            Expect(TokenKind.Comma, "','");
        }
        if (!At(TokenKind.CloseParen))
        {
            do
            {
                int parameterStart = position;
                if (allowRefKinds && At(TokenKind.ThisKeyword))
                {
                    // Consume it as the receiver it was meant to be, without its meaning.
                    var misplaced = ParseReceiverParameter();
                    Report(Catalog.ReceiverNotFirst(misplaced.Location));
                    parameters.Add(misplaced with { IsReceiver = false });
                    continue;
                }
                var annotations = ParseAnnotations();
                if (!allowRefKinds && At(TokenKind.MutKeyword))
                    Report(Catalog.MutVariantField(Loc(Current.Span)));
                else if (!allowRefKinds && (At(TokenKind.RefKeyword) || At(TokenKind.InKeyword)))
                    Report(Catalog.ParameterOnlyModifier(Loc(Current.Span), Current.Text));
                int modeStart = position;
                var (isMutable, refKind, mayBeNull) = ParseBindingMode("parameter");
                // A variant's field is escaping already: its type reports `escaping`.
                var escaping = allowRefKinds ? ParseEscaping() : null;
                var type = ParseType();
                if (mayBeNull && allowRefKinds)
                    type = NullableReferenceType(modeStart, refKind, type);
                var parameterName = Expect(TokenKind.Identifier, allowRefKinds ? "a parameter name" : "a field name");
                var location = LocationFrom(parameterStart);
                Expr? defaultValue = null;
                if (At(TokenKind.Assign))
                {
                    if (!allowRefKinds)
                        Report(Catalog.VariantFieldDefault(Loc(Current.Span)));
                    Advance();
                    defaultValue = ParseExpression();
                }
                parameters.Add(new ParameterSyntax(location, allowRefKinds ? refKind : RefKind.None, type, parameterName.Text)
                {
                    Annotations = annotations,
                    IsMutable = allowRefKinds && isMutable,
                    Escaping = escaping,
                    Default = allowRefKinds ? defaultValue : null,
                });
            } while (Accept(TokenKind.Comma));
        }
        Expect(TokenKind.CloseParen, "')'");
        return parameters;
    }

    /// <summary><c>this [ref|in] T name</c>: a method's receiver, which it takes by value.</summary>
    ParameterSyntax ParseReceiverParameter()
    {
        int start = position;
        Advance();
        var refKind = RefKind.None;
        if (At(TokenKind.RefKeyword))
        {
            Advance();
            refKind = RefKind.Ref;
        }
        else if (At(TokenKind.InKeyword))
        {
            Advance();
            refKind = RefKind.In;
        }
        var type = ParseType();
        var name = Expect(TokenKind.Identifier, "a parameter name");
        return new ParameterSyntax(LocationFrom(start), refKind, type, name.Text) { IsReceiver = true };
    }
}
