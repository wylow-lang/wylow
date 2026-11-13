using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Diagnostics;

// Chapter 12, functions and methods.
public static partial class Catalog
{
    [Error(446, "functions.parameters")]
    public static Diagnostic ReceiverNotFirst(Location at) =>
        Make(at, "'this' parameter must come first").Note("a method's receiver is its first parameter");

    [Error(209, "functions.declarations")]
    public static Diagnostic ModifierOnFunction(Location at, string modifier) =>
        Make(at, $"'{modifier}' cannot be used on a function");

    [Error(210, "functions.declarations")]
    public static Diagnostic ModifierOnType(Location at, string modifier, string kind) =>
        Make(at, $"'{modifier}' cannot be used on {kind}");

    [Error(211, "functions.declarations")]
    public static Diagnostic MissingBody(Location at, string name) =>
        Make(at, $"'{name}' has no body");

    [Error(238, "functions.methods")]
    public static Diagnostic ModifierOnStaticMethod(Location at, string modifier) =>
        Make(at, $"static method cannot be '{modifier}'");

    [Error(220, "functions.parameters.modes")]
    public static Diagnostic ParameterOnlyModifier(Location at, string modifier) =>
        Make(at, $"'{modifier}' can only be used on parameters");

    [Error(247, "functions.indexers")]
    public static Diagnostic OperatorOverloading(Location at) =>
        Make(at, "only 'operator []' and 'operator []=' can be declared");

    [Error(248, "functions.indexers")]
    public static Diagnostic ThisIndexer(Location at) =>
        Make(at, "indexer is not declared with 'operator []'").Help("write 'ref T operator [](int index)'");

    [Error(258, "functions.lambdas")]
    public static Diagnostic MixedLambdaParameters(Location at) =>
        Make(at, "lambda parameters must all have types or all leave them out");

    [Error(265, "functions.lambdas.escaping")]
    public static Diagnostic EscapingElsewhere(Location at) =>
        Make(at, "'escaping' can only be used on parameters, local variables, and fields of a 'ref struct'")
            .Note("other function values are always escaping");

    [Error(266, "functions.lambdas.escaping")]
    public static Diagnostic MutAfterEscaping(Location at) =>
        Make(at, "'mut' must come before 'escaping'").Help("write 'mut escaping fn() -> void f'");

    [Error(212, "functions.parameters")]
    public static Diagnostic EveryParameterDefaulted(Location at, string type) =>
        Make(at, "struct constructor cannot give every parameter a default value")
            .Note($"'{type}()' is always the default value of '{type}'");

    [Error(213, "functions.parameters")]
    public static Diagnostic DefaultOnOverride(Location at, string method) =>
        Make(at, $"'{method}' cannot declare default values")
            .Note("a method that overrides or implements another takes that method's default values");

    [Error(214, "functions.parameters")]
    public static Diagnostic ConflictingInheritedDefaults(Location at, string method, string parameter, IEnumerable<(Location At, string From)> sources) =>
        sources.Aggregate(Make(at, $"'{method}' inherits different default values for parameter '{parameter}'"),
            (d, source) => d.Note(source.At, $"default value from '{source.From}'"));

    [Error(215, "functions.parameters")]
    public static Diagnostic DefaultDependsOnItself(Location at, string parameter) =>
        Make(at, $"default value of '{parameter}' depends on itself");

    [Error(216, "functions.parameters")]
    public static Diagnostic NonConstantDefault(Location at) =>
        Make(at, "default value must be a constant or a struct constructor call");

    [Error(217, "functions.parameters")]
    public static Diagnostic GenericDefault(Location at, string parameter, string type) =>
        Make(at, $"default value of '{parameter}' must be 'null'")
            .Note($"its type '{type}' involves a type parameter");

    [Error(226, "functions.arguments")]
    public static Diagnostic PositionalAfterNamed(Location at) =>
        Make(at, "positional argument cannot follow a named argument");

    [Error(278, "functions.choices")]
    public static Diagnostic CaseNameInChosenLambda(Location at, string name) =>
        Make(at, $"lambda chosen by a switch expression cannot use '{name}'")
            .Note($"'{name}' is declared by the lambda's case").Help("use a switch statement");

    [Error(260, "functions.lambdas.typing")]
    public static Diagnostic UntypedLambda(Location at) =>
        Make(at, "lambda has no function type to take").Help("use it where a function type is expected, as in 'fn(int) -> int f = x -> x + 1;'");

    [Error(227, "functions.arguments")]
    public static Diagnostic NoParameterNamed(Location at, string function, string name) =>
        Make(at, $"cannot find parameter '{name}' in '{function}'");

    [Error(221, "functions.parameters.modes")]
    public static Diagnostic ArgumentNotRef(Location at, string what, string function) =>
        Make(at, $"{what} of '{function}' cannot be passed with 'ref'");

    [Error(222, "functions.parameters.modes")]
    public static Diagnostic RefArgumentNotPlace(Location at, string? elementless) =>
        Make(at, "'ref' argument must be a variable, field, or element").Elementless(elementless);

    [Error(223, "functions.parameters.modes")]
    public static Diagnostic RefArgumentReadOnly(Location at, string text, ReadOnlyReason reason) =>
        Make(at, $"cannot pass '{text}' by 'ref'").Because(reason);

    [Error(233, "functions.overloading.resolution")]
    public static Diagnostic NoOverloadForCount(Location at, string description, int count) =>
        Make(at, $"no {description} takes {Plural(count, "argument")}");

    [Error(249, "functions.indexers.setters")]
    public static Diagnostic NoIndexerSetter(Location at, string type, string getter) =>
        Make(at, $"cannot assign an element of '{type}'").Note($"'{type}' has no 'operator []=' for the element '{getter}' reads");

    [Error(250, "functions.indexers.setters")]
    public static Diagnostic SetterWithRefIndexer(Location at, string type) =>
        Make(at, $"'{type}' cannot have both 'operator []=' and an 'operator []' that returns 'ref'")
            .Note("an assignment would have two meanings");

    [Error(251, "functions.indexers.setters")]
    public static Diagnostic SetterReturn(Location at) =>
        Make(at, "'operator []=' must return 'void'");

    [Error(252, "functions.indexers.setters")]
    public static Diagnostic StaticSetter(Location at) =>
        Make(at, "'operator []=' cannot be static");

    [Error(253, "functions.indexers.setters")]
    public static Diagnostic SetterParameters(Location at) =>
        Make(at, "'operator []=' must take the indices, then the assigned value").Help("write 'void operator []=(int i, T value)'");

    [Error(254, "functions.indexers.setters")]
    public static Diagnostic SetterDefaults(Location at) =>
        Make(at, "'operator []=' cannot have default values").Note("an assignment takes the default values of the matching 'operator []'");

    [Error(255, "functions.indexers.setters")]
    public static Diagnostic SetterRefValue(Location at) =>
        Make(at, "value of 'operator []=' cannot be 'ref'").Note("it is passed by value or 'in'");

    [Error(256, "functions.indexers.setters")]
    public static Diagnostic SetterWithoutGetter(Location at, string indices) =>
        Make(at, $"'operator []=' has no matching 'operator []({indices})'")
            .Note("an 'operator []' with the same indices must return the element it assigns");

    [Error(257, "functions.indexers.setters")]
    public static Diagnostic SetterValueType(Location at, string expected, string found, string indices) =>
        Make(at, $"value of 'operator []=': expected '{expected}', found '{found}'")
            .Note($"'operator []({indices})' returns '{expected}'");

    [Error(267, "functions.lambdas.escaping")]
    public static Diagnostic NonEscapingMut(Location at) =>
        Make(at, "non-escaping function cannot be 'mut'").Note("it keeps the function it was given")
            .Help("declare it 'escaping' to reassign it");

    [Error(547, "structs.ref")]
    public static Diagnostic NonEscapingFieldInitializer(Location at, string name) =>
        Make(at, $"non-escaping field '{name}' cannot have an initializer").Note("a constructor sets it from one of its parameters");

    [Error(548, "structs.ref")]
    public static Diagnostic NonEscapingFieldSource(Location at, string name) =>
        Make(at, $"non-escaping field '{name}' can only be assigned a parameter of the constructor");

    [Error(549, "structs.ref")]
    public static Diagnostic RefStructFunctionArgument(Location at, string type) =>
        Make(at, $"non-escaping function argument of a constructor of '{type}' must be a variable that is not 'mut'")
            .Help("declare the function in a local first");

    [Error(268, "functions.lambdas.escaping")]
    public static Diagnostic ReassignNonEscaping(Location at, string name, bool isParameter) =>
        Make(at, $"cannot reassign non-escaping {(isParameter ? "parameter" : "variable")} '{name}'").Note("it keeps the function it was given")
            .Help("declare a new variable, or declare this one 'escaping'");

    [Error(272, "functions.lambdas.captures")]
    public static Diagnostic AssignDeferredInLambda(Location at, string name) =>
        Make(at, $"cannot assign to '{name}' in a lambda").Note($"a lambda may run more than once, and variable '{name}' is not 'mut'");

    [Error(239, "functions.ref-returns")]
    public static Diagnostic ReturnRefWithoutReference(Location at) =>
        Make(at, "'return ref' can only be used in a method that returns 'ref' or 'in'");

    [Error(240, "functions.ref-returns")]
    public static Diagnostic ReturnReferenceWithoutRef(Location at, string name, bool mayBeNull) =>
        mayBeNull
            ? Make(at, $"'{name}' returns a reference, so 'return' must be 'return ref' or 'return null'")
            : Make(at, $"'{name}' returns a reference, so 'return' must be 'return ref'");

    [Error(241, "functions.ref-returns")]
    public static Diagnostic ReturnedReferenceNotOfReceiver(Location at, string what) =>
        Make(at, $"{what} must point into the method's receiver").Note("it must be in 'this', or in an object that 'this' refers to");

    [Error(242, "functions.ref-returns")]
    public static Diagnostic ReturnedViewNotOfReceiver(Location at, string type, string? holder) =>
        holder is null
            ? Make(at, $"returned '{type}' must view the method's receiver").Note("it must view fields or elements of 'this', or be a span its methods return")
            : Make(at, $"returned '{type}' must view the method's receiver").Note("it must view fields or elements of 'this', or be a span its methods return")
                .Note($"'{holder}' holds its contents itself, and could replace them while the caller uses them");

    [Error(269, "functions.lambdas.escaping")]
    public static Diagnostic NullableNonEscapingVariable(Location at) =>
        Make(at, "variable of nullable function type must be declared 'escaping'")
            .Note("only a parameter can hold a nullable non-escaping function");

    [Error(270, "functions.lambdas.escaping")]
    public static Diagnostic EscapingOnNonFunction(Location at, string what, string type) =>
        Make(at, $"'escaping' cannot be declared on a {what} of non-function type '{type}'");

    [Error(271, "functions.lambdas.escaping")]
    public static Diagnostic NonEscapingStored(Location at, string what, string name, bool ofLambda) =>
        Make(at, $"non-escaping {what} '{name}' cannot be stored or returned")
            .Help(ofLambda ? "declare the parameter 'escaping' in the lambda's function type, as in 'fn(escaping fn() -> void) -> void'" : "declare it 'escaping'");

    [Error(273, "functions.lambdas.captures")]
    public static Diagnostic EscapingCapture(Location at, string name, CaptureProblem problem, bool isParameter, string detail) => problem switch
    {
        CaptureProblem.Reference => Make(at, $"escaping lambda cannot capture '{name}'")
            .Note($"'{name}' is a '{detail}' {(isParameter ? "parameter" : "variable")}").Help("copy its value to a local first"),
        CaptureProblem.View => Make(at, $"escaping lambda cannot capture '{name}'")
            .Note($"'{name}' is a '{detail}', which cannot outlive what it views"),
        CaptureProblem.NonEscaping => Make(at, $"escaping lambda cannot capture '{name}'")
            .Note($"'{name}' is a non-escaping {(isParameter ? "parameter" : "variable")}").Help("declare it 'escaping'"),
        _ => Make(at, $"escaping lambda cannot capture '{name}'")
            .Note($"'{name}' is 'mut', and the lambda would have only a copy").Help("copy it to a variable that is not 'mut'"),
    };

    [Error(274, "functions.lambdas.captures")]
    public static Diagnostic EscapingCaptureOfStructThis(Location at) =>
        Make(at, "escaping lambda cannot capture 'this' in a struct's method or constructor")
            .Note("'this' is a reference to the struct").Help("copy what the lambda needs to a local first");

    [Error(472, "concurrency.shared-functions")]
    public static Diagnostic SharedCaptureNotSendable(Location at, string name, string type, bool isParameter) =>
        Make(at, $"shared lambda cannot capture '{name}'")
            .Note($"'{name}' is a {(isParameter ? "parameter" : "variable")} of type '{type}', which is not sendable");

    [Error(473, "concurrency.shared-functions")]
    public static Diagnostic SharedCaptureNotCopyable(Location at, string name, string type, bool isParameter) =>
        Make(at, $"shared lambda cannot capture '{name}'")
            .Note($"'{name}' is a {(isParameter ? "parameter" : "variable")} of type '{type}', which is not copyable")
            .Help("move it to the lambda as an argument instead");

    [Error(474, "concurrency.shared-functions")]
    public static Diagnostic SharedCaptureMutable(Location at, string name, bool isParameter) =>
        Make(at, $"shared lambda cannot capture '{name}'")
            .Note($"'{name}' is a 'mut' {(isParameter ? "parameter" : "variable")}")
            .Help("copy it to a variable that is not 'mut'");

    [Error(475, "concurrency.shared-functions")]
    public static Diagnostic SharedCaptureReference(Location at, string name, string keyword, bool isParameter) =>
        Make(at, $"shared lambda cannot capture '{name}'")
            .Note($"'{name}' is a '{keyword}' {(isParameter ? "parameter" : "variable")}, which another thread could change")
            .Help("copy it to a local first");

    [Error(476, "concurrency.shared-functions")]
    public static Diagnostic SharedCaptureView(Location at, string name, string type) =>
        Make(at, $"shared lambda cannot capture '{name}'")
            .Note($"'{name}' is a '{type}', which another thread could change")
            .Help("capture a 'ReadOnlySpan' instead");

    [Error(477, "concurrency.shared-functions")]
    public static Diagnostic SharedCaptureOfThis(Location at) =>
        Make(at, "shared lambda cannot capture 'this' outside a shared class's method")
            .Help("copy what the lambda needs to a local first");

    [Error(232, "functions.overloading")]
    public static Diagnostic DuplicateFunction(Location at, string kind, string signature, Location? previous, string? escapingOnly) =>
        (previous, escapingOnly) switch
        {
            ({ } p, { } e) => Make(at, $"{kind} '{signature}' is already defined").Note(p, "previous definition here")
                .Note($"it differs from '{e}' only in which parameters are 'escaping', and overloads cannot"),
            ({ } p, null) => Make(at, $"{kind} '{signature}' is already defined").Note(p, "previous definition here"),
            (null, { } e) => Make(at, $"{kind} '{signature}' is already defined")
                .Note($"it differs from '{e}' only in which parameters are 'escaping', and overloads cannot"),
            _ => Make(at, $"{kind} '{signature}' is already defined"),
        };

    [Error(218, "functions.parameters")]
    public static Diagnostic DuplicateParameter(Location at, string name) =>
        Make(at, $"parameter '{name}' is already defined");

    [Error(219, "functions.parameters")]
    public static Diagnostic RefParameterDefault(Location at, string name) =>
        Make(at, $"'ref' parameter '{name}' cannot have a default value").Note("a 'ref' argument must be a variable");

    [Error(243, "functions.ref-returns")]
    public static Diagnostic ViewReturnFromNonInstance(Location at, string type) =>
        Make(at, $"only instance methods can return '{type}'").Note("it views the method's receiver");

    [Error(244, "functions.ref-returns")]
    public static Diagnostic StaticReferenceReturn(Location at, string keyword) =>
        Make(at, $"static method cannot return '{keyword}'").Note("a returned reference must point into the method's receiver");

    [Error(246, "functions.ref-returns")]
    public static Diagnostic VoidReferenceReturn(Location at, string keyword) =>
        Make(at, $"'void' cannot be returned by '{keyword}'");

    [Error(275, "functions.references")]
    public static Diagnostic InstanceMethodReference(Location at, string name) =>
        Make(at, $"instance method '{name}' cannot be used as a value").Note("only functions and static methods can")
            .Help("use a lambda that calls it");

    [Error(276, "functions.references")]
    public static Diagnostic OverloadedReference(Location at, string name) =>
        Make(at, $"'{name}' can only be used as a value where a function type is expected")
            .Note("its overloads take different numbers of arguments").Help("use a lambda that calls it");

    [Error(228, "functions.arguments")]
    public static Diagnostic ArgumentCount(Location at, string function, int expected, int found) =>
        Make(at, $"argument count of '{function}': expected {expected}, found {found}");

    [Error(229, "functions.arguments")]
    public static Diagnostic ArgumentGivenTwice(Location at, string parameter) =>
        Make(at, $"parameter '{parameter}' is given more than one argument");

    [Error(230, "functions.arguments")]
    public static Diagnostic NamedArgumentOrder(Location at, string name, string previous) =>
        Make(at, $"argument '{name}' must come before '{previous}'").Note("named arguments follow the order of the parameters");

    [Error(231, "functions.arguments")]
    public static Diagnostic MissingArgument(Location at, string function, string parameter) =>
        Make(at, $"call to '{function}' has no argument for parameter '{parameter}'");

    [Error(224, "functions.parameters.modes")]
    public static Diagnostic RefMissing(Location at, string what, string function) =>
        Make(at, $"{what} of '{function}' must be passed with 'ref'");

    [Error(225, "functions.parameters.modes")]
    public static Diagnostic RefArgumentType(Location at, string expected, string found) =>
        Make(at, $"'ref' argument: expected '{expected}', found '{found}'").Note("a 'ref' argument has exactly the type of its parameter");

    [Error(234, "functions.overloading.resolution")]
    public static Diagnostic NoOverloadForTypeArguments(Location at, string description, int count) =>
        Make(at, $"no {description} takes {Plural(count, "type argument")}");

    [Error(235, "functions.overloading.resolution")]
    public static Diagnostic NoOverloadForNames(Location at, string description) =>
        Make(at, $"no {description} takes these named arguments");

    [Error(236, "functions.overloading.resolution")]
    public static Diagnostic NoOverloadAccepts(Location at, string description, string arguments) =>
        Make(at, $"no {description} accepts ({arguments})");

    [Error(237, "functions.overloading.resolution")]
    public static Diagnostic AmbiguousCall(Location at, string name, IEnumerable<(Location? At, string Candidate)> candidates) =>
        candidates.Aggregate(Make(at, $"call to '{name}' is ambiguous"), (d, c) => d.Note(c.At, $"candidate '{c.Candidate}'"));

    [Error(261, "functions.lambdas.typing")]
    public static Diagnostic LambdaParameterCount(Location at, string type, int expected, int found) =>
        Make(at, $"parameters of lambda for '{type}': expected {expected}, found {found}");

    [Error(262, "functions.lambdas.typing")]
    public static Diagnostic UntypedEscapingParameter(Location at) =>
        Make(at, "untyped lambda parameter cannot be declared 'escaping'").Note("it takes its form from the lambda's type")
            .Help("give it a type");

    [Error(263, "functions.lambdas.typing")]
    public static Diagnostic LambdaParameterType(Location at, string name, string expected, string found, string type) =>
        Make(at, $"type of parameter '{name}': expected '{expected}', found '{found}'").Note($"'{type}' passes '{expected}'");

    [Error(264, "functions.lambdas.typing")]
    public static Diagnostic MutReferenceParameter(Location at, string name, string keyword) =>
        Make(at, $"parameter '{name}' cannot be 'mut'").Note($"it is passed as '{keyword}'");

    [Error(277, "functions.references")]
    public static Diagnostic FunctionReferenceWithoutType(Location at, string text) =>
        Make(at, $"function '{text}' can only be used as a value where a function type is expected");

    [Error(259, "functions.lambdas")]
    public static Diagnostic NamedFunctionValueArgument(Location at, string text) =>
        Make(at, $"arguments to '{text}' cannot be named").Note("a function type's parameters have no names");

    [Error(100, "structs.constructors.memberwise")]
    public static Diagnostic MissingFieldValue(Location at, string type, string field, string fieldType) =>
        Make(at, $"'{type}' needs a value for field '{field}'").Note($"its type '{fieldType}' has no default value");

    [Error(457, "functions.lambdas.captures")]
    public static Diagnostic LambdaMovesCapture(Location at, string name) =>
        Make(at, $"cannot move '{name}', which a lambda captures").Note("the lambda may run more than once");

    [Error(487, "functions.extensions")]
    public static Diagnostic RefReceiverOnTemporary(Location at, string extension) =>
        Make(at, $"cannot call '{extension}' on a temporary value").Note($"'{extension}' takes its receiver as 'this ref', which needs a variable").Temporary();

    [Error(488, "functions.extensions")]
    public static Diagnostic RefReceiverOnReadOnly(Location at, string extension, string receiver, ReadOnlyReason reason) =>
        Make(at, $"cannot call '{extension}' on '{receiver}'").Because(reason).Note($"'{extension}' takes its receiver as 'this ref', which it may change");

    [Error(489, "functions.extensions")]
    public static Diagnostic RefReceiverNullable(Location at, string extension, string receiver, string type) =>
        Make(at, $"cannot call '{extension}' on '{receiver}' of nullable type '{type}'")
            .Note($"'{extension}' takes its receiver as 'this ref', and nothing refers into a nullable value")
            .Help($"declare the receiver as 'this ref {type}' to take the nullable value");
}
