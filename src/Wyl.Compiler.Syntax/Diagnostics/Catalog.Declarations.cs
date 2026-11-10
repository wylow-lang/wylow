using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Diagnostics;

// Chapters 6 to 10: variables, structs, classes, enums, and interfaces.
public static partial class Catalog
{
    [Error(93, "variables.statics")]
    public static Diagnostic TopLevelVariable(Location at, string name) =>
        Make(at, "variables cannot be declared at the top level").Help($"declare '{name}' inside a function or type");

    [Error(463, "variables.statics")]
    public static Diagnostic StaticInGenericType(Location at, string type) =>
        Make(at, $"generic type '{type}' cannot declare a static field").Help("declare the static in a type that has no type parameters");

    [Error(464, "variables.statics")]
    public static Diagnostic StaticTypeNotAllowed(Location at, string name, string property, string detail, Location? detailAt) =>
        Make(at, $"the type of static '{name}' is not {property}").Note(detailAt, detail);

    [Error(465, "variables.statics")]
    public static Diagnostic StaticAssignment(Location at, string name) =>
        Make(at, $"cannot assign to static '{name}'").Note("every static is read-only");

    [Error(466, "variables.statics")]
    public static Diagnostic MutStaticField(Location at) =>
        Make(at, "static fields cannot be 'mut'").Note("every static is read-only");

    [Error(467, "expressions.members")]
    public static Diagnostic StaticFieldThroughValue(Location at, string name, string type) =>
        Make(at, $"static field '{name}' cannot be read through a value").Help($"read it through its type, as in '{type}.{name}'");

    [Error(468, "variables.statics")]
    public static Diagnostic StaticWithoutDefault(Location at, string name, string type) =>
        Make(at, $"static '{name}' has no initializer and '{type}' has no default value").Help("give it an initializer");

    [Error(469, "concurrency.shared-classes")]
    public static Diagnostic SharedOutsideClass(Location at) =>
        Make(at, "'shared' can only be used on a class");

    [Error(470, "concurrency.shared-classes")]
    public static Diagnostic MutSharedClassField(Location at, string field, string type) =>
        Make(at, $"field '{field}' of shared class '{type}' cannot be 'mut'").Note("a shared class's fields are read by several threads");

    [Error(471, "concurrency.shared-classes")]
    public static Diagnostic SharedClassFieldTypeNotAllowed(Location at, string field, string type, string property, string detail, Location? detailAt) =>
        Make(at, $"the type of field '{field}' of shared class '{type}' is not {property}").Note(detailAt, detail);

    [Error(478, "concurrency.isolated")]
    public static Diagnostic IsolatedNotSendable(Location at, string type, string? field, Location fieldAt, string? fieldType) =>
        field is null
            ? Make(at, $"'{type}' is not 'sendable'")
            : Make(at, $"'{type}' is not 'sendable'").Note(fieldAt, $"field '{field}' has type '{fieldType}', which is not sendable");

    [Error(479, "concurrency.atomic")]
    public static Diagnostic AtomicOfNonInteger(Location at, string argument) =>
        argument == "bool"
            ? Make(at, $"'Atomic' type argument: expected an integer type, found '{argument}'").Help("write 'AtomicBool'")
            : Make(at, $"'Atomic' type argument: expected an integer type, found '{argument}'");

    [Error(89, "variables.references")]
    public static Diagnostic MutReference(Location at, string keyword, string what) =>
        Make(at, $"'{keyword}' {what} cannot be 'mut'").Note("it refers to the same place for its whole scope");

    [Error(404, "memory.references")]
    public static Diagnostic ReferenceField(Location at) =>
        Make(at, "fields cannot be references").Note("only methods can return 'ref' or 'in'");

    [Error(95, "structs.declarations")]
    public static Diagnostic StructExtends(Location at) =>
        Make(at, "structs cannot extend a type").Help("use 'implements' for an interface");

    [Error(96, "structs.declarations")]
    public static Diagnostic ModifierOnStructMember(Location at, string modifier) =>
        Make(at, $"'{modifier}' cannot be used on a struct member").Note("structs cannot be inherited");

    [Error(98, "structs.fields")]
    public static Diagnostic MutStructField(Location at) =>
        Make(at, "struct fields cannot be 'mut'").Note("a struct's fields can be changed wherever the struct can");

    [Error(447, "structs.methods.by-value")]
    public static Diagnostic ReceiverOutsideValueMethod(Location at) =>
        Make(at, "'this' parameter is only for a method of a struct or enum, or an extension method").Note("a class, an interface, a constructor, and an indexer have no by-value receiver").Help("declare a top-level function to write an extension method");

    [Error(448, "structs.methods.by-value")]
    public static Diagnostic ReceiverMode(Location at) =>
        Make(at, "'this' parameter cannot be 'ref' or 'in'").Note("a method's receiver is passed by value").Help("declare the method 'mut' to change the receiver");

    [Error(449, "structs.methods.by-value")]
    public static Diagnostic ReceiverType(Location at, string expected, string found) =>
        Make(at, $"receiver type: expected '{expected}', found '{found}'").Note("a method's receiver has the type that declares it");

    [Error(450, "structs.methods.by-value")]
    public static Diagnostic ReceiverModifier(Location at, string modifiers) =>
        Make(at, $"a method with a by-value receiver cannot be {modifiers}");

    [Error(451, "structs.methods.by-value")]
    public static Diagnostic ReceiverReferenceReturn(Location at, string type) =>
        Make(at, "a method with a by-value receiver cannot return a reference or a view").Note($"it returns '{type}'");

    [Error(452, "interfaces.implementing")]
    public static Diagnostic ReceiverImplementation(Location at, string method) =>
        Make(at, $"{method} cannot implement an interface method with a by-value receiver").Note("an interface call uses the value in place");

    [Error(103, "structs.methods.mut")]
    public static Diagnostic MutClassMethod(Location at) =>
        Make(at, "'mut' cannot be used on a class method")
            .Note("a class method may change its object's 'mut' fields; 'mut' marks struct and interface methods");

    [Error(108, "classes.fields")]
    public static Diagnostic PropertyBody(Location at, string name) =>
        Make(at, $"'{name}' cannot have a property body").Help($"declare a field, or a method '{name}()'");

    [Error(109, "classes.fields")]
    public static Diagnostic ModifierOnField(Location at, string modifier) =>
        Make(at, $"'{modifier}' cannot be used on a field");

    [Error(128, "classes.abstract")]
    public static Diagnostic AbstractFinalClass(Location at) =>
        Make(at, "class cannot be both 'abstract' and 'final'");

    [Error(129, "classes.abstract")]
    public static Diagnostic AbstractMethodBody(Location at, string what) =>
        Make(at, $"abstract {what} cannot have a body");

    [Error(118, "classes.overriding")]
    public static Diagnostic ConflictingInheritanceModifiers(Location at) =>
        Make(at, "only one of 'virtual', 'override', and 'abstract' can be used");

    [Error(119, "classes.overriding")]
    public static Diagnostic FinalWithoutOverride(Location at) =>
        Make(at, "'final' can only be used on an 'override' method");

    [Error(134, "classes.constructors")]
    public static Diagnostic ModifierOnConstructor(Location at, string modifier) =>
        Make(at, $"'{modifier}' cannot be used on a constructor");

    [Error(144, "classes.destructors")]
    public static Diagnostic ModifierOnDestructor(Location at, string modifier) =>
        Make(at, $"'{modifier}' cannot be used on a destructor");

    [Error(145, "classes.destructors")]
    public static Diagnostic DestructorVisibility(Location at) =>
        Make(at, "destructor cannot have a visibility").Note("only the runtime calls a destructor");

    [Error(146, "classes.destructors")]
    public static Diagnostic DestructorOutsideClass(Location at, string kinds) =>
        Make(at, $"{kinds} cannot have destructors");

    [Error(147, "classes.destructors")]
    public static Diagnostic DestructorName(Location at, string type) =>
        Make(at, "destructor is not named after its type").Help($"name it '~{type}()'");

    [Error(148, "classes.destructors")]
    public static Diagnostic SecondDestructor(Location at, string type) =>
        Make(at, $"'{type}' already has a destructor");

    [Error(149, "classes.destructors")]
    public static Diagnostic DestructorParameters(Location at) =>
        Make(at, "destructors cannot have parameters");

    [Error(458, "structs.unique.destructors")]
    public static Diagnostic DestructorOnNonUniqueStruct(Location at) =>
        Make(at, "only a 'unique' struct can have a destructor").Help("declare the struct 'unique'");

    [Error(459, "structs.unique.destructors")]
    public static Diagnostic DiscardNotParameter(Location at, string name) =>
        Make(at, $"cannot discard '{name}'").Note("'discard' applies to a by-value parameter of a unique struct with a destructor");

    [Error(460, "structs.unique.destructors")]
    public static Diagnostic DiscardOutsideTypeFile(Location at, string name, string type, string file) =>
        Make(at, $"cannot discard '{name}' outside the file of '{type}'").Note($"only a function declared in '{file}' may discard it");

    [Error(150, "enums.declarations")]
    public static Diagnostic EnumExtends(Location at) =>
        Make(at, "enums cannot extend a type").Help("use 'implements' for an interface");

    [Error(151, "enums.declarations")]
    public static Diagnostic LateVariant(Location at) =>
        Make(at, "enum variants must come before the enum's methods");

    [Error(152, "enums.declarations")]
    public static Diagnostic EnumConstructor(Location at) =>
        Make(at, "enums cannot have constructors").Note("an enum's values are made from its variants");

    [Error(153, "enums.declarations")]
    public static Diagnostic EnumField(Location at) =>
        Make(at, "enums cannot have fields").Help("give the data to a variant, as in 'Name(int field)'");

    [Error(162, "enums.methods")]
    public static Diagnostic ModifierOnEnumMember(Location at, string modifier) =>
        Make(at, $"'{modifier}' cannot be used on an enum member").Note("enums cannot be inherited");

    [Error(163, "enums.methods")]
    public static Diagnostic MutEnumMethod(Location at) =>
        Make(at, "enum methods cannot be 'mut'").Note("an enum value cannot be modified, only replaced");

    [Error(154, "enums.variants")]
    public static Diagnostic VariantVisibility(Location at) =>
        Make(at, "enum variants cannot have a visibility").Note("a variant is as visible as its enum");

    [Error(155, "enums.variants")]
    public static Diagnostic MutVariantField(Location at) =>
        Make(at, "variant fields cannot be 'mut'").Note("an enum value is only replaced as a whole");

    [Error(156, "enums.variants")]
    public static Diagnostic VariantFieldDefault(Location at) =>
        Make(at, "variant fields cannot have default values");

    [Error(164, "enums.repr")]
    public static Diagnostic EnumIntegerTypeSyntax(Location at, string type, string name) =>
        Make(at, "':' cannot give an enum's integer type").Help($"write '@Repr({type}) enum {name}'");

    [Error(174, "interfaces.declarations")]
    public static Diagnostic InterfaceImplements(Location at) =>
        Make(at, "interfaces cannot use 'implements'").Help("use 'extends' for another interface");

    [Error(175, "interfaces.declarations")]
    public static Diagnostic InterfaceMemberVisibility(Location at) =>
        Make(at, "interface methods cannot have a visibility").Note("an interface method is as visible as its interface");

    [Error(176, "interfaces.declarations")]
    public static Diagnostic ModifierOnInterfaceMember(Location at, string modifier) =>
        Make(at, $"'{modifier}' cannot be used on an interface method");

    [Error(177, "interfaces.declarations")]
    public static Diagnostic InterfaceConstructor(Location at) =>
        Make(at, "interfaces cannot have constructors");

    [Error(178, "interfaces.declarations")]
    public static Diagnostic InterfaceField(Location at) =>
        Make(at, "interfaces cannot have fields");

    [Error(179, "interfaces.declarations")]
    public static Diagnostic InterfaceMethodBody(Location at, string what) =>
        Make(at, $"interface {what} cannot have a body");

    [Error(157, "enums.variants")]
    public static Diagnostic VariantNeedsData(Location at, string variant) =>
        Make(at, $"variant '{variant}' carries data").Help($"create it with '{variant}(...)'");

    [Error(158, "enums.variants")]
    public static Diagnostic VariantWithoutData(Location at, string variant) =>
        Make(at, $"variant '{variant}' carries no data").Help("write it without parentheses");

    [Error(159, "enums.variants")]
    public static Diagnostic VariantValueCount(Location at, string variant, int expected, int found) =>
        Make(at, $"values of '{variant}': expected {expected}, found {found}");

    [Error(160, "enums.variants")]
    public static Diagnostic VariantTypeArgument(Location at, string parameter, string type, string variant) =>
        Make(at, $"cannot infer type argument '{parameter}' of '{type}' from '{variant}'")
            .Help($"write '{type}<...>.{variant}', or use it where a '{type}' is expected");

    [Error(180, "interfaces.declarations")]
    public static Diagnostic InterfaceInstance(Location at, string type) =>
        Make(at, $"cannot create an instance of interface '{type}'");

    [Error(130, "classes.abstract")]
    public static Diagnostic AbstractInstance(Location at, string type) =>
        Make(at, $"cannot create an instance of abstract class '{type}'");

    [Error(131, "classes.abstract")]
    public static Diagnostic AbstractSuperCall(Location at, string method) =>
        Make(at, $"cannot call abstract method '{method}' through 'super'");

    [Error(135, "classes.constructors")]
    public static Diagnostic SuperCallNotFirst(Location at) =>
        Make(at, "'super(...)' must be the first statement of a constructor");

    [Error(136, "classes.constructors")]
    public static Diagnostic SuperCallOutsideConstructor(Location at) =>
        Make(at, "'super(...)' can only be used in a class constructor");

    [Error(137, "classes.constructors")]
    public static Diagnostic ThisCallNotFirst(Location at) =>
        Make(at, "'this(...)' must be the first statement of a constructor");

    [Error(138, "classes.constructors")]
    public static Diagnostic ThisCallOutsideConstructor(Location at) =>
        Make(at, "'this(...)' can only be used in a constructor");

    [Error(111, "classes.inheritance")]
    public static Diagnostic SuperWithoutBase(Location at, string type) =>
        Make(at, $"'super' cannot be used in '{type}'").Note($"'{type}' has no base class");

    [Error(112, "classes.inheritance")]
    public static Diagnostic SuperOutsideInstance(Location at) =>
        Make(at, "'super' can only be used in instance methods and constructors of a class");

    [Error(104, "structs.methods.mut")]
    public static Diagnostic MutOnTemporary(Location at, string method) =>
        Make(at, $"cannot call 'mut' method '{method}' on a temporary value").Temporary();

    [Error(105, "structs.methods.mut")]
    public static Diagnostic MutOnReadOnly(Location at, string method, string receiver, ReadOnlyReason reason) =>
        Make(at, $"cannot call 'mut' method '{method}' on '{receiver}'").Because(reason);

    [Error(84, "variables.locals")]
    public static Diagnostic DuplicateLocal(Location at, string name) =>
        Make(at, $"'{name}' is already defined in this scope");

    [Error(85, "variables.locals.var")]
    public static Diagnostic VarWithoutInitializer(Location at) =>
        Make(at, "'var' declaration must have an initializer");

    [Error(86, "variables.locals.var")]
    public static Diagnostic VoidInitializer(Location at) =>
        Make(at, "cannot assign a 'void' value to a variable");

    [Error(87, "variables.locals.var")]
    public static Diagnostic VarFromNull(Location at) =>
        Make(at, "cannot infer a type from 'null'").Help("declare the variable with a nullable type instead of 'var'");

    [Error(88, "variables.locals.var")]
    public static Diagnostic VarFromNullableReference(Location at, string text, string keyword) =>
        Make(at, $"'var' cannot hold '{text}', which might refer to nothing")
            .Help($"write '{keyword}? var' to refer to it, or use '!' to copy what it refers to");

    [Error(90, "variables.references")]
    public static Diagnostic ReferenceEscaping(Location at, string keyword) =>
        Make(at, $"'{keyword}' variable cannot be declared 'escaping'").Note("it refers to a place rather than holding a function");

    [Error(91, "variables.references")]
    public static Diagnostic ReferenceUninitialized(Location at, string what) =>
        Make(at, $"{what} must be initialized with the variable, field, or element it refers to");

    [Error(92, "variables.references")]
    public static Diagnostic ReferenceTypeFromNull(Location at, string what) =>
        Make(at, "cannot infer a type from 'null'").Help($"declare the type the {what} refers to instead of 'var'");

    [Error(113, "classes.inheritance")]
    public static Diagnostic ExtendInterface(Location at, string type) =>
        Make(at, $"class cannot extend interface '{type}'").Help("use 'implements'");

    [Error(114, "classes.inheritance")]
    public static Diagnostic ExtendNonClass(Location at, string type) =>
        Make(at, $"cannot extend '{type}', which is not a class");

    [Error(115, "classes.inheritance")]
    public static Diagnostic NullableBase(Location at) =>
        Make(at, "base class cannot be nullable");

    [Error(116, "classes.inheritance")]
    public static Diagnostic ExtendFinal(Location at, string type) =>
        Make(at, $"cannot extend final class '{type}'");

    [Error(117, "classes.inheritance")]
    public static Diagnostic InheritanceCycle(Location at, string name) =>
        Make(at, $"'{name}' inherits from itself");

    [Error(132, "classes.abstract")]
    public static Diagnostic AbstractMethodInConcreteClass(Location at, string type, string method) =>
        Make(at, $"'{type}' must be declared 'abstract'").Note($"it has abstract method '{method}'");

    [Error(183, "interfaces.implementing")]
    public static Diagnostic NotAnInterface(Location at, string type) =>
        Make(at, $"cannot implement '{type}', which is not an interface");

    [Error(184, "interfaces.implementing")]
    public static Diagnostic InterfaceListedTwice(Location at, string name) =>
        Make(at, $"interface '{name}' is listed more than once");

    [Error(181, "interfaces.declarations")]
    public static Diagnostic GenericInterfaceMethod(Location at) =>
        Make(at, "interface methods cannot have type parameters");

    [Error(99, "structs.fields")]
    public static Diagnostic DuplicateField(Location at, string name, Location? previous) =>
        previous is { } p
            ? Make(at, $"field '{name}' is already defined").Note(p, "previous definition here")
            : Make(at, $"field '{name}' is already defined");

    [Error(97, "structs.declarations")]
    public static Diagnostic MemberNameTaken(Location at, string name, string kind, Location previous) =>
        Make(at, $"'{name}' is already defined as a {kind}").Note(previous, "previous definition here");

    [Error(101, "structs.constructors.default")]
    public static Diagnostic ParameterlessStructConstructor(Location at) =>
        Make(at, "struct cannot declare a parameterless constructor").Help("use field initializers instead");

    [Error(106, "structs.recursive")]
    public static Diagnostic StructCycle(Location at, string kind, string name, IEnumerable<string> cycle) =>
        Make(at, $"{kind} '{name}' contains itself").Note(string.Join(" -> ", cycle))
            .Note("a struct or enum cannot hold itself by value");

    [Error(107, "structs.recursive")]
    public static Diagnostic UnboundedNesting(Location at, string kind, string name) =>
        Make(at, $"{kind} '{name}' has field types that nest without end");

    [Error(161, "enums.variants")]
    public static Diagnostic DuplicateVariant(Location at, string name) =>
        Make(at, $"variant '{name}' is already defined");

    [Error(165, "enums.repr")]
    public static Diagnostic ReprArguments(Location at) =>
        Make(at, "'@Repr' takes one integer type").Help("write '@Repr(uint)'");

    [Error(166, "enums.repr")]
    public static Diagnostic ReprTwice(Location at) =>
        Make(at, "'@Repr' can only be given once");

    [Error(167, "enums.repr")]
    public static Diagnostic ReprWithData(Location at) =>
        Make(at, "enum whose variants carry data cannot have '@Repr'");

    [Error(168, "enums.repr")]
    public static Diagnostic ReprNonInteger(Location at, string type) =>
        Make(at, $"'@Repr' type: expected an integer type, found '{type}'");

    [Error(169, "enums.repr")]
    public static Diagnostic VariantValueWithData(Location at) =>
        Make(at, "only enums whose variants carry no data can give variants values");

    [Error(170, "enums.repr")]
    public static Diagnostic DuplicateVariantValue(Location at, string name, string other, Location otherAt) =>
        Make(at, $"'{name}' has the same value as '{other}'").Note(otherAt, $"'{other}' is declared here");

    [Error(171, "enums.repr")]
    public static Diagnostic VariantValueTooLarge(Location at, string name, string type) =>
        Make(at, $"value of '{name}' does not fit in the enum's integer type '{type}'");

    [Error(172, "enums.repr")]
    public static Diagnostic VariantValueNotLiteral(Location at) =>
        Make(at, "variant's value must be an integer literal");

    [Error(173, "enums.repr")]
    public static Diagnostic VariantLiteralTooLarge(Location at, string type) =>
        Make(at, $"value does not fit in the enum's integer type '{type}'");

    [Error(139, "classes.constructors")]
    public static Diagnostic SuperCallWithoutBase(Location at, string type) =>
        Make(at, $"cannot call 'super(...)' in '{type}'").Note($"'{type}' has no base class");

    [Error(140, "classes.constructors")]
    public static Diagnostic NoParameterlessBaseConstructor(Location at, string baseType) =>
        Make(at, "constructor must start with 'super(...)'").Note($"'{baseType}' has no parameterless constructor");

    [Error(141, "classes.constructors")]
    public static Diagnostic ConstructorCycle(Location at, string constructor, IEnumerable<(string From, string To, Location At)> steps) =>
        steps.Aggregate(Make(at, $"constructor '{constructor}' calls itself through 'this(...)'"),
            (d, step) => d.Note(step.At, $"'{step.From}' calls '{step.To}' here"));

    [Error(142, "classes.construction")]
    public static Diagnostic ConstructorCallArgumentNotHeld(Location at, string keyword) =>
        Make(at, $"argument to '{keyword}(...)' for a reference or view parameter must be a parameter, a field of one, or 'null'");

    [Error(143, "classes.construction")]
    public static Diagnostic FieldWithoutDefault(Location at, string field, string type) =>
        Make(at, $"field '{field}' of type '{type}' has no initial value").Help("give it an initializer or assign it in a constructor");

    [Error(102, "structs.constructors.default")]
    public static Diagnostic NoDefaultValue(Location at, string type, string field, string fieldType) =>
        Make(at, $"'{type}' has no default value").Note($"field '{field}' has type '{fieldType}', which has none")
            .Help($"pass it by name, as in '{type}({field}: ...)'");

    [Error(182, "interfaces.declarations")]
    public static Diagnostic RedeclaredInterfaceMethod(Location at, string signature, string iface) =>
        Make(at, $"'{signature}' is already declared in interface '{iface}'");

    [Error(185, "interfaces.implementing")]
    public static Diagnostic MissingInterfaceImplementation(Location at, string type, string method) =>
        Make(at, $"'{type}' does not implement '{method}'");

    [Error(186, "interfaces.implementing")]
    public static Diagnostic StaticImplementation(Location at, string method, string required) =>
        Make(at, $"static method '{method}' cannot implement '{required}'");

    [Error(187, "interfaces.implementing")]
    public static Diagnostic ImplementationVisibility(Location at, string method, string required, Visibility visibility, string iface) =>
        Make(at, $"'{method}' must have {Written(visibility)}").Note($"it implements '{required}', and interface '{iface}' has {Written(visibility)}");

    [Error(188, "interfaces.implementing")]
    public static Diagnostic ImplementationSignature(Location at, string method, string required, EscapingDifference? difference) =>
        Make(at, $"'{method}' does not match the parameter and return types of '{required}'").Escaping(difference, $"'{required}'");

    [Error(190, "interfaces.mut")]
    public static Diagnostic MutImplementation(Location at, string method, string required) =>
        Make(at, $"'{method}' cannot be 'mut'").Note($"'{required}' is not 'mut'");

    [Error(189, "interfaces.implementing")]
    public static Diagnostic UnsafeImplementation(Location at, string method, string required) =>
        Make(at, $"'{method}' cannot be 'unsafe'").Note($"'{required}' is not, and a call through it needs no 'unsafe' code");

    [Error(110, "classes.fields")]
    public static Diagnostic FieldInBase(Location at, string name, string baseType) =>
        Make(at, $"field '{name}' is already defined in base class '{baseType}'");

    [Error(133, "classes.abstract")]
    public static Diagnostic MissingAbstractImplementation(Location at, string type, string method) =>
        Make(at, $"'{type}' does not implement abstract method '{method}'");

    [Error(120, "classes.overriding")]
    public static Diagnostic MissingOverride(Location at, string signature, string inherited) =>
        Make(at, $"'{signature}' must be marked 'override'").Note($"it overrides '{inherited}'");

    [Error(121, "classes.overriding")]
    public static Diagnostic HidesInherited(Location at, string signature, string inherited) =>
        Make(at, $"'{signature}' hides inherited method '{inherited}'").Note("only 'virtual' methods can be overridden");

    [Error(122, "classes.overriding")]
    public static Diagnostic OverridesNothing(Location at, string signature) =>
        Make(at, $"'{signature}' overrides nothing").Note("no base class has a method with these parameters");

    [Error(123, "classes.overriding")]
    public static Diagnostic OverrideFinal(Location at, string method) =>
        Make(at, $"cannot override final method '{method}'");

    [Error(124, "classes.overriding")]
    public static Diagnostic OverrideNonVirtual(Location at, string method) =>
        Make(at, $"cannot override '{method}'").Note("it is not 'virtual'");

    [Error(125, "classes.overriding")]
    public static Diagnostic OverrideSignature(Location at, string name, string overridden, EscapingDifference? difference) =>
        Make(at, $"'{name}' does not match the parameter and return types of '{overridden}'").Escaping(difference, $"'{overridden}'");

    [Error(126, "classes.overriding")]
    public static Diagnostic OverrideVisibility(Location at, string name, string overridden, Visibility visibility) =>
        Make(at, $"'{name}' must have {Written(visibility)}").Note($"it overrides '{overridden}', which has {Written(visibility)}");

    [Error(127, "classes.overriding")]
    public static Diagnostic UnsafeOverride(Location at, string name, string overridden) =>
        Make(at, $"'{name}' cannot be 'unsafe'").Note($"'{overridden}' is not, and a call through it needs no 'unsafe' code");

    /// <param name="what"><c>field</c>, or <c>variant field</c> for an enum.</param>
    [Error(439, "structs.unique")]
    public static Diagnostic UniqueField(Location at, string kind, string name, string what, string type) =>
        Make(at, $"{kind} '{name}' has a {what} of unique type '{type}'").Help($"declare it 'unique {kind} {name}'");
}
