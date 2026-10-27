namespace Vpl.Compiler.Semantics;

/// <summary>Facts about how a value of a type is stored: what copying, releasing, or pointing into it involves.</summary>
public static class TypeStorage
{
    extension(VplType type)
    {
        /// <summary>
        /// The type and the types of the values stored inline in a value of it: a nullable value's
        /// value, a fixed array's elements, and the fields of a struct or enum, with type arguments
        /// applied, recursively. Nullable values themselves are left out, as are the fields of an
        /// extern struct, whose storage is the runtime's.
        /// </summary>
        public IEnumerable<VplType> InlineTypes()
        {
            var seen = new HashSet<VplType>();
            var pending = new Stack<VplType>([type]);
            while (pending.TryPop(out var next))
            {
                if (!seen.Add(next))
                    continue;
                if (next.IsNullableValue)
                {
                    pending.Push(next.NonNull);
                    continue;
                }
                yield return next;
                if (next.IsFixedArray)
                    pending.Push(next.ElementType!);
                else if (next.IsStructOrEnum && !next.Symbol!.IsExtern)
                {
                    var map = TypeMap.For(next);
                    foreach (var field in next.Symbol.ValueFields)
                        pending.Push(map.Apply(field.Type));
                }
            }
        }

        /// <summary>
        /// Whether a value of the type may hold strong references to objects, directly or through a
        /// closure, so that releasing it may destroy one and run its destructor.
        /// </summary>
        public bool MayOwnObjects => type.InlineTypes().Any(t => t.IsReference || t.IsTypeParameter || t.IsEscaping);

        /// <summary>
        /// Whether copying or destroying a value of the type runs code: it counts references, strong
        /// or weak, or a string's bytes. An extern struct's storage is the runtime's, so it may.
        /// </summary>
        public bool IsCounted => type.InlineTypes().Any(t => t.IsReference || t.IsTypeParameter || t.IsEscaping || t == VplType.String || t.Symbol is { IsExtern: true });

        /// <summary>
        /// Whether referring to a value of the type where it is, instead of copying it, saves work:
        /// counting references, or copying a struct's fields. A type parameter's value is left alone,
        /// since it may be a struct that a hold refers to in place.
        /// </summary>
        public bool BorrowSavesCopy => !type.NonNull.IsTypeParameter && (type.IsCounted || type.NonNull.IsStruct);

        /// <summary>
        /// Whether storage of type <paramref name="part"/> may lie inside a value of this type, as a
        /// part of it: an element of a fixed array in it if <paramref name="element"/>, or else a
        /// field. Storage of one type is never another's (spec 01-safety.md §1.2, Strict aliasing), so
        /// nothing else can be; a type parameter may be anything. Nullability is left out: a nullable
        /// value lies where its value does.
        /// </summary>
        public bool MayContain(VplType part, bool element)
        {
            var inside = type.InlineTypes().Select(t => t.NonNull).ToList();
            if (inside.Any(t => t.IsTypeParameter) || part.InlineTypes().Any(t => t.NonNull.IsTypeParameter))
                return true;
            return element
                ? inside.Any(t => t.IsFixedArray && t.ElementType!.NonNull == part.NonNull)
                : inside.Skip(1).Contains(part.NonNull);
        }

        /// <summary>
        /// Whether the copies of a value share what a reference or view into it points to: an
        /// object's fields, a string's bytes, or a closure's captures. Such a value is held, not
        /// itself kept in place, while something points into it.
        /// </summary>
        public bool SharesStorage => type.NonNull.IsReference || type == VplType.String || type.NonNull.IsEscapingFunction;

        /// <summary>Whether the copies of a value may share their storage, as a type parameter's may.</summary>
        public bool MayShareStorage => type.SharesStorage || type.IsTypeParameter;

        /// <summary>
        /// Whether a value of the type may refer to an object or a closure, so that converting to it
        /// keeps the object's identity; a function value converted to another type refers to the same closure.
        /// </summary>
        public bool KeepsIdentity => type.NonNull.IsReference || type.NonNull.IsTypeParameter || type.NonNull.IsFunction;
    }
}
