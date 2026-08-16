using System.Reflection;

namespace CodeAnalyzer.BindingCheck;

/// <summary>What happened when a name was looked up.</summary>
public enum CheckOutcome
{
    /// <summary>The whole path walked, on at least one candidate type.</summary>
    Resolved,

    /// <summary>No candidate type carries this path. This is the finding.</summary>
    Unresolved,

    /// <summary>Nothing to check against — reported as a count, never as a pass.</summary>
    NoContext,
}

public sealed record CheckResult(XamlNameUse Use, CheckOutcome Outcome, string? On);

/// <summary>
/// Walks a binding path over candidate types by reflection.
/// <para>
/// It runs against the <em>compiled</em> assembly on purpose. Half this app's bindable
/// surface does not exist in the source: <c>[ObservableProperty] private string _query</c>
/// becomes <c>Query</c>, and <c>[RelayCommand] private void Search()</c> becomes
/// <c>SearchCommand</c>, both at build time. A checker reading the .cs files would report
/// every one of those as missing.
/// </para>
/// </summary>
public sealed class PathResolver(IReadOnlyList<Type> candidates)
{
    private const BindingFlags Members =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>
    /// True when the path walks all the way down from <paramref name="root"/>.
    /// A path stops walking at a type this tool cannot see through — an interface's
    /// implementation, a <c>dynamic</c> — and those stop being checked rather than
    /// becoming findings.
    /// </summary>
    public static bool Walks(Type root, string path)
    {
        var current = root;

        foreach (var segment in path.Split('.'))
        {
            var member = FindMember(current, segment);
            if (member is null)
            {
                return false;
            }

            current = member switch
            {
                PropertyInfo property => property.PropertyType,
                FieldInfo field => field.FieldType,
                MethodInfo method => method.ReturnType,
                _ => typeof(object),
            };

            // object tells us nothing about what comes next, so a longer path through it
            // is unverifiable rather than wrong.
            if (current == typeof(object))
            {
                return true;
            }
        }

        return true;
    }

    private static MemberInfo? FindMember(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var member = (MemberInfo?)current.GetProperty(name, Members)
                ?? (MemberInfo?)current.GetField(name, Members)
                ?? current.GetMethod(name, Members);

            if (member is not null)
            {
                return member;
            }

            // A collection's items are reached through the element type, which is what a
            // path like Overloads.Count is really walking on the way past.
            foreach (var contract in current.GetInterfaces())
            {
                var found = (MemberInfo?)contract.GetProperty(name, Members)
                    ?? (MemberInfo?)contract.GetMethod(name, Members);

                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks one use. A binding passes when <em>any</em> candidate type carries it; a
    /// handler is checked against the one type its file names in x:Class, which is the
    /// only sound half of this tool.
    /// </summary>
    public CheckResult Check(XamlNameUse use, Type? codeBehind)
    {
        if (use.Kind == XamlUseKind.Handler)
        {
            if (codeBehind is null)
            {
                return new CheckResult(use, CheckOutcome.NoContext, null);
            }

            return FindMember(codeBehind, use.Path) is not null
                ? new CheckResult(use, CheckOutcome.Resolved, codeBehind.Name)
                : new CheckResult(use, CheckOutcome.Unresolved, codeBehind.Name);
        }

        if (candidates.Count == 0)
        {
            return new CheckResult(use, CheckOutcome.NoContext, null);
        }

        foreach (var candidate in candidates)
        {
            if (Walks(candidate, use.Path))
            {
                return new CheckResult(use, CheckOutcome.Resolved, candidate.Name);
            }
        }

        return new CheckResult(use, CheckOutcome.Unresolved, null);
    }
}
