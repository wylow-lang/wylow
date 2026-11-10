using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

/// <summary>
/// Extension methods (spec <c>functions.extensions</c>): a call <c>x.name(...)</c> whose receiver's
/// type has no member <c>name</c> calls a top-level function that takes <c>x</c> as its <c>this</c>
/// parameter. The call binds as an ordinary call of that function, with the receiver as its first
/// argument, so nothing after the binder knows it was written as a method call.
/// </summary>
public sealed partial class Binder
{
    /// <summary>
    /// Binds <c>receiver.name(...)</c> as an extension call, or returns null if no extension named
    /// <paramref name="name"/> accepts the receiver. A receiver that may be null needs a narrowing,
    /// unless an extension's <c>this</c> parameter is itself nullable.
    /// </summary>
    BoundExpression? BindExtensionCall(
        Location location,
        BoundExpression receiver,
        string name,
        Location nameLocation,
        IReadOnlyList<WylType>? typeArguments,
        PendingArguments pending)
    {
        var extensions = ExtensionCandidates(receiver.Type, name, nameLocation);
        // Nothing refers into a nullable value, so a narrowed receiver is never a 'this ref' one.
        if (extensions.Count == 0 && receiver.Type.MayBeNull)
        {
            extensions = ExtensionCandidates(receiver.Type.Narrowed, name, nameLocation, refers: false);
            if (extensions.Count > 0)
                receiver = RequireNonNull(receiver);
        }
        if (extensions.Count == 0)
        {
            if (receiver.Type.MayBeNull && !receiver.Type.IsNullableReference && ExtensionCandidates(receiver.Type.Narrowed, name, nameLocation) is [var refers, ..])
                return ErrorExpression(Catalog.RefReceiverNullable(nameLocation, refers.Symbol.Name, TextOf(receiver.Location), receiver.Type.ToString()));
            return null;
        }
        var bound = pending.Bind(extensions, receivers: 1);
        if (receiver.Type == WylType.Error || AnyError(bound))
            return new BoundErrorExpression(location);
        List<Argument> arguments = [new Argument(receiver, false, receiver.Location) { IsReceiver = true }, .. bound];
        var method = Resolve(location, $"overload of '{name}'", extensions, typeArguments, arguments);
        if (method is null)
            return new BoundErrorExpression(location);
        CheckVisible(nameLocation, method.Symbol.Reach, $"function '{name}'");
        return MakeCall(location, method, null, arguments);
    }

    /// <summary>
    /// The extensions named <paramref name="name"/> that a call on a receiver of type
    /// <paramref name="receiver"/> chooses among: those of the first of <see cref="Places"/> that has
    /// one accepting the receiver. A place with none does not hide the places after it. The
    /// <c>*</c> imports are one place, ambiguous where used if two of them give accepting extensions.
    /// </summary>
    List<Candidate> ExtensionCandidates(WylType receiver, string name, Location use, bool refers = true)
    {
        var stars = new List<(FunctionSymbol Function, Module From)>();
        foreach (var (entry, star) in Places(name, use.Source))
        {
            var accepting = (entry.Functions ?? []).Where(f => f.IsExtension && AcceptsReceiver(f, receiver, refers)).ToList();
            if (star is not null)
            {
                stars.AddRange(accepting.Where(f => !stars.Any(s => s.Function == f)).Select(f => (f, star)));
                continue;
            }
            if (stars.Count > 0)
            {
                var from = stars[0].From;
                if (stars.FirstOrDefault(s => s.From != from) is { Function: not null } other && reportedAmbiguities.Add((use, name)))
                    Error(Catalog.AmbiguousStarImport(use, name, from, other.From));
                return [.. stars.Where(s => s.From == from).Select(s => new Candidate(s.Function, null))];
            }
            if (accepting.Count > 0)
                return [.. accepting.Select(f => new Candidate(f, null))];
        }
        return [];
    }

    /// <summary>Where the nearest extension named <paramref name="name"/> is declared, to point at when it does not accept a receiver.</summary>
    Location? NearestExtension(string name, Location use) =>
        Places(name, use.Source).SelectMany(p => p.Entry.Functions ?? []).FirstOrDefault(f => f.IsExtension)?.Declaration?.NameLocation;

    /// <summary>
    /// Whether an extension accepts a receiver of type <paramref name="receiver"/>: the receiver
    /// converts to the <c>this</c> parameter, and the type arguments that the receiver alone
    /// determines satisfy their bounds. A bound that mentions a type parameter the receiver leaves
    /// open is checked once the call's other arguments give it a type, unless the type argument has
    /// no instance of the bound's interface at all, which no type can repair. A <c>this ref</c> parameter
    /// takes only a receiver of exactly its type, or a <c>ref?</c> local that refers to one, as a
    /// <c>ref</c> argument does; with <paramref name="refers"/> false, it takes none.
    /// </summary>
    bool AcceptsReceiver(FunctionSymbol extension, WylType receiver, bool refers = true)
    {
        var map = extension.IsGeneric ? Inference.Determined(this, extension, receiver) : TypeMap.Empty;
        var parameter = extension.Parameters[0];
        var target = map.Apply(parameter.Type);
        bool Open(WylType type) => type.ContainedTypeParameters.Any(extension.TypeParameters.Contains);
        if (Open(target))
            return false;
        bool converts = parameter.RefKind == RefKind.Ref
            ? refers && (receiver == target || (receiver is NullableReferenceType reference && reference.Referenced == target))
            : ReceiverConverts(receiver, target);
        return converts && extension.TypeParameters.All(p =>
        {
            var argument = map.Apply(p.Type);
            return Open(argument) || !UnsatisfiedBounds(p, argument, map, currentFunction)
                .Any(u => u.Interface is null || !Open(u.Interface) || !HasInstanceOf(argument, u.Interface));
        });
    }

    /// <summary>Whether the type is, or implements, some instance of the generic interface's declaration.</summary>
    bool HasInstanceOf(WylType type, WylType generic) =>
        ((IEnumerable<WylType>)[type, .. Conversions.Supertypes(stdlib.MemberHost(type), currentFunction)]).Any(t => t.Symbol == generic.Symbol);

    /// <summary>
    /// Whether a receiver of type <paramref name="from"/> converts to a <c>this</c> parameter of type
    /// <paramref name="to"/>: by identity, by an upcast, or by a span conversion, and by no other
    /// conversion (spec <c>functions.extensions</c>).
    /// </summary>
    bool ReceiverConverts(WylType from, WylType to) =>
        from == to
        || from == WylType.Error
        || (from.IsReference && to.IsReference && (to.IsNullable || !from.IsNullable) && Conversions.IsSubtype(from.NonNull, to.NonNull, currentFunction))
        || (stdlib.IsSpan(to) && !from.MayBeNull && ViewedElement(from, to.Symbol == stdlib.ReadOnlySpan) is { } element && element == to.TypeArguments[0]);
}
