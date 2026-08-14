using System.Collections.ObjectModel;
using System.IO;
using CodeAnalyzer.Core.Crawling;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeAnalyzer.App.ViewModels;

/// <summary>
/// A directory in the workspace tree, with tri-state selection that propagates down to
/// children and recomputes up through ancestors. Children load on first expand, off the
/// UI thread, so opening a large workspace never walks the whole tree up front.
/// </summary>
public sealed partial class DirectoryNodeViewModel : ObservableObject
{
    private bool _childrenLoadStarted;
    private bool _suppressPropagation;

    private readonly WorkspaceSettings _settings;

    public DirectoryNodeViewModel(
        string name,
        string fullPath,
        string relativePath,
        DirectoryNodeViewModel? parent,
        WorkspaceSettings? settings = null)
    {
        Name = name;
        FullPath = fullPath;
        RelativePath = relativePath;
        Parent = parent;

        // The tree greys out exactly what the crawler will skip, so both must judge by
        // the same rules — including this workspace's extras.
        _settings = settings ?? parent?._settings ?? WorkspaceSettings.Default;
    }

    public string Name { get; }

    public string FullPath { get; }

    /// <summary>Path relative to the workspace root, forward-slashed. Empty for the root node.</summary>
    public string RelativePath { get; }

    public DirectoryNodeViewModel? Parent { get; }

    public ObservableCollection<DirectoryNodeViewModel> Children { get; } = [];

    /// <summary>Null means partially selected, which is what a tri-state CheckBox binds to.</summary>
    [ObservableProperty]
    private bool? _isChecked = false;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoadingChildren;

    /// <summary>Directories excluded by the crawler's ignore rules, shown greyed out.</summary>
    [ObservableProperty]
    private bool _isIgnored;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            _ = EnsureChildrenLoadedAsync();
        }
    }

    partial void OnIsCheckedChanged(bool? value)
    {
        if (_suppressPropagation || value is null)
        {
            return;
        }

        // Applies to the whole subtree. Children not yet materialized inherit this
        // state when they load, so there is no need to force a disk walk here.
        foreach (var child in Children)
        {
            child.SetCheckedFromParent(value.Value);
        }

        Parent?.RecomputeFromChildren();
    }

    private void SetCheckedFromParent(bool value)
    {
        _suppressPropagation = true;
        IsChecked = value && !IsIgnored;
        _suppressPropagation = false;

        foreach (var child in Children)
        {
            child.SetCheckedFromParent(value);
        }
    }

    private void RecomputeFromChildren()
    {
        if (Children.Count == 0)
        {
            return;
        }

        var allChecked = Children.All(c => c.IsChecked == true);
        var noneChecked = Children.All(c => c.IsChecked == false);

        _suppressPropagation = true;
        IsChecked = allChecked ? true : noneChecked ? false : null;
        _suppressPropagation = false;

        Parent?.RecomputeFromChildren();
    }

    /// <summary>
    /// Populates immediate subdirectories. Disk enumeration runs on a worker thread; the
    /// continuation resumes on the UI thread to touch <see cref="Children"/>.
    /// Safe to call repeatedly — only the first call walks disk.
    /// </summary>
    public async Task EnsureChildrenLoadedAsync()
    {
        if (_childrenLoadStarted || string.IsNullOrEmpty(FullPath))
        {
            return;
        }

        _childrenLoadStarted = true;
        IsLoadingChildren = true;
        try
        {
            var path = FullPath;
            var subdirectories = await Task.Run(() => EnumerateSubdirectories(path)).ConfigureAwait(true);

            // Children of a fully-checked parent start checked so selection stays consistent.
            var inheritChecked = IsChecked == true;

            foreach (var directory in subdirectories)
            {
                var name = Path.GetFileName(directory);
                var relative = string.IsNullOrEmpty(RelativePath) ? name : $"{RelativePath}/{name}";
                var ignored = _settings.IsIgnoredDirectoryName(name);

                var child = new DirectoryNodeViewModel(name, directory, relative, this)
                {
                    IsIgnored = ignored,
                    _suppressPropagation = true,
                    IsChecked = inheritChecked && !ignored,
                };
                child._suppressPropagation = false;

                Children.Add(child);
            }
        }
        finally
        {
            IsLoadingChildren = false;
        }
    }

    private static List<string> EnumerateSubdirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// Collects relative paths of selected directories. A fully checked directory implies
    /// everything beneath it, so this never needs the subtree to be materialized.
    /// </summary>
    public void CollectSelected(ICollection<string> into)
    {
        if (IsChecked == true)
        {
            into.Add(RelativePath);
            return;
        }

        foreach (var child in Children)
        {
            child.CollectSelected(into);
        }
    }
}
