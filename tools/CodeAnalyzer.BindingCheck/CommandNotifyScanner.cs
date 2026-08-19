// System.IO explicitly: a UseWPF project's implicit usings do not include it.
using System.IO;
using System.Text.RegularExpressions;

namespace CodeAnalyzer.BindingCheck;

/// <summary>One command that shares a CanExecute predicate but is never re-asked.</summary>
public sealed record UnnotifiedCommand(string File, string Command, string Predicate, IReadOnlyList<string> Siblings);

/// <summary>
/// Finds the button that is right about everything except whether it is enabled.
/// <para>
/// A <c>[RelayCommand(CanExecute = nameof(P))]</c> command re-asks P only when something
/// calls its <c>NotifyCanExecuteChanged</c>. When several commands share one predicate,
/// the notify calls are written by hand at each place the predicate's inputs change — so
/// adding a fourth command to a trio is one edit and three omissions, and the new button
/// sits permanently greyed while its siblings work. WPF says nothing: a disabled button
/// is not an error, it is a button.
/// </para>
/// <para>
/// The rule is deliberately narrow, and only fires where the file contradicts itself:
/// the predicate is shared, some sharers are notified, and this one never is. A predicate
/// nobody notifies is a constant by intent and is left alone.
/// </para>
/// </summary>
public static class CommandNotifyScanner
{
    // [RelayCommand(CanExecute = nameof(Pred))], any further attributes, then the method name.
    private static readonly Regex Commands = new(
        @"\[RelayCommand\(CanExecute\s*=\s*nameof\((?<pred>\w+)\)\)\]\s*(?:\[[^\]]*\]\s*)*"
        + @"(?:private|public|internal|protected)[^\n(]*?(?<method>\w+)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex Notified = new(@"(?<command>\w+Command)\.NotifyCanExecuteChanged\(\)", RegexOptions.Compiled);

    private static readonly Regex NotifiedByAttribute = new(
        @"NotifyCanExecuteChangedFor\(nameof\((?<command>\w+)\)\)", RegexOptions.Compiled);

    public static IReadOnlyList<UnnotifiedCommand> Scan(string viewModelDirectory, string repoRoot)
    {
        var findings = new List<UnnotifiedCommand>();

        var files = Directory
            .GetFiles(viewModelDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f, StringComparer.Ordinal);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);

            var byPredicate = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (Match match in Commands.Matches(text))
            {
                var command = CommandNameFor(match.Groups["method"].Value);
                if (!byPredicate.TryGetValue(match.Groups["pred"].Value, out var list))
                {
                    byPredicate[match.Groups["pred"].Value] = list = [];
                }

                list.Add(command);
            }

            var notified = Notified.Matches(text).Select(m => m.Groups["command"].Value)
                .Concat(NotifiedByAttribute.Matches(text).Select(m => m.Groups["command"].Value))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var (predicate, commands) in byPredicate)
            {
                if (commands.Count < 2)
                {
                    continue;
                }

                var told = commands.Where(notified.Contains).ToList();
                if (told.Count == 0)
                {
                    // Nobody re-asks this predicate. That is a decision, not an oversight.
                    continue;
                }

                foreach (var forgotten in commands.Where(c => !notified.Contains(c)))
                {
                    findings.Add(new UnnotifiedCommand(
                        Path.GetRelativePath(repoRoot, file).Replace('\\', '/'), forgotten, predicate, told));
                }
            }
        }

        return findings;
    }

    /// <summary>The generator's naming rule: FooAsync() becomes FooCommand, Foo() becomes FooCommand.</summary>
    private static string CommandNameFor(string method) =>
        (method.EndsWith("Async", StringComparison.Ordinal) ? method[..^"Async".Length] : method) + "Command";
}
