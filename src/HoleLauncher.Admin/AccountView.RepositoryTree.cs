using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using static HoleLauncher.Admin.Locale;
using Avalonia.Interactivity;
using Avalonia.Input;
using HoleLauncherBackend.Contracts;

namespace HoleLauncher.Admin;

public sealed partial class AccountView
{
    private string? selectedTreeFile;
    private string? selectedTreeFolder;
    private string[] repositoryFolders = [];
    private RepositoryTreeFile? treeDragFile;
    private Point treeDragOrigin;
    private IPointer? treeDragPointer;
    private bool treeDragging;
    private Border? treeDropHeader;
    private IBrush? treeDropPreviousBackground;
    private Dictionary<string, TreeViewItem> repositoryTreeFolders = new(StringComparer.OrdinalIgnoreCase);
    private const double TreeDragThreshold = 6;
    private const double TreeDragPreviewOffset = 14;

    private sealed record FolderChoice(string Path)
    {
        public override string ToString() => Path.Length == 0 ? T("repository.folder_root") : Path;
    }

    private static RepositoryFile MoveToFolder(RepositoryFile file, string folder) =>
        file with { Folder = folder, PlaceAtRoot = folder.Length == 0 && file.ProjectId is not null };

    private static IEnumerable<string> FolderAndParents(string path)
    {
        while (path.Length > 0)
        {
            yield return path;
            var slash = path.LastIndexOf('/');
            if (slash < 0)
                yield break;
            path = path[..slash];
        }
    }
    private sealed record RepositoryTreeFile(RepositoryFile File, bool Automatic)
    {
        public string Key => File.ProjectId ?? $"{File.StorageDomain}/{File.StorageId}";
    }

    private void OnRepositoryTreeToggled(object? sender, RoutedEventArgs e)
    {
        if (RepositoryTreePanel is null)
            return;
        RepositoryBuildFilesTab.IsSelected = true;
        var tree = RepositoryTreeToggle.IsChecked == true;
        RepositoryTreePanel.IsVisible = tree;
        RepositoryFiles.IsVisible = !tree;
        RenderRepositoryFiles();
    }

    private void OnAddRepositoryTreeFolder(object? sender, RoutedEventArgs e)
    {
        var name = Value(RepositoryNewFolder);
        if (!RepositoryStorage.IsSafePathSegment(name))
        {
            RepositoryMessage.Text = T("build.invalid_folder", name);
            return;
        }

        var parent = selectedTreeFolder ?? "";
        selectedTreeFolder = parent.Length == 0 ? name : $"{parent}/{name}";
        selectedTreeFile = null;
        AddRepositoryFolder(selectedTreeFolder);
        RepositoryNewFolder.Clear();
        RepositoryMessage.Text = null;
        RenderRepositoryFiles();
    }

    private void OnRemoveRepositoryTreeFolder(object? sender, RoutedEventArgs e)
    {
        if (selectedTreeFolder is not { Length: > 0 } folder)
            return;

        bool IsInside(string path) => path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
        if (repositoryFiles.Any(file => IsInside(file.EffectiveFolder)) ||
            DraftAutomaticDependencies().Any(file => IsInside(ArtifactFolder(file.Path))))
        {
            RepositoryMessage.Text = T("repository.folder_not_empty");
            return;
        }

        repositoryFolders = repositoryFolders.Where(path => !IsInside(path)).ToArray();
        selectedTreeFolder = FolderAndParents(folder).Skip(1).FirstOrDefault() ?? "";
        RepositoryMessage.Text = null;
        RenderRepositoryFiles();
    }

    private static string ArtifactFolder(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    private void OnRepositoryTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (treeDragFile is not { } file || e.Pointer != treeDragPointer)
            return;
        if (!e.GetCurrentPoint(RepositoryFileTree).Properties.IsLeftButtonPressed)
        {
            ResetTreeDrag();
            return;
        }

        var point = e.GetPosition(RepositoryFileTree);
        if (!treeDragging && Math.Abs(point.X - treeDragOrigin.X) < TreeDragThreshold &&
            Math.Abs(point.Y - treeDragOrigin.Y) < TreeDragThreshold)
            return;

        if (!treeDragging)
        {
            treeDragging = true;
            e.Pointer.Capture(RepositoryFileTree);
            RepositoryDragPreview.Child = CreateCard(file.File.Card, compact: true);
            RepositoryDragPreview.IsVisible = true;
        }

        var overlayPoint = e.GetPosition(RepositoryDragOverlay);
        Canvas.SetLeft(RepositoryDragPreview, overlayPoint.X + TreeDragPreviewOffset);
        Canvas.SetTop(RepositoryDragPreview, overlayPoint.Y + TreeDragPreviewOffset);
        var target = FindTreeDropTarget(point);
        RepositoryDragPreview.Opacity = target is null ? .6 : 1;
        if (treeDropHeader != target?.Header)
        {
            ClearTreeDropHighlight();
            treeDropHeader = target?.Header as Border;
            if (treeDropHeader is not null)
            {
                treeDropPreviousBackground = treeDropHeader.Background;
                treeDropHeader.Background = Brush.Parse("#36516C");
            }
        }
        e.Handled = true;
    }

    private void OnRepositoryTreePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer != treeDragPointer || e.InitialPressMouseButton != MouseButton.Left)
            return;

        var file = treeDragging ? treeDragFile : null;
        var folder = file is null ? null :
            (FindTreeDropTarget(e.GetPosition(RepositoryFileTree))?.Tag as FolderChoice)?.Path;
        ResetTreeDrag();
        if (e.Pointer.Captured == RepositoryFileTree)
            e.Pointer.Capture(null);
        if (file is null)
            return;

        e.Handled = true;
        if (folder is null || string.Equals(file.File.EffectiveFolder, folder, StringComparison.OrdinalIgnoreCase))
            return;
        selectedTreeFile = file.Key;
        selectedTreeFolder = null;
        UpdateRepositoryFile(file.File, MoveToFolder(file.File, folder));
    }

    private TreeViewItem? FindTreeDropTarget(Point point)
    {
        if (!new Rect(RepositoryFileTree.Bounds.Size).Contains(point))
            return null;

        var item = HitItem(point) ?? HitItem(point.WithY(point.Y + 4)) ?? HitItem(point.WithY(point.Y - 4));
        if (item?.Tag is FolderChoice)
            return item;
        if (item?.Tag is RepositoryTreeFile leaf)
            return repositoryTreeFolders.GetValueOrDefault(leaf.File.EffectiveFolder);
        return null;

        TreeViewItem? HitItem(Point position)
        {
            var hit = RepositoryFileTree.InputHitTest(position) as Visual;
            return hit as TreeViewItem ?? hit?.FindAncestorOfType<TreeViewItem>();
        }
    }

    private void OnRepositoryTreePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (e.Source == RepositoryFileTree)
            ResetTreeDrag();
    }

    private void ResetTreeDrag()
    {
        treeDragFile = null;
        treeDragPointer = null;
        treeDragging = false;
        RepositoryDragPreview.IsVisible = false;
        RepositoryDragPreview.Child = null;
        ClearTreeDropHighlight();
    }

    private void ClearTreeDropHighlight()
    {
        if (treeDropHeader is not null)
            treeDropHeader.Background = treeDropPreviousBackground;
        treeDropHeader = null;
        treeDropPreviousBackground = null;
    }

    private void RenderRepositoryTree(IEnumerable<RepositoryTreeFile> files)
    {
        var folders = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
        var fileCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var root = GetFolder("");
        foreach (var folder in repositoryFolders.Order(StringComparer.OrdinalIgnoreCase))
            GetFolder(folder);

        TreeViewItem? selected = null;
        foreach (var item in files.OrderBy(item => item.File.Card.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            var file = item.File;
            var folder = file.EffectiveFolder;
            var leaf = new TreeViewItem { Header = new Border { Background = Brushes.Transparent,
                Child = CreateCard(file.Card, compact: true) }, Tag = item };
            if (!item.Automatic)
            {
                leaf.Cursor = new Cursor(StandardCursorType.Hand);
                leaf.AddHandler(PointerPressedEvent, (_, args) =>
                {
                    if (treeDragPointer is not null ||
                        args.GetCurrentPoint(leaf).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
                        return;

                    treeDragFile = leaf.Tag as RepositoryTreeFile;
                    treeDragPointer = args.Pointer;
                    treeDragOrigin = args.GetPosition(RepositoryFileTree);
                }, RoutingStrategies.Tunnel);
            }
            var index = fileCounts.GetValueOrDefault(folder);
            GetFolder(folder).Items.Insert(index, leaf);
            fileCounts[folder] = index + 1;
            if (item.Key == selectedTreeFile)
                selected = leaf;
        }

        if (selected is null && selectedTreeFolder is { } path)
            folders.TryGetValue(path, out selected);
        repositoryTreeFolders = folders;
        RepositoryFileTree.ItemsSource = new[] { root };
        RepositoryFileTree.SelectedItem = selected;
        ApplyTreeSelection(selected);

        TreeViewItem GetFolder(string path)
        {
            if (folders.TryGetValue(path, out var existing))
                return existing;
            var slash = path.LastIndexOf('/');
            var header = new Border { Background = Brushes.Transparent, Padding = new Thickness(4, 1),
                Child = new TextBlock { Text = path.Length == 0 ? T("repository.folder_root") : path[(slash + 1)..] } };
            var node = new TreeViewItem { Header = header, IsExpanded = true, Tag = new FolderChoice(path) };
            folders.Add(path, node);
            if (path.Length > 0)
                GetFolder(slash < 0 ? "" : path[..slash]).Items.Add(node);
            return node;
        }
    }

    private void OnRepositoryTreeSelected(object? sender, SelectionChangedEventArgs e) =>
        ApplyTreeSelection(RepositoryFileTree.SelectedItem as TreeViewItem);

    private void ApplyTreeSelection(TreeViewItem? item)
    {
        selectedTreeFolder = (item?.Tag as FolderChoice)?.Path;
        RepositoryRemoveFolder.IsEnabled = selectedTreeFolder is { Length: > 0 };
        ShowTreeFile(item?.Tag as RepositoryTreeFile);
    }

    private void ShowTreeFile(RepositoryTreeFile? selection)
    {
        selectedTreeFile = selection?.Key;
        if (selection is null)
        {
            RepositoryTreeDetails.Content = null;
            return;
        }

        var entries = resolvedRepositoryMods.Where(file => file.ProjectId is not null).ToDictionary(file => file.ProjectId!);
        var explicitProjects = repositoryFiles.Select(file => file.ProjectId).OfType<string>().ToHashSet();
        var pin = selection.Automatic && selection.File.ProjectId is { } project && entries.TryGetValue(project, out var dependency)
            ? CreateDependencyPin(selection.File, dependency.ModVersionId) : null;
        RepositoryTreeDetails.Content = CreateRepositoryFileCard(selection.File, selection.Automatic,
            selection.Automatic ? null : FindAutomaticDependencies(selection.File.ProjectId, entries, explicitProjects), pin);
    }
}
