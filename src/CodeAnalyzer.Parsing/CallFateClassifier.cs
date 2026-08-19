using CodeAnalyzer.Core.Domain;
using TreeSitter;

namespace CodeAnalyzer.Parsing;

/// <summary>
/// Reads how a call's result is consumed off the call node's ancestors.
/// <para>
/// This is a parent walk rather than a set of query-pack patterns because fate is decided
/// by the <em>nearest deciding ancestor</em> above an arbitrary stack of transparent
/// wrappers — <c>var x = await (Foo());</c> is still an assignment — and a pattern per
/// (call shape × wrapper stack × context) combination would fight the same-offset
/// dedup in <see cref="TreeSitterAnalyzer"/> besides. The packs say what a node is; the
/// analyzer reads the structure around it, the same split the binding and qualified-name
/// rewrites already use.
/// </para>
/// <para>
/// Every claim errs toward silence: an ancestor shape outside the per-language tables —
/// a chained <c>Foo().Bar()</c>, a lambda body, a comprehension — answers
/// <see cref="ResultFate.Unknown"/>, never a guess. Languages without a table answer
/// Unknown for every call, which is what lets a new language degrade instead of lie.
/// </para>
/// </summary>
internal static class CallFateClassifier
{
    /// <summary>
    /// Hard cap on the ancestor walk. Fate deciders sit within a handful of levels of
    /// any real call; the cap is for machine-generated nesting, not for correctness.
    /// </summary>
    private const int MaxHops = 32;

    /// <summary>
    /// Classifies the call expression node's result consumption. The caller decides what
    /// is a call site; this answers for any node it is handed, and answers
    /// <see cref="ResultFate.Unknown"/> for a language it has no table for.
    /// </summary>
    internal static (ResultFate Fate, string? Name) Classify(string language, Node callNode) =>
        language switch
        {
            LanguageNames.CSharp => WalkCSharp(callNode),
            LanguageNames.Python => WalkPython(callNode),
            _ => (ResultFate.Unknown, null),
        };

    private static (ResultFate Fate, string? Name) WalkCSharp(Node callNode)
    {
        var child = callNode;
        for (var hops = 0; hops < MaxHops; hops++)
        {
            var parent = child.Parent;
            if (parent is null)
            {
                break;
            }

            switch (parent.Type)
            {
                // The vendored grammar has no equals_value_clause: a declarator holds its
                // initializer as a direct child, so reaching one from below IS the
                // initializer position.
                case "variable_declarator":
                    return (ResultFate.Assigned, parent.GetChildForField("name")?.Text);

                case "assignment_expression":
                    return AssignedTo(parent, child);

                case "return_statement":
                case "arrow_expression_clause":
                case "yield_statement":
                    return (ResultFate.Returned, null);

                case "argument":
                    return (ResultFate.PassedAsArgument, null);

                case "if_statement":
                case "while_statement":
                case "do_statement":
                case "for_statement":
                    return Is(parent.GetChildForField("condition"), child)
                        ? (ResultFate.Tested, null)
                        : (ResultFate.Unknown, null);

                case "switch_statement":
                    return Is(parent.GetChildForField("value"), child)
                        ? (ResultFate.Tested, null)
                        : (ResultFate.Unknown, null);

                // The governed expression is the first named child; an arm sits inside
                // switch_expression_arm and never reaches here directly.
                case "switch_expression":
                    return Is(parent.FirstNamedChild, child)
                        ? (ResultFate.Tested, null)
                        : (ResultFate.Unknown, null);

                // Only the condition is a test; a branch value flows onward —
                // `var x = flag ? Foo() : 0;` assigns.
                case "conditional_expression":
                    if (Is(parent.GetChildForField("condition"), child))
                    {
                        return (ResultFate.Tested, null);
                    }
                    break;

                case "expression_statement":
                    return (ResultFate.Discarded, null);

                // Transparent wrappers: the value passes through them unconsumed.
                case "await_expression":
                case "parenthesized_expression":
                case "cast_expression":
                case "binary_expression":
                case "prefix_unary_expression":
                case "postfix_unary_expression":
                case "is_pattern_expression":
                case "interpolation":
                case "interpolated_string_expression":
                    break;

                default:
                    return (ResultFate.Unknown, null);
            }

            child = parent;
        }

        return (ResultFate.Unknown, null);
    }

    private static (ResultFate Fate, string? Name) WalkPython(Node callNode)
    {
        var child = callNode;
        for (var hops = 0; hops < MaxHops; hops++)
        {
            var parent = child.Parent;
            if (parent is null)
            {
                break;
            }

            switch (parent.Type)
            {
                case "assignment":
                case "augmented_assignment":
                    return AssignedTo(parent, child);

                case "named_expression":
                    return Is(parent.GetChildForField("value"), child)
                        ? (ResultFate.Assigned, parent.GetChildForField("name")?.Text)
                        : (ResultFate.Unknown, null);

                // `with open(p) as fh:` — the alias is where the value lands.
                case "as_pattern":
                    return (ResultFate.Assigned, parent.GetChildForField("alias")?.Text);

                case "return_statement":
                case "yield":
                    return (ResultFate.Returned, null);

                // An enclosing call's argument list — our own sits below us, never above.
                case "argument_list":
                    return (ResultFate.PassedAsArgument, null);

                case "keyword_argument":
                    return Is(parent.GetChildForField("value"), child)
                        ? (ResultFate.PassedAsArgument, null)
                        : (ResultFate.Unknown, null);

                case "if_statement":
                case "elif_clause":
                case "while_statement":
                    return Is(parent.GetChildForField("condition"), child)
                        ? (ResultFate.Tested, null)
                        : (ResultFate.Unknown, null);

                case "assert_statement":
                    return (ResultFate.Tested, null);

                // `a if cond else b` carries no fields; the condition is the operand
                // written after the `if` keyword. A branch value flows onward.
                case "conditional_expression":
                    if (child.PreviousSibling is { Type: "if" })
                    {
                        return (ResultFate.Tested, null);
                    }
                    break;

                // This grammar build wraps no statement node around a bare
                // expression: a call written as its own statement is a direct child of
                // the suite it sits in.
                case "expression_statement":
                case "block":
                case "module":
                    return (ResultFate.Discarded, null);

                // Transparent wrappers.
                case "await":
                case "parenthesized_expression":
                case "not_operator":
                case "boolean_operator":
                case "comparison_operator":
                case "unary_operator":
                    break;

                default:
                    return (ResultFate.Unknown, null);
            }

            child = parent;
        }

        return (ResultFate.Unknown, null);
    }

    /// <summary>
    /// Both grammars write assignment as (left, operator, right); a call reached from the
    /// right side lands in the left side's name. Assigning to <c>_</c> is the discard
    /// idiom in both languages and is stored as what it means, not what it writes.
    /// </summary>
    private static (ResultFate Fate, string? Name) AssignedTo(Node assignment, Node child)
    {
        if (!Is(assignment.GetChildForField("right"), child))
        {
            return (ResultFate.Unknown, null);
        }

        var left = assignment.GetChildForField("left")?.Text;
        return left == "_" ? (ResultFate.Discarded, null) : (ResultFate.Assigned, left);
    }

    private static bool Is(Node? candidate, Node child) =>
        candidate is not null && candidate.Equals(child);
}
