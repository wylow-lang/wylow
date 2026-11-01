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

    TypeDecl ParseTypeDecl(int start, Modifiers modifiers, TypeKind kind)
    {
        Advance();
        var name = Expect(TokenKind.Identifier, kind switch
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
            Error(TextSpan.FromBounds(tokens[colon].Span.Start, Previous.Span.End),
                $"an enum's integer type is given with an annotation, as in '@Repr({type}) enum {name.Text}'");
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
                    Error(extends.Span, $"{(kind == TypeKind.Struct ? "structs" : "enums")} cannot inherit; they may only implement interfaces");
                baseType = ParseType();
            }
        }
        if (At(TokenKind.ImplementsKeyword))
        {
            var implements = Advance();
            if (kind == TypeKind.Interface)
                Error(implements.Span, "interfaces extend other interfaces with 'extends'");
            do
                interfaces.Add(ParseType());
            while (Accept(TokenKind.Comma));
        }

        Expect(TokenKind.OpenBrace, "'{'");
        var variants = kind == TypeKind.Enum ? ParseVariants() : [];
        var members = new Members();
        ParseMembers(kind, name.Text, members);
        Expect(TokenKind.CloseBrace, "'}'");
        return new TypeDecl(LocationFrom(start), modifiers, kind, name.Text, Loc(name.Span), typeParameters,
            kind == TypeKind.Enum ? null : baseType, interfaces, members.Fields, members.Methods, members.Destructor, variants);
    }

    /// <summary>Variants separated by commas, then, if members follow, a ';' as in Java.</summary>
    List<VariantDecl> ParseVariants()
    {
        var variants = new List<VariantDecl>();
        while (At(TokenKind.Identifier))
        {
            int start = position;
            var name = Advance();
            var fields = At(TokenKind.OpenParen) ? ParseParameterList(allowRefKinds: false) : null;
            var value = Accept(TokenKind.Assign) ? ParseExpression() : null;
            variants.Add(new VariantDecl(LocationFrom(start), name.Text, Loc(name.Span), fields, value));
            if (!Accept(TokenKind.Comma))
                break;
        }
        if (variants.Count == 0)
            Error(Current.Span, "expected an enum variant");
        if (!Accept(TokenKind.Semicolon) && !At(TokenKind.CloseBrace))
            Error(new TextSpan(Previous.Span.End, 0), "expected ',' or '}' after an enum variant, or ';' before the enum's methods");
        return variants;
    }

    const Modifiers InheritanceModifiers = Modifiers.Virtual | Modifiers.Override | Modifiers.Abstract | Modifiers.Final;

    /// <summary>
    /// A type's members, in any order, each with its own access modifier or none, which is the
    /// package. An interface's members are all public and take no modifier.
    /// </summary>
    void ParseMembers(TypeKind kind, string typeName, Members members)
    {
        while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
        {
            int before = position;
            if (SyntaxFacts.IsAccessKeyword(Current.Kind) && Peek(1).Kind == TokenKind.OpenBrace)
                ParseAccessBlock();
            else
                ParseMember(kind, typeName, members);
            if (position == before)
                Advance();
        }

        // A block, as the language once had, is reported once and its members parsed as members.
        void ParseAccessBlock()
        {
            var keyword = Advance();
            Advance();
            Error(keyword.Span, $"access is written on each member, as in '{keyword.Text} int x;', not as a '{keyword.Text} {{ ... }}' block");
            while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
            {
                int before = position;
                ParseMember(kind, typeName, members);
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

    void ParseMember(TypeKind kind, string typeName, Members members)
    {
        var annotations = ParseAnnotations();
        var (methods, fields) = (members.Methods.Count, members.Fields.Count);
        ParseMemberDeclaration(kind, typeName, members);
        if (annotations.Count == 0)
            return;
        for (int i = methods; i < members.Methods.Count; i++)
            members.Methods[i] = members.Methods[i] with { Annotations = annotations };
        for (int i = fields; i < members.Fields.Count; i++)
            members.Fields[i] = members.Fields[i] with { Annotations = annotations };
    }

    void ParseMemberDeclaration(TypeKind kind, string typeName, Members members)
    {
        int start = position;
        var modifiers = ParseModifiers();
        switch (kind)
        {
            case TypeKind.Struct:
                Disallow(modifiers, InheritanceModifiers | Modifiers.Protected,
                    token => $"'{token.Text}' is not valid on struct members; structs cannot be inherited");
                break;
            case TypeKind.Interface:
                Disallow(modifiers, ~Modifiers.Mut, token => SyntaxFacts.IsAccessKeyword(token.Kind)
                    ? "an interface's members are all public, so they take no access modifier"
                    : "interface members cannot have modifiers other than 'mut'");
                modifiers = modifiers with { Flags = (modifiers.Flags & ~AccessModifiers) | Modifiers.Public };
                break;
            case TypeKind.Enum:
                Disallow(modifiers, InheritanceModifiers | Modifiers.Protected,
                    token => $"'{token.Text}' is not valid on enum members; enums cannot be inherited");
                Disallow(modifiers, Modifiers.Mut, _ => "enum methods cannot be 'mut', since an enum value cannot be modified");
                break;
        }

        if (IsLateVariant(kind))
        {
            Error(Current.Span, "enum variants must come before the enum's methods");
            SkipDeclaration();
            return;
        }

        if (IsDestructorStart())
        {
            ParseDestructor(start, kind, typeName, modifiers, members);
            return;
        }

        if (At(TokenKind.Identifier) && Current.Text == typeName && Peek(1).Kind == TokenKind.OpenParen)
        {
            Disallow(modifiers, Modifiers.Static | Modifiers.Mut | Modifiers.Extern | InheritanceModifiers,
                token => $"'{token.Text}' is not valid on a constructor");
            var constructorName = Advance();
            if (kind == TypeKind.Interface)
                Error(constructorName.Span, "interfaces cannot have constructors");
            else if (kind == TypeKind.Enum)
                Error(constructorName.Span, "enums cannot have constructors; their values are created from variants");
            var constructor = ParseFunctionRest(start, modifiers.Flags, null, constructorName);
            RequireBody(constructor);
            members.Methods.Add(constructor);
            return;
        }

        int typeStart = position;
        var (returnRefKind, mayBeNull) = ParseRefKind();
        if (!SyntaxFacts.IsTypeStart(Current.Kind))
        {
            Error(Current.Span, At(TokenKind.ReservedKeyword)
                ? $"'{Current.Text}' is not supported yet"
                : "expected a field, method, or constructor");
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
                Error(TextSpan.FromBounds(tokens[operatorStart].Span.Start, Current.Span.End),
                    "only the indexer, 'operator []', can be declared; overloading other operators is not supported yet");
                SkipDeclaration();
                return;
            }
            CheckMethodModifiers(kind, modifiers);
            var indexerName = LocationFrom(operatorStart);
            var indexerParameters = ParseParameterList(allowRefKinds: true);
            var indexerBody = Accept(TokenKind.Semicolon) ? null : ParseBlock();
            AddMethod(kind, modifiers, members, new FunctionDecl(LocationFrom(start), modifiers.Flags, type, SyntaxFacts.IndexerName,
                indexerName, [], indexerParameters, indexerBody) { ReturnRefKind = returnRefKind });
            return;
        }
        if (At(TokenKind.ThisKeyword) && Peek(1).Kind == TokenKind.OpenBracket)
        {
            Error(Current.Span, "an indexer is declared as 'operator []', as in 'ref T operator [](int index)'");
            SkipDeclaration();
            return;
        }

        var name = Expect(TokenKind.Identifier, "a member name");
        if (At(TokenKind.OpenParen) || At(TokenKind.Less))
        {
            CheckMethodModifiers(kind, modifiers);
            AddMethod(kind, modifiers, members, ParseFunctionRest(start, modifiers.Flags, type, name) with { ReturnRefKind = returnRefKind });
            return;
        }
        if (At(TokenKind.OpenBrace))
        {
            Error(name.Span, $"Wylow has no properties; declare a field, or a method '{name.Text}()'");
            Advance();
            for (int depth = 1; depth > 0 && !At(TokenKind.EndOfFile); Advance())
                depth += At(TokenKind.OpenBrace) ? 1 : At(TokenKind.CloseBrace) ? -1 : 0;
            return;
        }
        if (returnRefKind != RefKind.None)
        {
            Error(name.Span, "fields cannot be references; only methods can return 'ref' or 'in'");
            if (mayBeNull)
                type = type.Element!;
        }

        if (kind == TypeKind.Interface)
            Error(name.Span, "interfaces cannot have fields");
        else if (kind == TypeKind.Enum)
            Error(name.Span, "enums cannot have fields; data belongs to variants, as in 'Name(int field)'");
        Disallow(modifiers, Modifiers.Static, _ => "static fields are not supported yet");
        if (kind == TypeKind.Struct)
            Disallow(modifiers, Modifiers.Mut, _ => "struct fields cannot be 'mut'; they can be changed wherever the struct itself can be");
        Disallow(modifiers, InheritanceModifiers | Modifiers.Unsafe | Modifiers.Extern, token => $"'{token.Text}' is not valid on a field");
        int errors = diagnostics.Count;
        while (true)
        {
            var initializer = Accept(TokenKind.Assign) ? ParseExpression() : null;
            members.Fields.Add(new FieldDecl(LocationFrom(start), modifiers.Flags, type, name.Text, Loc(name.Span), initializer));
            if (!Accept(TokenKind.Comma))
                break;
            name = Expect(TokenKind.Identifier, "a field name");
        }
        FinishStatement(errors);
    }

    void AddMethod(TypeKind kind, ModifierList modifiers, Members members, FunctionDecl method)
    {
        var what = method.Name == SyntaxFacts.IndexerName ? "indexers" : "methods";
        if (kind == TypeKind.Interface)
        {
            if (method.Body is not null)
                Error(method.NameLocation.Span, $"interface {what} cannot have a body");
        }
        else if ((modifiers.Flags & Modifiers.Abstract) != 0)
        {
            if (method.Body is not null)
                Error(method.NameLocation.Span, $"abstract {what} cannot have a body");
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
            Disallow(modifiers, Modifiers.Mut, _ => "'mut' is only valid on struct and interface methods; a class method may change its object's 'mut' fields");
        if ((flags & Modifiers.Static) != 0)
        {
            Disallow(modifiers, Modifiers.Mut | InheritanceModifiers, token => $"a static method cannot be '{token.Text}'");
            return;
        }
        if ((flags & Modifiers.Private) != 0)
            Disallow(modifiers, InheritanceModifiers, token => $"a private method cannot be '{token.Text}'");

        bool seenDispatch = false;
        foreach (var (flag, token) in modifiers.Tokens)
        {
            if (flag is not (Modifiers.Virtual or Modifiers.Override or Modifiers.Abstract))
                continue;
            if (seenDispatch)
                Error(token.Span, "only one of 'virtual', 'override', and 'abstract' is allowed");
            seenDispatch = true;
        }
        if ((flags & Modifiers.Final) != 0 && (flags & Modifiers.Override) == 0)
            Disallow(modifiers, Modifiers.Final, _ => "'final' can only be used on an 'override' method");
    }

    void ParseDestructor(int start, TypeKind kind, string typeName, ModifierList modifiers, Members members)
    {
        Disallow(modifiers, ~Modifiers.None, token => $"'{token.Text}' is not valid on a destructor");
        Advance();
        var name = Advance();
        if (kind != TypeKind.Class)
            Error(name.Span, kind switch
            {
                TypeKind.Struct => "structs cannot have destructors",
                TypeKind.Enum => "enums cannot have destructors",
                _ => "interfaces cannot have destructors",
            });
        else if (name.Text != typeName)
            Error(name.Span, $"a destructor must be named after its class, as in '~{typeName}()'");
        else if (members.Destructor is not null)
            Error(name.Span, "a class can have only one destructor");
        Advance();
        if (!At(TokenKind.CloseParen))
        {
            Error(Current.Span, "destructors cannot have parameters");
            while (!At(TokenKind.CloseParen) && !At(TokenKind.OpenBrace) && !At(TokenKind.EndOfFile))
                Advance();
        }
        Expect(TokenKind.CloseParen, "')'");
        var body = ParseBlock();
        if (kind == TypeKind.Class && name.Text == typeName && members.Destructor is null)
            members.Destructor = new DestructorDecl(LocationFrom(start), Loc(name.Span), body);
    }

    FunctionDecl ParseFunctionRest(int start, Modifiers modifiers, TypeSyntax? returnType, Token name)
    {
        // A constructor is recognized only by its name and '(', so it has no type parameters.
        var typeParameters = ParseTypeParameters();
        var parameters = ParseParameterList(allowRefKinds: true);
        var body = Accept(TokenKind.Semicolon) ? null : ParseBlock();
        return new FunctionDecl(LocationFrom(start), modifiers, returnType, name.Text, Loc(name.Span), typeParameters, parameters, body);
    }

    /// <summary>Function parameters, or with <paramref name="allowRefKinds"/> false, the fields of an enum variant.</summary>
    List<ParameterSyntax> ParseParameterList(bool allowRefKinds)
    {
        Expect(TokenKind.OpenParen, "'('");
        var parameters = new List<ParameterSyntax>();
        if (!At(TokenKind.CloseParen))
        {
            do
            {
                int parameterStart = position;
                var annotations = ParseAnnotations();
                if (!allowRefKinds && At(TokenKind.MutKeyword))
                    Error(Current.Span, "a variant's fields cannot be 'mut'; an enum value is only ever replaced as a whole");
                else if (!allowRefKinds && (At(TokenKind.RefKeyword) || At(TokenKind.InKeyword)))
                    Error(Current.Span, $"'{Current.Text}' is only valid on parameters");
                int modeStart = position;
                var (isMutable, refKind, mayBeNull) = ParseBindingMode("parameter");
                var type = ParseType();
                if (mayBeNull && allowRefKinds)
                    type = NullableReferenceType(modeStart, refKind, type);
                var parameterName = Expect(TokenKind.Identifier, allowRefKinds ? "a parameter name" : "a field name");
                var location = LocationFrom(parameterStart);
                Expr? defaultValue = null;
                if (At(TokenKind.Assign))
                {
                    if (!allowRefKinds)
                        Error(Current.Span, "a variant's fields cannot have default values");
                    Advance();
                    defaultValue = ParseExpression();
                }
                parameters.Add(new ParameterSyntax(location, allowRefKinds ? refKind : RefKind.None, type, parameterName.Text)
                {
                    Annotations = annotations,
                    IsMutable = allowRefKinds && isMutable,
                    Default = allowRefKinds ? defaultValue : null,
                });
            } while (Accept(TokenKind.Comma));
        }
        Expect(TokenKind.CloseParen, "')'");
        return parameters;
    }
}
