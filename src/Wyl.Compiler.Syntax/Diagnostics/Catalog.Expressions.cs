using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Diagnostics;

// Chapters 13 to 15: expressions, statements, and patterns.
public static partial class Catalog
{
    [Error(286, "expressions.construction")]
    public static Diagnostic InitializerBraces(Location at, string type) =>
        Make(at, "fields cannot be set in braces after a construction")
            .Help($"name them in the constructor call, as in '{type}(field: value)'");

    [Error(322, "expressions.switch")]
    public static Diagnostic BlockInSwitchExpression(Location at) =>
        Make(at, "cases of a switch expression cannot be blocks").Help("use a switch statement");

    [Error(337, "expressions.interpolation.format")]
    public static Diagnostic AlignmentTooLarge(Location at) =>
        Make(at, "alignment is too large");

    [Error(345, "statements.assignment")]
    public static Diagnostic AssignmentInExpression(Location at) =>
        Make(at, "assignment cannot be used as an expression");

    [Error(341, "statements.blocks")]
    public static Diagnostic BodyWithoutBraces(Location at, string keyword) =>
        Make(at, $"body of '{keyword}' is not enclosed in braces");

    [Error(342, "statements.declarations")]
    public static Diagnostic DeclarationNotAllowed(Location at) =>
        Make(at, "variable cannot be declared here");

    [Error(365, "statements.switch")]
    public static Diagnostic DeclarationInCase(Location at) =>
        Make(at, "variable cannot be declared directly in a case").Help("use a block, as in 'case X -> { ... }'");

    [Error(366, "statements.switch")]
    public static Diagnostic ColonCase(Location at) =>
        Make(at, "expected '->', found ':'").Note("a case is written 'case X -> ...', and there is no fallthrough");

    [Error(375, "patterns.kinds")]
    public static Diagnostic FunctionTypePattern(Location at) =>
        Make(at, "type pattern cannot name a function type").Help("compare a nullable function with 'null'");

    [Error(328, "expressions.is")]
    public static Diagnostic IsOnVoid(Location at) =>
        Make(at, "'is' cannot test a 'void' value");

    [Error(329, "expressions.is")]
    public static Diagnostic NullPatternInIs(Location at) =>
        Make(at, "'null' cannot be a pattern of 'is'").Help("compare with '== null'");

    [Error(330, "expressions.is")]
    public static Diagnostic TypeTestOnValue(Location at, string type) =>
        Make(at, $"'is' cannot test the type of a value of type '{type}'")
            .Note("only class and interface values have a type to test");

    [Error(331, "expressions.is")]
    public static Diagnostic PatternAlwaysMatches(Location at) =>
        Make(at, "pattern always matches").Note("'is' would always be true");

    [Error(323, "expressions.switch")]
    public static Diagnostic VoidSwitchCase(Location at) =>
        Make(at, "case of a switch expression must produce a value");

    [Error(324, "expressions.switch")]
    public static Diagnostic NoProducingCase(Location at) =>
        Make(at, "switch expression has no case that produces a value");

    [Error(325, "expressions.switch")]
    public static Diagnostic IncompatibleCases(Location at, IEnumerable<string> types) =>
        Make(at, $"cases of the switch expression have incompatible types {string.Join(", ", types.Select(t => $"'{t}'"))}");

    [Error(326, "expressions.switch")]
    public static Diagnostic SwitchExpressionView(Location at, string type) =>
        Make(at, $"switch expression cannot produce a value of type '{type}'")
            .Note("the result would view one of its cases").Help("use a switch statement");

    [Error(327, "expressions.switch")]
    public static Diagnostic SwitchExpressionReference(Location at) =>
        Make(at, "switch expression cannot produce a reference").Help("use a switch statement with a variable for each case");

    [Error(367, "statements.switch")]
    public static Diagnostic SwitchOnVoid(Location at) =>
        Make(at, "cannot switch on a 'void' value");

    [Error(368, "statements.switch")]
    public static Diagnostic SwitchOnNull(Location at) =>
        Make(at, "cannot switch on 'null'").Note("'null' has no type of its own");

    [Error(369, "statements.switch")]
    public static Diagnostic SwitchOnNullableReference(Location at, string type) =>
        Make(at, $"cannot switch on a value of type '{type}'").Help("check it for null first, or use '!'");

    [Error(370, "statements.switch")]
    public static Diagnostic DefaultNotLast(Location at) =>
        Make(at, "'default' must be the last case");

    [Error(376, "patterns.kinds")]
    public static Diagnostic LiteralPatternKind(Location at) =>
        Make(at, "only integer, character, and 'bool' literals can be patterns");

    [Error(377, "patterns.kinds")]
    public static Diagnostic NoSuchVariantInPattern(Location at, string type, string variant) =>
        Make(at, $"cannot find variant '{variant}' in enum '{type}'");

    [Error(378, "patterns.kinds")]
    public static Diagnostic VariantPatternWithoutData(Location at, string variant) =>
        Make(at, $"variant '{variant}' carries no data").Help($"match it without parentheses, as '{variant}'");

    [Error(379, "patterns.kinds")]
    public static Diagnostic PatternFieldCount(Location at, string type, int expected, int found) =>
        Make(at, $"fields of '{type}': expected {expected}, found {found}");

    [Error(380, "patterns.matching")]
    public static Diagnostic PatternNeverMatches(Location at, string pattern, string type) =>
        Make(at, $"pattern '{pattern}' can never match a value of type '{type}'");

    [Error(381, "patterns.matching")]
    public static Diagnostic MatchViewType(Location at, string type) =>
        Make(at, $"cannot match a value of type '{type}' against patterns");

    [Error(382, "patterns.bindings")]
    public static Diagnostic BindingInSeveralPatterns(Location at) =>
        Make(at, "case with several patterns cannot bind names");

    [Error(383, "patterns.references")]
    public static Diagnostic ReferencePatternOnValue(Location at, string keyword, string text, string type) =>
        Make(at, $"'is {keyword}' cannot test '{text}' of type '{type}'")
            .Note($"'is {keyword}' tests whether a nullable reference refers to a place");

    [Error(384, "patterns.references")]
    public static Diagnostic ReferencePatternType(Location at, string text, string referenced, string written) =>
        Make(at, $"pattern type: expected '{referenced}', found '{written}'")
            .Note($"'{text}' refers to a value of type '{referenced}'");

    [Error(445, "patterns.references")]
    public static Diagnostic VariantFieldReference(Location at) =>
        Make(at, "cannot bind 'ref' to a field of a variant").Note("a variant's field is not changed in place").Help("bind it with 'in'");

    [Error(386, "patterns.cases.guards")]
    public static Diagnostic GuardOnDefault(Location at) =>
        Make(at, "case with 'default' cannot have a guard").Note("'default' matches every value the cases above do not");

    [Error(387, "patterns.cases.guards")]
    public static Diagnostic GuardOnNull(Location at) =>
        Make(at, "case with 'null' cannot have a guard").Help("test the condition inside the case");

    [Error(388, "patterns.cases.null")]
    public static Diagnostic NullNeverMatches(Location at, string type) =>
        Make(at, $"pattern 'null' can never match a value of non-nullable type '{type}'");

    [Error(389, "patterns.cases.null")]
    public static Diagnostic DefaultListedWithPattern(Location at) =>
        Make(at, "only 'null' can be listed with 'default'").Help("write 'case null, default'");

    [Error(390, "patterns.cases.null")]
    public static Diagnostic SwitchOnNullable(Location at, string type) =>
        Make(at, $"cannot switch on a value of nullable type '{type}' without a 'case null'")
            .Help("check it for null first, or use '!' or '??'");

    [Error(391, "patterns.exhaustiveness")]
    public static Diagnostic UnreachableCase(Location at) =>
        Make(at, "case can never be reached").Note("the cases above match every value it matches");

    [Error(392, "patterns.exhaustiveness")]
    public static Diagnostic UnreachableDefault(Location at) =>
        Make(at, "'default' can never be reached").Note("the cases above match every value");

    [Error(393, "patterns.exhaustiveness")]
    public static Diagnostic NotExhaustive(Location at, string type, string? missing) =>
        missing is null
            ? Make(at, $"switch does not handle every value of type '{type}'").Help("add a 'default' case")
            : Make(at, $"switch does not handle every value of type '{type}'").Note($"'{missing}' is not covered");

    [Error(279, "expressions.primary")]
    public static Diagnostic TypeNotValue(Location at, string written) =>
        Make(at, $"'{written}' is a type, not a value");

    [Error(280, "expressions.primary")]
    public static Diagnostic FunctionNotValue(Location at, string kind, string written, string call, bool canReference) =>
        Make(at, $"{kind} '{written}' is used as a value")
            .Help(canReference ? $"call it with '{call}(...)', or use it where a function type is expected" : $"call it with '{call}(...)'");

    [Error(281, "expressions.primary")]
    public static Diagnostic UndefinedName(Location at, string? kind, string name, string? variantOf, bool genericEnum) =>
        variantOf is null ? Make(at, $"cannot find {(kind is null ? "" : kind + " ")}'{name}' in this scope")
        : Make(at, $"cannot find {(kind is null ? "" : kind + " ")}'{name}' in this scope").Help(genericEnum
            ? $"to create the variant of '{variantOf}', write '{variantOf}<...>.{name}', or use it where a '{variantOf}' is expected"
            : $"to create the variant of '{variantOf}', write '{variantOf}.{name}'");

    [Error(282, "expressions.primary")]
    public static Diagnostic InstanceMemberUnavailable(Location at, string kind, string name, NoThisContext context) =>
        Make(at, $"instance {kind} '{name}' cannot be used here").Note(NoThisReason(context));

    [Error(283, "expressions.primary")]
    public static Diagnostic ThisOutsideType(Location at) =>
        Make(at, "'this' can only be used inside a struct or class");

    [Error(284, "expressions.primary")]
    public static Diagnostic ThisUnavailable(Location at, NoThisContext context) =>
        Make(at, "'this' cannot be used here").Note(NoThisReason(context));

    static string NoThisReason(NoThisContext context) => context switch
    {
        NoThisContext.FieldInitializer => "field initializers cannot use 'this', fields, or instance methods",
        NoThisContext.SuperArguments => "arguments to 'super(...)' cannot use 'this', fields, or instance methods",
        NoThisContext.ChainArguments => "arguments to 'this(...)' cannot use 'this', fields, or instance methods",
        NoThisContext.DefaultValue => "default values cannot use 'this', fields, or instance methods",
        _ => "static methods cannot use 'this', fields, or instance methods",
    };

    [Error(285, "expressions.primary")]
    public static Diagnostic SuperNotCall(Location at) =>
        Make(at, "'super' can only be used to call a base class constructor or method");

    [Error(292, "expressions.members")]
    public static Diagnostic SuperField(Location at, string name) =>
        Make(at, "'super' can only be used to call methods").Help($"use 'this.{name}' for a field");

    [Error(293, "expressions.members")]
    public static Diagnostic NoStaticMember(Location at, string type, string name) =>
        Make(at, $"cannot find static member '{name}' in '{type}'");

    [Error(294, "expressions.members")]
    public static Diagnostic NoFixedArrayMember(Location at, string type, string name) =>
        Make(at, $"cannot find member '{name}' in '{type}'").Note("a fixed array's only member is 'length'");

    [Error(295, "expressions.members")]
    public static Diagnostic VariantFieldAccess(Location at, string name) =>
        Make(at, "fields of a variant can only be read with a pattern").Help($"write 'if (x is Variant(var {name}))'");

    [Error(296, "expressions.members")]
    public static Diagnostic NoMember(Location at, string type, string name) =>
        Make(at, $"cannot find member '{name}' in '{type}'");

    [Error(297, "expressions.members")]
    public static Diagnostic NoField(Location at, string type, string name) =>
        Make(at, $"cannot find field '{name}' in '{type}'");

    [Error(298, "expressions.members")]
    public static Diagnostic MethodNotField(Location at, string method, string name) =>
        Make(at, $"method '{method}' is used as a field").Help($"call it with '{name}(...)'");

    [Error(307, "expressions.arithmetic.operands")]
    public static Diagnostic UnaryOperatorNotApplicable(Location at, string op, string type, NullableOperand nullable = NullableOperand.None) =>
        Make(at, $"operator '{op}' cannot be applied to '{type}'").WithNullableHelp(nullable);

    [Error(308, "expressions.arithmetic.operands")]
    public static Diagnostic BinaryOperatorNotApplicable(Location at, string op, string left, string right, NullableOperand nullable = NullableOperand.None, bool numeric = false) =>
        numeric ? Make(at, $"operator '{op}' cannot be applied to '{left}' and '{right}'").Help("use an explicit cast to convert one side")
        : Make(at, $"operator '{op}' cannot be applied to '{left}' and '{right}'").WithNullableHelp(nullable);

    [Error(309, "expressions.shifts")]
    public static Diagnostic ShiftCountOutOfRange(Location at, string op, long count, string type, int width) =>
        width < 32
            ? Make(at, $"shift count {count} is out of range for '{type}'").Note($"a '{type}' can shift by 0 to {width - 1}")
                .Help($"widen it first, as in '(int)x {op} {count}'")
            : Make(at, $"shift count {count} is out of range for '{type}'").Note($"a '{type}' can shift by 0 to {width - 1}");

    [Error(310, "expressions.comparison")]
    public static Diagnostic FieldNotEquatable(Location at, string op, string type, string owner, string field, string fieldType) =>
        Make(at, $"operator '{op}' cannot be applied to '{type}'")
            .Note($"field '{owner}.{field}' of type '{fieldType}' does not support '=='");

    [Error(311, "expressions.comparison")]
    public static Diagnostic TypeNotEquatable(Location at, string op, string left, string right, string type) =>
        Make(at, $"operator '{op}' cannot be applied to '{left}' and '{right}'").Note($"'{type}' does not support '=='");

    [Error(312, "expressions.null")]
    public static Diagnostic NonNullOnNonNullable(Location at, string type) =>
        Make(at, $"'!' is used on a value of non-nullable type '{type}'").Help("remove the '!'");

    [Error(313, "expressions.null")]
    public static Diagnostic CoalesceOnReference(Location at, string type) =>
        Make(at, $"'??' cannot be applied to '{type}'").Help("check it for null with 'if'");

    [Error(314, "expressions.null")]
    public static Diagnostic CoalesceOnNonNullable(Location at, string type) =>
        Make(at, $"'??' is used on a value of non-nullable type '{type}'");

    [Error(315, "expressions.null")]
    public static Diagnostic CoalesceNonEscaping(Location at) =>
        Make(at, "'??' cannot be applied to a nullable non-escaping function").Help("check it for null with 'if'");

    [Error(319, "expressions.conditional")]
    public static Diagnostic ConditionalTypes(Location at, string whenTrue, string whenFalse) =>
        Make(at, $"branches of '?:' have incompatible types '{whenTrue}' and '{whenFalse}'");

    [Error(320, "expressions.conditional")]
    public static Diagnostic ConditionalView(Location at, string type) =>
        Make(at, $"'?:' cannot choose between two values of type '{type}'")
            .Note("the result would view either").Help("use 'if' with a variable for each");

    [Error(321, "expressions.conditional")]
    public static Diagnostic ConditionalReference(Location at) =>
        Make(at, "'?:' cannot choose between references").Help("use 'if' with a variable for each");

    [Error(332, "expressions.try")]
    public static Diagnostic TryOnNonResult(Location at, string type) =>
        Make(at, $"operand of 'try': expected a 'Result', found '{type}'");

    [Error(333, "expressions.try")]
    public static Diagnostic TryInInferredLambda(Location at) =>
        Make(at, "'try' cannot be used in a lambda whose return type is inferred").Help("specify the call's type arguments");

    [Error(334, "expressions.try")]
    public static Diagnostic TryOutsideResult(Location at) =>
        Make(at, "'try' can only be used in a function that returns a 'Result'");

    [Error(335, "expressions.try")]
    public static Diagnostic TryErrorType(Location at, string function, string expected, string found) =>
        Make(at, $"error type of 'try': expected '{expected}', found '{found}'")
            .Note($"{function} returns errors of type '{expected}'").Help("convert the error first");

    [Error(299, "expressions.calls")]
    public static Diagnostic NotCallable(Location at, string text, string type) =>
        Make(at, $"cannot call '{text}' of non-function type '{type}'");

    [Error(300, "expressions.calls")]
    public static Diagnostic NoMethod(Location at, string type, string name, Location? extension = null) =>
        extension is null ? Make(at, $"cannot find method '{name}' in '{type}'")
            : Make(at, $"cannot find method '{name}' in '{type}'").Note(extension, $"no extension '{name}' accepts a receiver of type '{type}'");

    [Error(301, "expressions.calls")]
    public static Diagnostic FieldNotMethod(Location at, string field) =>
        Make(at, $"'{field}' is a field, not a method");

    [Error(302, "expressions.calls")]
    public static Diagnostic InstanceMethodThroughType(Location at, string method, string type) =>
        Make(at, $"instance method '{method}' cannot be called through its type").Help($"call it on a value of type '{type}'");

    [Error(303, "expressions.calls")]
    public static Diagnostic StaticMethodThroughValue(Location at, string name, string type) =>
        Make(at, $"static method '{name}' cannot be called through a value").Help($"call it through its type, as in '{type}.{name}(...)'");

    [Error(304, "expressions.calls")]
    public static Diagnostic UnsafeCall(Location at, string what) =>
        Make(at, $"{what} is unsafe and can only be called in 'unsafe' code");

    [Error(287, "expressions.construction")]
    public static Diagnostic ClassWithoutNew(Location at, string name) =>
        Make(at, $"class '{name}' cannot be created without 'new'").Help($"write 'new {name}(...)'");

    [Error(288, "expressions.construction")]
    public static Diagnostic EnumConstructed(Location at, string name, string? variant) =>
        Make(at, $"enum '{name}' cannot be constructed")
            .Help(variant is null ? "create one of its variants" : $"create one of its variants, as in '{name}.{variant}'");

    [Error(289, "expressions.construction")]
    public static Diagnostic StructWithNew(Location at, string name) =>
        Make(at, $"struct '{name}' cannot be created with 'new'").Help($"write '{name}(...)'");

    [Error(290, "expressions.construction")]
    public static Diagnostic EnumWithNew(Location at, string name, string? variant) =>
        Make(at, $"enum '{name}' cannot be created with 'new'")
            .Help(variant is null ? "create one of its variants" : $"create one of its variants, as in '{name}.{variant}'");

    [Error(291, "expressions.construction")]
    public static Diagnostic NotNewable(Location at, string type) =>
        Make(at, $"'{type}' cannot be created with 'new'");

    [Error(316, "expressions.null")]
    public static Diagnostic ConditionalOnNonNullable(Location at, string type) =>
        Make(at, $"'?.' is used on a value of non-nullable type '{type}'").Help("use '.'");

    [Error(317, "expressions.null")]
    public static Diagnostic ConditionalMutCall(Location at, string method, string text) =>
        Make(at, $"'?.' would call '{method}', which changes its receiver, on a copy of '{text}'")
            .Note("the copy's changes would be lost").Help($"check it for null, then call '{method}' on a variable");

    [Error(318, "expressions.null")]
    public static Diagnostic ConditionalNotNullable(Location at, string type) =>
        Make(at, $"'?.' cannot produce a nullable '{type}'");

    [Error(305, "expressions.indexing")]
    public static Diagnostic IndexCount(Location at, string type) =>
        Make(at, $"indexing '{type}' takes a single index").Help("write 'a[i]'");

    [Error(306, "expressions.indexing")]
    public static Diagnostic NotIndexable(Location at, string type) =>
        Make(at, $"'{type}' cannot be indexed");

    [Error(346, "statements.assignment")]
    public static Diagnostic ElementOfTemporary(Location at) =>
        Make(at, "cannot assign to an element of a temporary value").Temporary();

    [Error(372, "statements.return")]
    public static Diagnostic MissingReturnValue(Location at, string? function, string type) =>
        Make(at, function is null ? $"lambda must return a value of type '{type}'" : $"'{function}' must return a value of type '{type}'");

    [Error(373, "statements.return")]
    public static Diagnostic ReturnFromConstructor(Location at, bool isConstructor) =>
        Make(at, isConstructor ? "cannot return a value from a constructor" : "cannot return a value from a destructor");

    [Error(374, "statements.return")]
    public static Diagnostic ReturnFromVoid(Location at, string? function) =>
        Make(at, function is null ? "cannot return a value from a 'void' lambda" : $"cannot return a value from 'void' function '{function}'");

    [Error(438, "statements.loops")]
    public static Diagnostic ForWithoutCondition(Location at) =>
        Make(at, "'for' needs a condition").Help("write 'loop' for a loop with no condition");

    [Error(371, "statements.jumps")]
    public static Diagnostic JumpOutsideLoop(Location at, string keyword) =>
        Make(at, $"'{keyword}' can only be used inside a loop");

    [Error(343, "statements.expressions")]
    public static Diagnostic NotAStatement(Location at) =>
        Make(at, "only calls can be used as statements");

    [Error(344, "statements.expressions.discard")]
    public static Diagnostic DiscardVoid(Location at) =>
        Make(at, "'_ =' has no value to discard").Help("call it without '_ ='");

    [Error(347, "statements.assignment")]
    public static Diagnostic IncDecNonNumeric(Location at, string op, NullableOperand nullable) =>
        Make(at, $"'{op}' can only be applied to a numeric variable").WithNullableHelp(nullable);

    [Error(348, "statements.assignment")]
    public static Diagnostic FieldOfTemporary(Location at) =>
        Make(at, "cannot assign to a field of a temporary value").Temporary();

    [Error(349, "statements.assignment")]
    public static Diagnostic ElementOfTemporaryArray(Location at) =>
        Make(at, "cannot assign to an element of a temporary array").Temporary();

    [Error(350, "statements.assignment")]
    public static Diagnostic NotAssignable(Location at) =>
        Make(at, "cannot assign to this expression");

    [Error(351, "statements.assignment")]
    public static Diagnostic AssignReadOnly(Location at, string text, ReadOnlyReason reason) =>
        Make(at, $"cannot assign to '{text}'").Because(reason);

    [Error(359, "statements.for-each.ranges")]
    public static Diagnostic RangeIndex(Location at) =>
        Make(at, "range has no index").Help("loop over its values alone");

    [Error(360, "statements.for-each.ranges")]
    public static Diagnostic RangeBorrowed(Location at, string keyword) =>
        Make(at, $"values of a range cannot be borrowed with '{keyword}'");

    [Error(361, "statements.for-each.ranges")]
    public static Diagnostic RangeBounds(Location at, string start, string end) =>
        Make(at, $"range bounds must be integers, found '{start}' and '{end}'");

    [Error(362, "statements.for-each.ranges")]
    public static Diagnostic RangeVariableType(Location at, string type) =>
        Make(at, $"range variable type: expected an integer type, found '{type}'");

    [Error(352, "statements.for-each")]
    public static Diagnostic LoopInsideNullable(Location at, string variable) =>
        Make(at, $"cannot loop over elements inside nullable '{variable}'").InsideNullable(variable);

    [Error(353, "statements.for-each")]
    public static Diagnostic NotIterable(Location at, string type) =>
        Make(at, $"cannot loop over '{type}'");

    [Error(354, "statements.for-each")]
    public static Diagnostic NullableLoopVariable(Location at, string keyword) =>
        Make(at, $"loop variable cannot be '{keyword}?'").Note("every element exists").Help($"write '{keyword}'");

    [Error(355, "statements.for-each")]
    public static Diagnostic LoopRefOverReadOnly(Location at, string text, ReadOnlyReason reason) =>
        Make(at, $"cannot loop over '{text}' by 'ref'").Because(reason).Help("loop with 'in'");

    [Error(356, "statements.for-each")]
    public static Diagnostic LoopRefOverTemporary(Location at) =>
        Make(at, "cannot loop over a temporary array by 'ref'").Temporary();

    [Error(357, "statements.for-each")]
    public static Diagnostic StringCharactersBorrowed(Location at, string keyword) =>
        Make(at, $"characters of a string cannot be borrowed with '{keyword}'").Note("they are decoded from its bytes");

    [Error(363, "statements.for-each.indices")]
    public static Diagnostic IndexBorrowed(Location at) =>
        Make(at, "loop index cannot be borrowed").Note("it is a copy of the position");

    [Error(364, "statements.for-each.indices")]
    public static Diagnostic IndexType(Location at, string type) =>
        Make(at, $"loop index type: expected 'int', found '{type}'");

    [Error(483, "statements.for-each.iterators")]
    public static Diagnostic IteratorValueBorrowed(Location at, string keyword) =>
        Make(at, $"values of an iterator cannot be borrowed with '{keyword}'").Note("the iterator's 'current' returns a value");

    [Error(484, "statements.for-each.iterators")]
    public static Diagnostic IteratorTypeArguments(Location at, string type, string first, string second) =>
        Make(at, $"cannot loop over '{type}', which implements both '{first}' and '{second}'");

    [Error(486, "statements.for-each.iterators")]
    public static Diagnostic InIteratorElementRef(Location at) =>
        Make(at, "elements of an 'InIterator' cannot be borrowed with 'ref'").Note("the iterator's 'current' returns an 'in' reference, which cannot change the element");

    [Error(485, "statements.for-each.iterators")]
    public static Diagnostic IteratorAndSpan(Location at, string type, string text) =>
        Make(at, $"cannot loop over '{type}', which is an iterator and converts to a span")
            .Help($"loop over '{text}.asReadOnlySpan()' for the elements of the span");

    [Error(358, "statements.for-each")]
    public static Diagnostic LoopOverTemporaryView(Location at) =>
        Make(at, "cannot loop over the elements of a temporary value").Help("store the value in a variable first");

    [Error(338, "expressions.interpolation.format")]
    public static Diagnostic FormatSpecifierSyntax(Location at, string format) =>
        Make(at, $"'{format}' is not a format specifier").Help("write a letter and an optional precision of up to two digits, as in 'F2'");

    [Error(339, "expressions.interpolation.format")]
    public static Diagnostic UnknownFormatSpecifier(Location at, char kind) =>
        Make(at, $"cannot find format specifier '{kind}'").Note("the specifiers are D, X, and B for integers, and F and E for floating point");

    [Error(340, "expressions.interpolation.format")]
    public static Diagnostic FormatSpecifierType(Location at, string format, bool integers, string type) =>
        Make(at, $"format specifier '{format}' cannot format a value of type '{type}'")
            .Note(integers ? "it applies to integers" : "it applies to 'float' and 'double'");

    [Error(336, "expressions.interpolation")]
    public static Diagnostic NotPrintable(Location at, string type, string? hasToString) =>
        hasToString is null
            ? Make(at, $"cannot interpolate a value of type '{type}'").Note($"'{type}' does not implement 'Printable'")
            : Make(at, $"cannot interpolate a value of type '{type}'").Note($"'{type}' does not implement 'Printable'")
                .Help($"declare 'implements Printable' on '{hasToString}' to use its 'toString()'");
}
