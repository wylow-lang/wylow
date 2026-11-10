using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder(DiagnosticBag diagnostics)
{
    // Bounds may mention types whose own bounds are not bound yet, so satisfaction is checked last.
    readonly List<(WylType Argument, TypeParameterSymbol Parameter, TypeMap Map, Location Location, FunctionSymbol? Function)> boundChecks = [];

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

    /// <summary>A call argument, with the name it was passed by, if any.</summary>
    sealed record Argument(BoundExpression Expression, bool IsRef, Location Location, string? Name = null, Location NameLocation = default)
    {
        /// <summary>
        /// The receiver of an extension call, passed to the <c>this</c> parameter: it converts only as a
        /// receiver does, and a <c>this ref</c> parameter takes it without a written <c>ref</c>.
        /// </summary>
        public bool IsReceiver { get; init; }
    }

    /// <summary>A method or constructor that may be called, as a member of <see cref="Owner"/>.</summary>
    sealed record Candidate(FunctionSymbol Symbol, WylType? Owner);

    enum PlaceKind { Temporary, Mutable, ReadOnly }


    /// <summary>
    /// Binds a package with the standard library, whose units are among <paramref name="units"/>:
    /// every file's top-level declarations first, so any may name any other (spec programs.packages),
    /// then their signatures and bodies.
    /// </summary>
    public BoundProgram BindProgram(Package package, IReadOnlyList<CompilationUnit> units)
    {
        foreach (var (module, builtin) in Builtins.All)
            AddBuiltin(module, builtin);
        RegisterSuppressions(units);
        RegisterImports(units);
        var declarations = units.SelectMany(u => u.Declarations).ToList();

        var typeList = new List<TypeSymbol>();
        foreach (var decl in declarations.OfType<TypeDecl>())
        {
            if (DeclareType(decl) is not { } symbol)
                continue;
            typeList.Add(symbol);
            if (decl.Location.Source.Module.Package.IsStd)
                stdlib.Register(symbol);
        }
        NameAmbiguousTypes(typeList);
        foreach (var s in typeList)
        {
            typeParameters = s.TypeParameters;
            BindBounds(s.Declaration.TypeParameters, s.TypeParameters);
            BindSupertypes(s);
            typeParameters = [];
        }
        CheckInheritanceCycles(typeList);
        PropagateSharedClasses(typeList);
        CheckBorrowableValueTypes();

        var functionList = declarations.OfType<FunctionDecl>().Select(DeclareFunction).ToList();
        foreach (var function in functionList.Where(f => f.Declaration!.Location.Source.Module.Package.IsStd))
            stdlib.Register(function);
        CheckImports();
        foreach (var s in typeList)
            DeclareMembers(s);
        foreach (var s in typeList)
            CheckMembers(s);
        foreach (var s in typeList)
            CheckIndexerSetters(s);
        var cyclic = CheckStructCycles([.. typeList.Where(s => s.Kind is TypeKind.Struct or TypeKind.Enum)]);
        foreach (var s in typeList)
            CheckUniqueFields(s, cyclic);
        // A struct that contains itself, or a field that can't be stored, is already an error.
        foreach (var s in typeList.Where(s => s is { Kind: TypeKind.Struct, IsExtern: false, Constructors.Count: 0, Fields.Count: > 0 }
                     && !cyclic.Contains(s) && s.Fields.All(f => !f.Type.IsRefStruct)))
            DeclareMemberwiseConstructor(s);
        foreach (var s in typeList)
            CheckSignatures(s);
        foreach (var function in functionList)
            CheckFunctionSignature(function, $"function '{function.Name}'");
        foreach (var s in typeList)
            BindFieldInitializers(s);
        BindDefaults([.. functionList, .. typeList.SelectMany(s => s.Members)]);

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
        CheckConstructorCycles(bodies);
        FindReturnedHolds([.. functionList, .. typeList.SelectMany(s => s.Members)]);

        CheckBounds();
        return new BoundProgram(package, [.. units.Select(u => u.Source).Where(s => s.Module.Package == package)], typeList, bodies, functionList, FindMain(package), stdlib);
    }

    void Error(Diagnostic diagnostic) => diagnostics.Add(diagnostic);

    static string TextOf(Location location) => location.Source.Text.Substring(location.Span.Start, location.Span.Length);
}
