using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

public sealed partial class Binder(DiagnosticBag diagnostics)
{
    readonly Dictionary<string, List<FunctionSymbol>> functions = new();
    readonly Dictionary<string, TypeSymbol> types = new();

    // Bounds may mention types whose own bounds are not bound yet, so satisfaction is checked last.
    readonly List<(VplType Argument, TypeParameterSymbol Parameter, TypeMap Map, Location Location)> boundChecks = [];

    readonly StdlibSymbols stdlib = new();

    FunctionSymbol? currentFunction;
    TypeSymbol? currentType;
    IReadOnlyList<TypeParameterSymbol> typeParameters = [];
    bool hasThis;
    NoThisContext noThisContext;
    Scope? scope;
    int loopDepth;
    int unsafeDepth;

    bool InUnsafe => unsafeDepth > 0 || currentFunction?.IsUnsafe == true;

    sealed class Scope(Scope? parent)
    {
        readonly Dictionary<string, LocalSymbol> locals = new();

        public Scope? Parent { get; } = parent;

        public LocalSymbol? Lookup(string name)
        {
            for (var s = this; s is not null; s = s.Parent)
            {
                if (s.locals.TryGetValue(name, out var local))
                    return local;
            }
            return null;
        }

        public void Add(LocalSymbol local) => locals[local.Name] = local;
    }

    sealed record Argument(BoundExpression Expression, bool IsRef, Location Location);

    /// <summary>A method or constructor that may be called, as a member of <see cref="Owner"/>.</summary>
    sealed record Candidate(FunctionSymbol Symbol, VplType? Owner);

    enum PlaceKind { Temporary, Mutable, ReadOnly }

    enum NoThisContext { Static, FieldInitializer, SuperArguments }

    public BoundProgram BindProgram(CompilationUnit unit)
    {
        foreach (var builtin in Builtins.All)
            AddFunction(builtin);

        var typeList = new List<TypeSymbol>();
        foreach (var decl in unit.Declarations.OfType<TypeDecl>())
        {
            if (DeclareType(decl) is not { } symbol)
                continue;
            typeList.Add(symbol);
            if (Stdlib.Contains(decl.Location.Source))
                stdlib.Register(symbol);
        }
        foreach (var s in typeList)
        {
            typeParameters = s.TypeParameters;
            BindBounds(s.Declaration.TypeParameters, s.TypeParameters);
            BindSupertypes(s);
            typeParameters = [];
        }
        CheckInheritanceCycles(typeList);

        var functionList = unit.Declarations.OfType<FunctionDecl>().Select(DeclareFunction).ToList();
        foreach (var s in typeList)
            DeclareMembers(s);
        foreach (var s in typeList)
            CheckMembers(s);
        CheckStructCycles([.. typeList.Where(s => s.Kind is TypeKind.Struct or TypeKind.Enum)]);
        foreach (var s in typeList)
            BindFieldInitializers(s);

        var bodies = new List<BoundFunction>();
        foreach (var s in typeList)
        {
            foreach (var member in s.Members.Where(m => m.Body is not null))
                bodies.Add(new BoundFunction(member, BindBody(member)));
            if (s.Kind == TypeKind.Class && s.Constructors.Count == 0)
                bodies.Add(BindImplicitConstructor(s));
        }
        foreach (var function in functionList.Where(f => f.Body is not null))
            bodies.Add(new BoundFunction(function, BindBody(function)));

        CheckBounds();
        return new BoundProgram(unit.Source, typeList, bodies, FindMain(unit.Source), stdlib);
    }

    void Error(Location location, string message) => diagnostics.Error(location, message);

    static string TextOf(Location location) => location.Source.Text.Substring(location.Span.Start, location.Span.Length);

    static string KindName(TypeKind kind) => kind switch
    {
        TypeKind.Struct => "struct",
        TypeKind.Class => "class",
        TypeKind.Enum => "enum",
        _ => "interface",
    };
}
