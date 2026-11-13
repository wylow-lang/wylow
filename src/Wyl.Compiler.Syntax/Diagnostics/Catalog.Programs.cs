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

    [Error(531, "programs.manifest.dependencies")]
    public static Diagnostic DependencyNotPackage(Location at, string path) =>
        Make(at, $"'{path}' does not hold a package").Note($"a package's directory holds a '{Manifest.FileName}'");

    [Error(532, "programs.manifest.dependencies")]
    public static Diagnostic DependencyNameMismatch(Location at, string key, string name) =>
        Make(at, $"dependency '{key}' names package '{name}'")
            .Help($"write the entry as '{name} {{ ... }}', and import it as 'import {name} as {key};' where another name is needed");

    [Error(533, "programs.manifest.dependencies")]
    public static Diagnostic DependencyNotLibrary(Location at, string name, Location manifest) =>
        Make(at, $"dependency '{name}' is not a library").Note(manifest, "the package's manifest has no 'kind = \"library\";'");

    [Error(534, "programs.manifest.dependencies")]
    public static Diagnostic DependencyCycle(Location at, IReadOnlyList<string> cycle) =>
        Make(at, $"dependencies form a cycle: {string.Join(" -> ", cycle.Select(n => $"'{n}'"))}");

    [Error(535, "programs.manifest.dependencies")]
    public static Diagnostic DuplicatePackageName(Location at, string name, Location other) =>
        Make(at, $"another package of the program is named '{name}'").Note(other, "the other package is named here");

    [Error(536, "programs.manifest.dependencies")]
    public static Diagnostic DependencyModuleName(Location at, string name) =>
        Make(at, $"dependency '{name}' has the name of a module directly in 'src/'")
            .Note($"a path that starts with '{name}' could mean either one");

    [Error(537, "programs.manifest.versions")]
    public static Diagnostic UnsatisfiedVersion(Location at, string name, PackageVersion version, string stated, Location given) =>
        Make(at, $"library '{name}' has version '{version}', which does not satisfy '{stated}'").Note(given, "the library's version is given here");

    [Error(538, "programs.manifest.dependencies")]
    public static Diagnostic ConflictingDefine(Location at, string name, string value, string otherValue, string other, Location otherAt) =>
        Make(at, $"'{name}' is defined as '{value}', and package '{other}' defines it as '{otherValue}'")
            .Note(otherAt, $"package '{other}' defines '{name}' here")
            .Note("the generated program compiles with the definitions of every package");

    [Error(539, "programs.manifest.dependencies")]
    public static Diagnostic ConflictingCopy(Location at, string fileName, string other, Location otherAt) =>
        Make(at, $"'copy' names a file '{fileName}', and package '{other}' copies another file of that name")
            .Note(otherAt, $"package '{other}' names its file here")
            .Note("the files that every package copies go into the executable's directory");

    [Error(526, "programs.manifest")]
    public static Diagnostic UnknownPackageKind(Location at, string kind) =>
        Make(at, $"package kind '{kind}' is neither 'program' nor 'library'");

    [Error(527, "programs.manifest")]
    public static Diagnostic LibraryWithoutVersion(Location at) =>
        Make(at, "library does not give its 'version'").Help("add 'version = \"0.1.0\";'");

    [Error(528, "programs.manifest.versions")]
    public static Diagnostic MalformedVersion(Location at, string version, string form) =>
        Make(at, $"version '{version}' does not have the form {form}")
            .Note("each number is written in decimal digits, with no leading zero except in '0' itself");

    [Error(518, "programs.manifest")]
    public static Diagnostic MissingNativePath(Location at, string key, string path, string kind) =>
        Make(at, $"'{key}' names '{path}', which is not a {kind}");

    [Error(553, "programs.manifest")]
    public static Diagnostic FetchHashMalformed(Location at, string hash) =>
        Make(at, $"'{hash}' is not a SHA-256 hash").Help("write 64 lowercase hexadecimal digits");

    [Error(554, "programs.manifest")]
    public static Diagnostic FetchFiles(Location at) =>
        Make(at, "'files' must list one or more relative paths inside the archive")
            .Help("write a path such as 'sqlite-amalgamation-3540100/sqlite3.h'");

    [Error(555, "programs.manifest")]
    public static Diagnostic FetchUrlScheme(Location at, string url) =>
        Make(at, $"'{url}' is not an 'https' URL").Help("write an absolute URL whose scheme is 'https'");

    [Error(530, "programs.packages")]
    public static Diagnostic LibraryNotRunnable(Location at, string package) =>
        Make(at, $"cannot build or run library '{package}'")
            .Note("a library has no entry point, and its code runs only as part of a program that depends on it")
            .Help("check the library with 'wyl check'");

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
        Make(at, $"'pub({scope})' is not a visibility")
            .Help(scope is "module" or "package" ? $"write '{scope}'" : "write 'module' or 'package' without parentheses");

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

    [Warning(480, "programs.paths", "unqualified-call")]
    public static Diagnostic UnqualifiedCall(Location at, string name, string path) =>
        Make(at, $"function '{name}' is called without its module").Help($"write '{path}'");

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

    [Error(437, "programs.builtin-names")]
    public static Diagnostic LanguageNameImported(Location at, string name) =>
        Make(at, $"cannot import a declaration as '{name}'").Note($"'{name}' is one of the types the language is built on, which every file sees");

    [Error(58, "programs.entry")]
    public static Diagnostic NoMain(Location at, string? package) =>
        package is null
            ? Make(at, "program has no 'main' function")
            : Make(at, $"package '{package}' has no 'main' function in its root module")
                .Help("if the package is a library, add 'kind = \"library\";' to its manifest");

    [Error(529, "programs.entry")]
    public static Diagnostic LibraryMain(Location at, string package) =>
        Make(at, $"library '{package}' has a 'main' function in its root module").Note("a library has no entry point");

    [Error(59, "programs.entry")]
    public static Diagnostic MainParameters(Location at, bool typeParameters) => typeParameters
        ? Make(at, "'main' cannot have type parameters")
        : Make(at, "'main' cannot take these parameters").Help("write 'main()' or 'main(String[] args)'");

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
