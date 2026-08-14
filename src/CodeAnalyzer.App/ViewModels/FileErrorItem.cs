namespace CodeAnalyzer.App.ViewModels;

/// <summary>
/// One row in the error list: a file whose last parse was imperfect, with a description
/// that says whether partial symbols survived or nothing did.
/// </summary>
public sealed record FileErrorItem(string RelativePath, string Language, string Description);
