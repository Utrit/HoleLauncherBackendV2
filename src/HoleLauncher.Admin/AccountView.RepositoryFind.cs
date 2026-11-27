using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HoleLauncherBackend.Contracts;
using static HoleLauncher.Admin.Locale;

namespace HoleLauncher.Admin;

public sealed partial class AccountView
{
    private ObservableCollection<RepositoryFile> repositoryFileRows = [];
    private List<(int Index, TreeViewItem? Node)> repositoryMatches = [];
    private int repositoryMatch = -1;

    private static Control CreateFlatRepositoryCard()
    {
        const int iconSize = 18;
        var icon = new Image { Width = iconSize, Height = iconSize, Stretch = Stretch.UniformToFill };
        var title = new TextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.SemiBold };
        var content = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10, Children = { icon, title } };
        Grid.SetColumn(title, 1);
        var row = new Border { Height = 36, Padding = new Thickness(8, 4), ClipToBounds = true,
            BorderThickness = new Thickness(3, 0, 0, 0), Child = content };
        row.DataContextChanged += (_, _) => _ = UpdateAsync();
        row.AttachedToVisualTree += (_, _) => _ = UpdateAsync();
        return row;

        async Task UpdateAsync()
        {
            var file = row.DataContext as RepositoryFile;
            title.Text = file?.Card?.Title;
            row.BorderBrush = file is null ? null : TypeBrush(file.Type);
            icon.Source = null;
            if (!row.IsAttachedToVisualTree() || file?.Card?.IconUrl is not { Length: > 0 } url)
                return;
            var bitmap = await GetRepositoryIconAsync(url, iconSize);
            if (ReferenceEquals(row.DataContext, file))
                icon.Source = bitmap;
        }
    }

    private void OnFlatRepositoryFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source == RepositoryFiles)
            ShowFlatRepositoryDetails();
    }

    private void ShowFlatRepositoryDetails()
    {
        if (RepositoryFlatDetails is null)
            return;
        RepositoryFlatDetails.Content = RepositoryFiles.SelectedItem is RepositoryFile file
            ? CreateRepositoryFileCard(file, false,
                FindAutomaticDependencies(file.ProjectId, flatResolvedMods, flatExplicitProjects))
            : null;
    }

    private void OpenRepositoryFind()
    {
        RepositoryFindBar.IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            RepositoryFindText.Focus();
            RepositoryFindText.SelectAll();
        });
    }

    private void OnRepositoryFindChanged(object? sender, TextChangedEventArgs e) =>
        RefreshRepositoryMatches(jump: true);

    private void OnPreviousRepositoryMatch(object? sender, RoutedEventArgs e) => MoveRepositoryMatch(-1);

    private void OnNextRepositoryMatch(object? sender, RoutedEventArgs e) => MoveRepositoryMatch(1);

    private void OnCloseRepositoryFind(object? sender, RoutedEventArgs e) => CloseRepositoryFind();

    private void CloseRepositoryFind()
    {
        RepositoryFindBar.IsVisible = false;
        RepositoryFindText.Clear();
        if (RepositoryTreeToggle.IsChecked == true)
            RepositoryFileTree.Focus();
        else
            RepositoryFiles.Focus();
    }

    private void OnRepositoryFindKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            CloseRepositoryFind();
        else if (e.Key == Key.Enter)
            MoveRepositoryMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        else
            return;
        e.Handled = true;
    }

    private void RefreshRepositoryMatches(bool jump = false)
    {
        if (RepositoryFindBar?.IsVisible != true)
            return;
        var query = RepositoryFindText.Text?.Trim();
        repositoryMatches.Clear();
        if (!string.IsNullOrEmpty(query))
        {
            if (RepositoryTreeToggle.IsChecked == true)
            {
                foreach (var node in RepositoryFileTree.Items.OfType<TreeViewItem>().SelectMany(TreeNodes))
                {
                    if (node.Tag is RepositoryTreeFile file && Matches(file.File, query) ||
                        node.Tag is FolderChoice folder && folder.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
                        repositoryMatches.Add((-1, node));
                }
            }
            else
            {
                for (var index = 0; index < repositoryFileRows.Count; index++)
                    if (Matches(repositoryFileRows[index], query))
                        repositoryMatches.Add((index, null));
            }
        }

        repositoryMatch = repositoryMatches.Count == 0 ? -1 : jump ? 0 :
            Math.Clamp(repositoryMatch, 0, repositoryMatches.Count - 1);
        UpdateRepositoryMatchCount();
        if (jump && repositoryMatch >= 0)
            ScrollToRepositoryMatch();
    }

    private static bool Matches(RepositoryFile file, string query) =>
        file.Card.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (file.Card.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
        file.EffectiveFolder.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<TreeViewItem> TreeNodes(TreeViewItem item)
    {
        yield return item;
        foreach (var descendant in item.Items.OfType<TreeViewItem>().SelectMany(TreeNodes))
            yield return descendant;
    }

    private void MoveRepositoryMatch(int direction)
    {
        if (repositoryMatches.Count == 0)
            return;
        repositoryMatch = (repositoryMatch + direction + repositoryMatches.Count) % repositoryMatches.Count;
        UpdateRepositoryMatchCount();
        ScrollToRepositoryMatch();
    }

    private void UpdateRepositoryMatchCount() => RepositoryFindCount.Text =
        string.IsNullOrWhiteSpace(RepositoryFindText.Text) ? "" :
        repositoryMatch < 0 ? T("repository.find_empty") : $"{repositoryMatch + 1} / {repositoryMatches.Count}";

    private void ScrollToRepositoryMatch()
    {
        var (index, node) = repositoryMatches[repositoryMatch];
        if (node is null)
        {
            RepositoryFiles.SelectedIndex = index;
            RepositoryFiles.ScrollIntoView(index);
            return;
        }

        for (var parent = node.Parent as TreeViewItem; parent is not null; parent = parent.Parent as TreeViewItem)
            parent.IsExpanded = true;
        RepositoryFileTree.SelectedItem = node;
        Dispatcher.UIThread.Post(() => node.BringIntoView(), DispatcherPriority.Loaded);
    }
}
