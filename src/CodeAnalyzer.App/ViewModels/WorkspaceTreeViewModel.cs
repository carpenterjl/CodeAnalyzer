using System.Collections.ObjectModel;
using System.IO;
using CodeAnalyzer.Core.Crawling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodeAnalyzer.App.ViewModels;

/// <summary>
/// The checkbox tree of workspace subdirectories. The checked set defines what the
/// crawler walks; "Apply selection" is what actually kicks off indexing.
/// </summary>
public sealed partial class WorkspaceTreeViewModel : ObservableObject
{
    [ObservableProperty]
    private string? _workspaceRoot;

    [ObservableProperty]
    private bool _hasWorkspace;

    public ObservableCollection<DirectoryNodeViewModel> Roots { get; } = [];

    /// <summary>Raised when the user applies a selection; carries relative directory paths.</summary>
    public event EventHandler<IReadOnlyList<string>>? SelectionApplied;

    public async Task LoadWorkspaceAsync(string rootPath, WorkspaceSettings? settings = null)
    {
        Roots.Clear();
        WorkspaceRoot = rootPath;
        HasWorkspace = true;

        var root = new DirectoryNodeViewModel(
            Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name
                ? name
                : rootPath,
            rootPath,
            string.Empty,
            parent: null,
            settings);

        Roots.Add(root);

        await root.EnsureChildrenLoadedAsync().ConfigureAwait(true);
        root.IsExpanded = true;
    }

    public void Clear()
    {
        Roots.Clear();
        WorkspaceRoot = null;
        HasWorkspace = false;
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var root in Roots)
        {
            root.IsChecked = true;
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var root in Roots)
        {
            root.IsChecked = false;
        }
    }

    /// <summary>The checked directories, as workspace-relative paths.</summary>
    public IReadOnlyList<string> CollectSelectedDirectories()
    {
        var selected = new List<string>();
        foreach (var root in Roots)
        {
            root.CollectSelected(selected);
        }

        return selected;
    }

    /// <summary>
    /// Re-applies a previously saved selection when a workspace is reopened. Only the
    /// materialised part of the tree can be checked; deeper nodes inherit on expand.
    /// </summary>
    public void RestoreSelection(IReadOnlyList<string> relativePaths)
    {
        if (relativePaths.Count == 0)
        {
            return;
        }

        // An empty path means the whole workspace was selected.
        if (relativePaths.Any(string.IsNullOrEmpty))
        {
            SelectAll();
            return;
        }

        var wanted = new HashSet<string>(relativePaths, StringComparer.OrdinalIgnoreCase);

        foreach (var root in Roots)
        {
            ApplyRestore(root, wanted);
        }
    }

    private static void ApplyRestore(DirectoryNodeViewModel node, HashSet<string> wanted)
    {
        if (wanted.Contains(node.RelativePath))
        {
            node.IsChecked = true;
            return;
        }

        foreach (var child in node.Children)
        {
            ApplyRestore(child, wanted);
        }
    }

    [RelayCommand]
    private void ApplySelection() => SelectionApplied?.Invoke(this, CollectSelectedDirectories());
}
