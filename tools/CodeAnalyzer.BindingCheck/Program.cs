// System.IO explicitly: a UseWPF project's implicit usings do not include it.
using System.IO;
using System.Reflection;
using CodeAnalyzer.BindingCheck;

// codeanalyzer-bindingcheck [path-to-scan]
//
// WPF reports a broken binding path as a runtime trace line and carries on, so a typo in
// a {Binding} or a Click handler is invisible until somebody clicks the thing. Three
// milestones in this repo's history each rebuilt this check as a scratch tool and threw
// it away; this is the same check, kept.
//
// Two halves with different strengths, and the difference is stated in the output rather
// than blurred:
//   Handlers are checked soundly. x:Class names exactly one type, and either it has the
//   method or it does not.
//   Bindings are checked against every view model and item type in the assembly at once,
//   and pass if any of them carries the path. That catches the error worth catching — a
//   name that exists nowhere — while not false-positiving on the row templates, whose
//   real data context is an item type the file never names. It will not catch a path
//   that is valid on some other type than the one actually in play.

var root = FindRepoRoot();
var selfTest = args.Contains("--selftest");
var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();

var scanPath = selfTest
    ? Path.Combine(root, "tools", "CodeAnalyzer.BindingCheck", "selftest")
    : positional.Count > 0
        ? Path.GetFullPath(positional[0])
        : Path.Combine(root, "src", "CodeAnalyzer.App");

if (!Directory.Exists(scanPath))
{
    Console.Error.WriteLine($"{scanPath} is not a directory");
    return 2;
}

var assembly = typeof(CodeAnalyzer.App.ViewModels.MainViewModel).Assembly;
var candidates = CandidateContextTypes(assembly);

var files = Directory
    .GetFiles(scanPath, "*.xaml", SearchOption.AllDirectories)
    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
    .OrderBy(f => f, StringComparer.Ordinal)
    .ToList();

var resolver = new PathResolver(candidates);
var results = new List<CheckResult>();
var skipped = 0;

var unreadable = new List<string>();

foreach (var file in files)
{
    XamlFileUses scanned;
    try
    {
        scanned = BindingScanner.Scan(file, root);
    }
    catch (System.Xml.XmlException e)
    {
        // A file this tool cannot read is a finding, not a crash. Falling over on the
        // first bad file would also hide every file after it.
        unreadable.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')} — {e.Message}");
        continue;
    }

    var codeBehind = scanned.CodeBehindType is null
        ? null
        : assembly.GetType(scanned.CodeBehindType);

    foreach (var use in scanned.Uses)
    {
        var result = resolver.Check(use, codeBehind);
        if (result.Outcome == CheckOutcome.NoContext)
        {
            skipped++;
            continue;
        }

        results.Add(result);
    }
}

var unresolved = results.Where(r => r.Outcome == CheckOutcome.Unresolved).ToList();
var bindings = results.Count(r => r.Use.Kind == XamlUseKind.Binding);
var handlers = results.Count(r => r.Use.Kind == XamlUseKind.Handler);

Console.WriteLine(
    $"binding check: {files.Count} XAML files, {bindings} binding paths, {handlers} handlers, "
    + $"{candidates.Count} candidate context types");

if (skipped > 0)
{
    // Never silent. A checker that quietly ignored what it could not read would report a
    // clean run over a file it never understood.
    Console.WriteLine($"  {skipped} not checked (no context type to check against)");
}

foreach (var problem in unreadable)
{
    Console.WriteLine($"  unreadable: {problem}");
}

foreach (var result in unresolved)
{
    var where = result.Use.Kind == XamlUseKind.Handler
        ? $"not a method on {result.On}"
        : "on no view model or item type in the assembly";

    Console.WriteLine(
        $"  {result.Use.File}:{result.Use.Line}  {result.Use.Attribute}=\"{result.Use.Path}\" — {where}");
}

if (selfTest)
{
    // The planted errors, and nothing else. Both halves matter: missing one means the
    // checker has gone blind, and reporting a fifth means it has started inventing.
    string[] expected = ["Search.Qeury", "Detail.Ovrloads.Count", "FocusSymblCommand", "OnNoSuchHandler"];
    var found = unresolved.Select(u => u.Use.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();
    var wanted = expected.OrderBy(p => p, StringComparer.Ordinal).ToList();

    if (found.SequenceEqual(wanted, StringComparer.Ordinal))
    {
        Console.WriteLine($"  selftest passed — found exactly the {wanted.Count} planted errors");
        return 0;
    }

    Console.Error.WriteLine($"  selftest FAILED");
    Console.Error.WriteLine($"    expected: {string.Join(", ", wanted)}");
    Console.Error.WriteLine($"    found:    {string.Join(", ", found)}");
    return 1;
}

if (unresolved.Count == 0)
{
    Console.WriteLine("  all resolved");
    return 0;
}

Console.WriteLine($"  {unresolved.Count} unresolved");
return 1;

static List<Type> CandidateContextTypes(Assembly assembly) =>
    assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false })
        .Where(t => t.Namespace is not null
                    && (t.Namespace.Contains(".ViewModels", StringComparison.Ordinal)
                        || t.Namespace.EndsWith(".Views", StringComparison.Ordinal)))
        .OrderBy(t => t.FullName, StringComparer.Ordinal)
        .ToList();

static string FindRepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodeAnalyzer.sln")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? Directory.GetCurrentDirectory();
}
