using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Diagnostics;

// Chapter 3, programs, packages, and modules.
public static partial class Catalog
{
    [Error(21, "programs.manifest")]
    public static Diagnostic ManifestExpected(Location at, string what) =>
        Make(at, $"expected {what}");

    [Error(22, "programs.manifest")]
    public static Diagnostic InterpolatedManifestString(Location at) =>
        Make(at, "manifest strings cannot be interpolated");

    [Error(23, "programs.manifest")]
    public static Diagnostic UnknownManifestKey(Location at, string key, string keys) =>
        Make(at, $"'{key}' is not a key here").Note($"the keys here are {keys}");

    [Error(24, "programs.manifest")]
    public static Diagnostic DuplicateManifestKey(Location at, string key, Location previous) =>
        Make(at, $"'{key}' is already given").Note(previous, "previous entry here");

    [Error(25, "programs.manifest")]
    public static Diagnostic ManifestValueKind(Location at, string key, string expected, string example) =>
        Make(at, $"'{key}' takes {expected}").Help($"write '{example}'");

    [Error(26, "programs.manifest")]
    public static Diagnostic ManifestWithoutName(Location at) =>
        Make(at, "manifest does not give the package's 'name'").Help("add 'name = \"my_app\";'");

    [Error(27, "programs.manifest")]
    public static Diagnostic PackageNameNotIdentifier(Location at, string name) =>
        Make(at, $"package name '{name}' is not an identifier");

    [Error(28, "programs.manifest")]
    public static Diagnostic ReservedPackageName(Location at) =>
        Make(at, "package name 'std' is reserved for the standard library");

    [Error(29, "programs.manifest")]
    public static Diagnostic DependencyNotBlock(Location at, string name) =>
        Make(at, $"dependency '{name}' is not a block").Help($"write '{name} {{ path = \"../{name}\"; }}'");

    [Error(30, "programs.manifest")]
    public static Diagnostic DependencyWithoutPath(Location at, string name) =>
        Make(at, $"dependency '{name}' has no 'path'");

    [Error(31, "programs.manifest")]
    public static Diagnostic UnusableManifestKey(Location at, string key) =>
        Make(at, $"manifest key '{key}' cannot be used");

    [Error(19, "programs.packages")]
    public static Diagnostic MissingSourceDirectory(Location at) =>
        Make(at, "package has no 'src/' directory");

    [Error(20, "programs.packages")]
    public static Diagnostic NoSourceFiles(Location at) =>
        Make(at, "'src/' contains no '.wyl' files");

    [Error(32, "programs.modules")]
    public static Diagnostic DirectoryNotModuleName(Location at, string name) =>
        Make(at, $"directory '{name}' contains source files, but '{name}' is not an identifier")
            .Note("a directory under 'src/' is a module, and a module's name is an identifier");

    [Error(33, "programs.modules")]
    public static Diagnostic StdModuleName(Location at) =>
        Make(at, "top-level module cannot be named 'std'").Note("'std' names the standard library");

    [Error(35, "programs.imports")]
    public static Diagnostic ImportWithAnnotations(Location at) =>
        Make(at, "imports cannot have annotations");

    [Error(36, "programs.imports")]
    public static Diagnostic ImportAfterDeclaration(Location at) =>
        Make(at, "imports must come before the file's declarations");

    [Error(37, "programs.imports")]
    public static Diagnostic ModifierOnImport(Location at, string modifier) =>
        Make(at, $"'{modifier}' cannot be used on an import");

    [Error(48, "programs.visibility")]
    public static Diagnostic DuplicateVisibility(Location at) =>
        Make(at, "declaration already has a visibility");

    [Error(49, "programs.visibility")]
    public static Diagnostic VisibilityNotFirst(Location at, string written, string next) =>
        Make(at, "visibility must come before other modifiers").Help($"write '{written} {next}'");

    [Error(50, "programs.visibility")]
    public static Diagnostic DuplicateModifier(Location at, string modifier) =>
        Make(at, $"modifier '{modifier}' is repeated");

    [Error(51, "programs.visibility")]
    public static Diagnostic NotAVisibility(Location at, string keyword) => keyword switch
    {
        "public" => Make(at, "'public' is not a visibility").Help("write 'pub'"),
        "private" => Make(at, "'private' is not a visibility")
            .Note("a declaration is private to its file unless marked 'pub'").Help("remove 'private'"),
        _ => Make(at, $"'{keyword}' is not a visibility")
            .Note("a subclass sees what its file and module see")
            .Help("write the 'pub' level that subclasses in other files need"),
    };

    [Error(52, "programs.visibility")]
    public static Diagnostic UnknownVisibilityScope(Location at, string scope) =>
        Make(at, $"'pub({scope})' is not a visibility").Help("write 'pub(module)' or 'pub(package)'");

    [Error(53, "programs.visibility")]
    public static Diagnostic VisibilityBlock(Location at) =>
        Make(at, "visibility cannot be given to a block of members").Help("write it on each member, as in 'pub int x;'");

    [Error(54, "programs.visibility")]
    public static Diagnostic NotVisible(Location at, string what, Visibility level, SourceText? source) =>
        Make(at, $"{what} is not visible here").Note($"it is visible {Reaching(level, source)}");

    [Error(34, "programs.names")]
    public static Diagnostic DuplicateName(Location at, string name, Location? previous, string? file, string? kind) =>
        (previous, file, kind) switch
        {
            ({ } p, _, { } k) => Make(at, $"'{name}' is already defined in this module").Note(p, $"previous definition here, as {k}"),
            ({ } p, _, null) => Make(at, $"'{name}' is already defined in this module").Note(p, "previous definition here"),
            (null, { } f, _) => Make(at, $"'{name}' is already defined in this module").Note($"it is defined in {f}"),
            _ => Make(at, $"'{name}' is already defined in this module"),
        };

    [Error(38, "programs.imports")]
    public static Diagnostic NoTopLevelModule(Location at, string name) =>
        Make(at, $"cannot find top-level module or package '{name}'");

    [Error(39, "programs.imports")]
    public static Diagnostic NoSubmodule(Location at, Text.Module module, string name) =>
        Make(at, $"cannot find module '{name}' in {ModuleName(module)}");

    [Error(40, "programs.imports")]
    public static Diagnostic DuplicateImport(Location at, string name) =>
        Make(at, $"'{name}' is already imported").Help("import one of them under another name with 'as'");

    [Error(41, "programs.imports")]
    public static Diagnostic DuplicateReExport(Location at, string name) =>
        Make(at, $"'{name}' is already re-exported by this module");

    [Error(42, "programs.imports")]
    public static Diagnostic NoRootName(Location at, string name) =>
        Make(at, $"cannot find '{name}' as a top-level module, a package, or a name of the root module");

    [Error(43, "programs.imports")]
    public static Diagnostic NoNameInModule(Location at, Text.Module module, string name) =>
        Make(at, $"cannot find '{name}' in {ModuleName(module)}");

    [Error(44, "programs.imports")]
    public static Diagnostic ImportTakesModuleName(Location at, string name) =>
        Make(at, $"'{name}' is already a name of this module").Help("import it under another name with 'as'");

    [Error(55, "programs.visibility")]
    public static Diagnostic ReExportReachesFarther(Location at, string name, Visibility level, SourceText? source) =>
        Make(at, $"'{name}' cannot be re-exported to reach farther than it is visible").Note($"it is visible {Reaching(level, source)}");

    [Error(46, "programs.paths")]
    public static Diagnostic ModuleNotImported(Location at, string name) =>
        Make(at, $"module '{name}' is not imported here")
            .Note("a path starts with a module the file imports or one of its own submodules").Help($"write 'import {name};'");

    [Error(47, "programs.paths")]
    public static Diagnostic NotAModule(Location at, string name) =>
        Make(at, $"cannot find module '{name}' in this scope");

    [Error(45, "programs.imports")]
    public static Diagnostic AmbiguousStarImport(Location at, string name, Text.Module first, Text.Module second) =>
        Make(at, $"'{name}' is ambiguous here").Note($"'*' imports it from both {ModuleName(first)} and {ModuleName(second)}")
            .Help("import the one meant by name");

    [Error(57, "programs.builtin-names")]
    public static Diagnostic LanguageTypeName(Location at, string name) =>
        Make(at, $"'{name}' cannot be declared").Note($"'{name}' is one of the types the language is built on, which every file sees");

    [Error(58, "programs.entry")]
    public static Diagnostic NoMain(Location at, string? package) =>
        Make(at, package is null ? "program has no 'main' function" : $"package '{package}' has no 'main' function in its root module");

    [Error(59, "programs.entry")]
    public static Diagnostic MainParameters(Location at) =>
        Make(at, "'main' cannot take parameters or type parameters");

    [Error(60, "programs.entry")]
    public static Diagnostic MainReturnType(Location at) =>
        Make(at, "'main' must return 'void' or 'int'");

    [Error(61, "programs.entry")]
    public static Diagnostic DuplicateMain(Location at, Location previous) =>
        Make(at, "program already has a 'main' function").Note(previous, "previous definition here");

    [Error(56, "programs.visibility.signatures")]
    public static Diagnostic SignatureLessVisible(Location at, string what, Visibility level, SourceText? source, string role, string type,
        string? named, Visibility namedLevel, SourceText? namedSource) =>
        named is null
            ? Make(at, $"{role} '{type}' is less visible than {what}")
                .Note($"{what} is visible {Reaching(level, source)}").Note($"'{type}' is visible {Reaching(namedLevel, namedSource)}")
            : Make(at, $"{role} '{type}' is less visible than {what}")
                .Note($"{what} is visible {Reaching(level, source)}").Note($"'{type}' names '{named}', which is visible {Reaching(namedLevel, namedSource)}");
}
