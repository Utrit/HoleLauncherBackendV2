using System.Net.Http.Json;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using System.Runtime.InteropServices.JavaScript;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using HoleLauncherBackend.Contracts;
using static HoleLauncher.Admin.Locale;

namespace HoleLauncher.Admin;

public sealed partial class AccountView
{
    private const string RepositoryPath = "/api/auth/repository";
    private RepositoryFile[] repositoryFiles = [];
    private RepositoryCatalog repository = new([], []);
    private RepositoryManifest? selectedRepositoryVersion;
    private RepositoryArtifact[] resolvedRepositoryMods = [];
    private int modOffset;
    private bool repositoryUploading;
    private JSObject? repositoryPendingFile;
    private JSObject? repositoryPendingIcon;
    private StorageUploadAccepted? repositoryUploadedFile;
    private StorageUploadAccepted? repositoryUploadedIcon;
    private bool updatingRepositoryOptions;
    private RepositoryGameVersion[] gameVersions = [];

    private sealed record RepositoryLoaderChoice(RepositoryLoader Loader)
    {
        public override string ToString() => Loader.Name;
    }

    private sealed record SearchModRow(ModSearchHit Hit, CheckBox Selected, ComboBox Type);

    private sealed record RepositoryType(RepositoryFileType Type)
    {
        public override string ToString() => T($"repository.type.{Type.ToString().ToLowerInvariant()}");
    }

    private sealed record RepositoryVersion(string? Id, string Label)
    {
        public override string ToString() => Label;
    }

    private async void OnRepositoryNav(object? sender, RoutedEventArgs e)
    {
        SelectSection(RepositoryNav);
        RepositoryTypePicker.ItemsSource = Enum.GetValues<RepositoryFileType>().Select(type => new RepositoryType(type)).ToArray();
        RepositoryTypePicker.SelectedIndex = 0;
        await RunRepositoryAsync(async () =>
        {
            if (gameVersions.Length == 0)
            {
                gameVersions = await Api.GetFromJsonAsync($"{RepositoryPath}/game-versions", AdminJsonContext.Default.RepositoryGameVersionArray) ?? [];
                RepositoryMinecraft.ItemsSource = gameVersions.Select(game => game.Version).ToArray();
                SetRepositoryCompatibility("1.21.1", "neoforge", null);
            }
            await LoadRepositoryAsync();
        });
    }

    private async Task LoadRepositoryAsync()
    {
        repository = await Api.GetFromJsonAsync($"{RepositoryPath}/catalog", AdminJsonContext.Default.RepositoryCatalog)
            ?? new([], []);
        RenderRepositoryVersions();
        RenderRepositoryOffers();
    }

    private void RenderRepositoryVersions()
    {
        RepositoryVersions.Children.Clear();
        foreach (var manifest in repository.Versions.Reverse())
        {
            var title = new TextBlock { Text = manifest.Build.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            var info = new TextBlock { Text = T("repository.version_files", manifest.Build.Version, manifest.Files.Length) };
            var actions = new WrapPanel();
            actions.Children.Add(ChannelCheckbox(manifest, test: false));
            actions.Children.Add(ChannelCheckbox(manifest, test: true));
            var edit = new Button { Content = T("repository.edit_version"), Margin = new Thickness(8, 0, 0, 0) };
            edit.Click += (_, _) => SelectRepositoryVersion(manifest);
            actions.Children.Add(edit);
            actions.Children.Add(DownloadBuildButton(manifest, server: false));
            actions.Children.Add(DownloadBuildButton(manifest, server: true));
            actions.Children.Add(DownloadBuildButton(manifest, server: null));
            var remove = new Button { Content = T("repository.delete_version"), Margin = new Thickness(8, 0, 0, 0) };
            remove.Click += async (_, _) =>
            {
                if (!Equals(remove.Content, T("repository.confirm_delete_version")))
                {
                    remove.Content = T("repository.confirm_delete_version");
                    return;
                }
                await RunRepositoryAsync(async () =>
                {
                    using var response = await Api.DeleteAsync($"{RepositoryPath}/builds/{manifest.BuildId:D}/version?version={Uri.EscapeDataString(manifest.Build.Version)}");
                    response.EnsureSuccessStatusCode();
                    if (selectedRepositoryVersion?.BuildId == manifest.BuildId &&
                        selectedRepositoryVersion.Build.Version == manifest.Build.Version)
                        OnNewRepositoryBuild(null, new RoutedEventArgs());
                    await LoadRepositoryAsync();
                });
            };
            actions.Children.Add(remove);
            var isSelected = selectedRepositoryVersion?.BuildId == manifest.BuildId &&
                selectedRepositoryVersion.Build.Version == manifest.Build.Version;
            RepositoryVersions.Children.Add(new Border
            {
                Background = Brush.Parse(isSelected ? "#293D55" : "#1B2533"),
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(6),
                Child = new StackPanel { Spacing = 3, Children = { title, info, actions } }
            });
        }
    }

    private Button DownloadBuildButton(RepositoryManifest manifest, bool? server)
    {
        var side = server == true ? "server" : "client";
        var label = T(server switch
        {
            true => "repository.download_server",
            false => "repository.download_client",
            null => "repository.download_manifest"
        });
        var button = new Button { Content = label, Margin = new Thickness(8, 0, 0, 0) };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            if (server.HasValue) button.Content = T("repository.download_preparing");
            RepositoryMessage.Text = null;
            try
            {
                var path = $"{RepositoryPath}/builds/{manifest.BuildId:D}";
                var version = Uri.EscapeDataString(manifest.Build.Version);
                path += server.HasValue ? $"/download?version={version}&side={side}" : $"/manifest?version={version}";
                var suffix = server.HasValue ? $"{side}.zip" : "manifest.json";
                var status = await Program.DownloadRepositoryArchive(path, $"{manifest.Build.Name}-{manifest.Build.Version}-{suffix}");
                RepositoryMessage.Text = T(status == 200 ? "repository.download_started" : "repository.download_failed");
            }
            catch (JSException)
            {
                RepositoryMessage.Text = T("repository.download_failed");
            }
            finally
            {
                button.Content = label;
                button.IsEnabled = true;
            }
        };
        return button;
    }

    private CheckBox ChannelCheckbox(RepositoryManifest manifest, bool test)
    {
        var channel = new RepositoryRelease(manifest.BuildId, manifest.Build.Version, test);
        var check = new CheckBox
        {
            Content = T(test ? "repository.testing" : "repository.stage"),
            IsChecked = repository.Releases.Contains(channel)
        };
        check.Click += async (_, _) =>
        {
            var saved = false;
            await RunRepositoryAsync(async () =>
            {
                var change = new RepositoryChannel(manifest.Build.Version, test, check.IsChecked == true);
                using var response = await Api.PutAsJsonAsync($"{RepositoryPath}/builds/{manifest.BuildId:D}/channel", change,
                    AdminJsonContext.Default.RepositoryChannel);
                response.EnsureSuccessStatusCode();
                repository = repository with
                {
                    Releases = change.Enabled ? [.. repository.Releases, channel] :
                        repository.Releases.Where(entry => entry != channel).ToArray()
                };
                saved = true;
            });
            if (!saved)
                check.IsChecked = repository.Releases.Contains(channel);
        };
        return check;
    }

    private void SelectRepositoryVersion(RepositoryManifest manifest)
    {
        pendingOfferAcceptances.Clear();
        selectedRepositoryVersion = manifest;
        var draft = manifest.Build;
        RepositoryName.Text = draft.Name;
        SetRepositoryCompatibility(draft.Minecraft, draft.Loader, draft.LoaderVersion);
        repositoryFolders = [.. draft.Folders];
        selectedTreeFolder = selectedTreeFile = null;
        ResetRepositoryUploads();
        repositoryFiles = draft.Files;
        resolvedRepositoryMods = manifest.Files.Where(file => file.ProjectId is not null).ToArray();
        RenderRepositoryVersions();
        RenderRepositoryFiles();
        RenderRepositoryOffers();
    }

    private void OnNewRepositoryBuild(object? sender, RoutedEventArgs e)
    {
        pendingOfferAcceptances.Clear();
        selectedRepositoryVersion = null;
        RepositoryName.Text = null;
        repositoryFolders = [];
        selectedTreeFolder = selectedTreeFile = null;
        SetRepositoryCompatibility("1.21.1", "neoforge", null);
        RepositoryModHits.Children.Clear();
        ResetRepositoryUploads();
        repositoryFiles = [];
        resolvedRepositoryMods = [];
        RenderRepositoryVersions();
        RenderRepositoryFiles();
        RenderRepositoryOffers();
    }

    private void OnRepositoryMinecraftChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (updatingRepositoryOptions)
            return;
        SetRepositoryCompatibility(RepositoryMinecraft.SelectedItem as string,
            (RepositoryLoader.SelectedItem as RepositoryLoaderChoice)?.Loader.Id, null);
        ClearRepositoryDependencies();
    }

    private void OnRepositoryLoaderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (updatingRepositoryOptions)
            return;
        SetRepositoryLoaderVersion(null);
        ClearRepositorySearch();
        ClearRepositoryDependencies();
    }

    private void ClearRepositoryDependencies()
    {
        resolvedRepositoryMods = [];
        RenderRepositoryFiles();
    }

    private void SetRepositoryCompatibility(string? minecraft, string? loader, string? loaderVersion)
    {
        updatingRepositoryOptions = true;
        try
        {
            var game = gameVersions.FirstOrDefault(game => game.Version == minecraft) ?? gameVersions.FirstOrDefault();
            RepositoryMinecraft.SelectedItem = game?.Version;
            var choices = game?.Loaders.Select(item => new RepositoryLoaderChoice(item)).ToArray() ?? [];
            RepositoryLoader.ItemsSource = choices;
            RepositoryLoader.SelectedItem = choices.FirstOrDefault(item => item.Loader.Id == loader) ?? choices.FirstOrDefault();
            SetRepositoryLoaderVersion(loaderVersion);
            ClearRepositorySearch();
        }
        finally
        {
            updatingRepositoryOptions = false;
        }
    }

    private void SetRepositoryLoaderVersion(string? version)
    {
        var versions = (RepositoryLoader.SelectedItem as RepositoryLoaderChoice)?.Loader.Versions ?? [];
        RepositoryLoaderVersion.ItemsSource = versions;
        RepositoryLoaderVersion.SelectedItem = versions.Contains(version) ? version :
            versions.FirstOrDefault(item => !item.Contains('-')) ?? versions.FirstOrDefault();
    }

    private void ClearRepositorySearch()
    {
        modOffset = 0;
        RepositoryModHits.Children.Clear();
        RepositoryMoreMods.IsEnabled = false;
    }

    private async void OnBuildRepositoryVersion(object? sender, RoutedEventArgs e) => await RunRepositoryAsync(async () =>
    {
        if (RepositoryMinecraft.SelectedItem is not string minecraft ||
            RepositoryLoader.SelectedItem is not RepositoryLoaderChoice loader ||
            RepositoryLoaderVersion.SelectedItem is not string loaderVersion)
        {
            RepositoryMessage.Text = T("repository.invalid");
            return;
        }

        var id = selectedRepositoryVersion?.BuildId ?? Guid.NewGuid();
        var acceptedOffers = pendingOfferAcceptances.Where(entry => repositoryFiles.Any(file =>
            entry.Value.ProjectId is { } project ? project == file.ProjectId :
            file.StorageDomain == entry.Value.StorageDomain && file.StorageId == entry.Value.StorageId)).Select(entry => entry.Key).ToArray();
        var draft = new RepositoryDraft(Value(RepositoryName), "", minecraft, loader.Loader.Id, loaderVersion,
            repositoryFolders
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), repositoryFiles, acceptedOffers);
        using var response = await Api.PutAsJsonAsync($"{RepositoryPath}/builds/{id:D}/build", draft, AdminJsonContext.Default.RepositoryDraft);
        if (!response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "application/json" &&
            await response.Content.ReadFromJsonAsync(AdminJsonContext.Default.RepositoryBuildError) is { } error)
        {
            RepositoryMessage.Text = T(error.Key, error.Arguments);
            return;
        }

        response.EnsureSuccessStatusCode();
        var manifest = await response.Content.ReadFromJsonAsync(AdminJsonContext.Default.RepositoryManifest) ?? throw new JsonException();
        await LoadRepositoryAsync();
        SelectRepositoryVersion(manifest);
        RepositoryMessage.Text = T("repository.built", manifest.Build.Version);
    });

    private async void OnRepositorySearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !RepositoryPanel.IsEnabled)
            return;
        e.Handled = true;
        await SearchRepositoryModsAsync();
    }

    private async void OnSearchRepositoryMods(object? sender, RoutedEventArgs e) => await SearchRepositoryModsAsync();

    private Task SearchRepositoryModsAsync()
    {
        ClearRepositorySearch();
        return ModrinthProjectFromLink(Value(RepositoryModQuery)) is { } id
            ? AddRepositoryModFromLinkAsync(id) : FetchModsAsync();
    }

    private async void OnMoreRepositoryMods(object? sender, RoutedEventArgs e) => await FetchModsAsync();

    private Task FetchModsAsync() => RunRepositoryAsync(async () =>
    {
        var query = Uri.EscapeDataString(Value(RepositoryModQuery));
        var hits = await Api.GetFromJsonAsync($"{RepositoryPath}/mods?query={query}&{Compatibility()}&offset={modOffset}",
            AdminJsonContext.Default.ModSearchHitArray) ?? [];
        modOffset += hits.Length;
        RepositoryMoreMods.IsEnabled = hits.Length != 0;
        foreach (var hit in hits)
        {
            var file = repositoryFiles.FirstOrDefault(file => file.ProjectId == hit.ProjectId);
            var selected = new CheckBox { IsChecked = file is not null, Content = CreateCard(hit.Card) };
            var type = CreateTypePicker(file?.Type ?? SelectedType());
            var card = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Children = { selected, type } };
            Grid.SetColumn(type, 1);
            var border = new Border { Padding = new Thickness(8), CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(3, 0, 0, 0), Child = card, Tag = new SearchModRow(hit, selected, type) };
            border.BorderBrush = TypeBrush(PickerType(type));
            type.SelectionChanged += (_, _) => border.BorderBrush = TypeBrush(PickerType(type));
            RepositoryModHits.Children.Add(border);
        }
    });

    private void OnAddSelectedMods(object? sender, RoutedEventArgs e)
    {
        var rows = RepositoryModHits.Children.OfType<Border>().Select(border => (SearchModRow)border.Tag!)
            .Where(row => row.Selected.IsChecked == true).ToDictionary(row => row.Hit.ProjectId);
        var next = repositoryFiles.Select(file => file.ProjectId is { } id && rows.Remove(id, out var row)
            ? file with { Type = PickerType(row.Type) } : file).ToList();
        var folder = Value(RepositoryFolder);
        if (folder.Length > 0 && !RepositoryStorage.IsSafeFolder(folder))
        {
            RepositoryMessage.Text = T("build.invalid_folder", folder);
            return;
        }
        next.AddRange(rows.Values.Select(row => new RepositoryFile(folder, PickerType(row.Type),
            row.Hit.Card, ProjectId: row.Hit.ProjectId)));
        if (rows.Count > 0)
            AddRepositoryFolder(folder);
        repositoryFiles = [.. next];
        RenderRepositoryFiles();
    }

    private async void OnAddRepositoryStorage(object? sender, RoutedEventArgs e)
    {
        if (repositoryUploading || repositoryPendingFile is not { } file)
            return;
        var domain = RepositoryStorage.DomainFor(Value(RepositoryName));
        var title = Value(RepositoryStorageTitle);
        if (domain is null || title.Length == 0 ||
            (repositoryUploadedFile is { } previous && previous.Domain != domain) ||
            (repositoryUploadedIcon is { } previousIcon && previousIcon.Domain != domain))
        {
            RepositoryMessage.Text = T("repository.invalid");
            return;
        }

        await RunRepositoryAsync(async () =>
        {
            var account = gameAccountId;
            var version = selectedRepositoryVersion;
            if (repositoryUploadedFile is null)
                await UploadRepositoryFileAsync(file, false, (result, _) => repositoryUploadedFile = result);
            if (repositoryUploadedFile is not { } stored)
                return;

            if (repositoryPendingIcon is { } image && repositoryUploadedIcon is null)
            {
                await UploadRepositoryFileAsync(image, true, (result, _) => repositoryUploadedIcon = result);
                if (repositoryUploadedIcon is null)
                    return;
            }

            if (account != gameAccountId || version != selectedRepositoryVersion ||
                !ReferenceEquals(file, repositoryPendingFile))
                return;
            var iconUrl = repositoryUploadedIcon is { } icon ? RepositoryStorage.FileUrl(icon.Domain, icon.FileId) : null;
            var card = new ModCard(title, Value(RepositoryStorageDescription), iconUrl);
            var folder = Value(RepositoryFolder);
            repositoryFiles = [.. repositoryFiles, new(folder, SelectedType(), card, StorageDomain: stored.Domain, StorageId: stored.FileId)];
            AddRepositoryFolder(folder);
            ResetRepositoryUploads();
            RenderRepositoryFiles();
        });
    }

    private static void OnRepositoryDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects &= DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnRepositoryDropFile(object? sender, DragEventArgs e) => StageRepositoryDrop(e, false);
    private void OnRepositoryDropIcon(object? sender, DragEventArgs e) => StageRepositoryDrop(e, true);
    private async void OnChooseRepositoryFile(object? sender, RoutedEventArgs e) => await ChooseRepositoryUploadAsync(false);
    private async void OnChooseRepositoryIcon(object? sender, RoutedEventArgs e) => await ChooseRepositoryUploadAsync(true);

    private void StageRepositoryDrop(DragEventArgs args, bool icon)
    {
        args.Handled = true;
        if (Program.TakeRepositoryDrop() is { } file)
            StageRepositoryFile(file, icon);
    }

    private void StageRepositoryFile(JSObject file, bool icon)
    {
        if (repositoryUploading || !RepositoryPanel.IsEnabled)
        {
            file.Dispose();
            return;
        }

        var name = file.GetPropertyAsString("name") ?? "";
        if (icon)
        {
            repositoryPendingIcon?.Dispose();
            repositoryPendingIcon = file;
            repositoryUploadedIcon = null;
            RepositoryUploadedIconName.Text = name;
        }
        else
        {
            repositoryPendingFile?.Dispose();
            repositoryPendingFile = file;
            repositoryUploadedFile = null;
            RepositoryUploadedFileName.Text = name;
            RepositoryStorageTitle.Text = name;
        }
        RepositoryUploadMessage.Text = null;
    }

    private async Task ChooseRepositoryUploadAsync(bool icon, Action<StorageUploadAccepted, string>? onIcon = null)
    {
        if (repositoryUploading)
            return;

        var account = gameAccountId;
        var version = selectedRepositoryVersion;
        var name = Value(RepositoryName);
        try
        {
            var file = await Program.ChooseRepositoryFile(icon);
            if (file is null)
                return;
            if (account != gameAccountId || version != selectedRepositoryVersion ||
                name != Value(RepositoryName) || !RepositoryPanel.IsVisible)
            {
                file.Dispose();
                return;
            }

            if (onIcon is null)
            {
                StageRepositoryFile(file, icon);
                return;
            }

            using (file)
                await UploadRepositoryFileAsync(file, icon, onIcon);
        }
        catch (JSException)
        {
            RepositoryUploadMessage.Text = T("repository.upload_failed");
        }
    }


    private async Task UploadRepositoryFileAsync(JSObject file, bool icon, Action<StorageUploadAccepted, string> onUploaded)
    {
        if (repositoryUploading)
            return;
        var name = Value(RepositoryName);
        if (RepositoryStorage.DomainFor(name) is null)
        {
            RepositoryUploadMessage.Text = T("repository.upload_name");
            return;
        }

        var account = gameAccountId;
        var version = selectedRepositoryVersion;
        var fileName = file.GetPropertyAsString("name") ?? "";
        repositoryUploading = true;
        RepositoryUploadMessage.Text = T("repository.upload_waiting");
        RepositoryUploadProgress.Value = 0;
        RepositoryUploadProgress.IsIndeterminate = false;
        RepositoryUploadProgress.IsVisible = true;
        try
        {
            var path = $"{RepositoryPath}/upload?name={Uri.EscapeDataString(name)}&icon={icon.ToString().ToLowerInvariant()}";
            var raw = await Program.UploadRepositoryFile(path, file, ReportUploadProgress);
            var response = JsonSerializer.Deserialize(raw, AdminJsonContext.Default.BrowserUploadResponse) ?? throw new JsonException();
            if (response.Status != (int)System.Net.HttpStatusCode.Accepted)
            {
                RepositoryUploadMessage.Text = T(response.Status == 400 && icon ? "repository.invalid_icon" : "repository.upload_failed");
                return;
            }

            var uploaded = JsonSerializer.Deserialize(response.Body, AdminJsonContext.Default.StorageUploadAccepted)
                ?? throw new JsonException();
            RepositoryUploadProgress.Value = 100;
            if (!await WaitForOperationAsync(uploaded.OperationId, T("repository.upload_failed"),
                message => RepositoryUploadMessage.Text = message))
                return;

            if (account != gameAccountId || version != selectedRepositoryVersion || name != Value(RepositoryName))
            {
                RepositoryUploadMessage.Text = T("repository.uploaded");
                return;
            }

            onUploaded(uploaded, fileName);
            RepositoryUploadMessage.Text = T("repository.uploaded");
        }
        catch (Exception error) when (error is JSException or HttpRequestException or JsonException or OperationCanceledException)
        {
            RepositoryUploadMessage.Text = T("repository.upload_failed");
        }
        finally
        {
            repositoryUploading = false;
            RepositoryUploadProgress.IsVisible = false;
        }
    }

    private void ReportUploadProgress(double bytes, double total, double bytesPerSecond)
    {
        var percent = total > 0 ? Math.Clamp(100 * bytes / total, 0, 100) : 100;
        RepositoryUploadProgress.Value = percent;
        RepositoryUploadMessage.Text = T("repository.upload_percent", percent, UploadSize(bytes), UploadSize(total), UploadSize(bytesPerSecond));
    }

    private static string UploadSize(double bytes) => bytes >= 1024 * 1024
        ? T("repository.upload_mib", bytes / (1024 * 1024))
        : T("repository.upload_kib", bytes / 1024);


    private void ResetRepositoryUploads()
    {
        repositoryUploadedFile = repositoryUploadedIcon = null;
        repositoryPendingFile?.Dispose();
        repositoryPendingIcon?.Dispose();
        repositoryPendingFile = repositoryPendingIcon = null;
        RepositoryUploadedFileName.Text = RepositoryUploadedIconName.Text = null;
        RepositoryStorageTitle.Text = RepositoryStorageDescription.Text = null;
        if (!repositoryUploading)
            RepositoryUploadMessage.Text = null;
    }

    private void RenderRepositoryFiles()
    {
        RepositoryFiles.Children.Clear();
        if (RepositoryTreeToggle.IsChecked == true)
        {
            RenderRepositoryTree(repositoryFiles.Select(file => new RepositoryTreeFile(file, false))
                .Concat(DraftAutomaticDependencies().Select(file => new RepositoryTreeFile(DependencyFile(file), true))));
            return;
        }
        var explicitProjects = repositoryFiles.Select(file => file.ProjectId).OfType<string>().ToHashSet();
        var mods = resolvedRepositoryMods.Where(file => file.ProjectId is not null).ToDictionary(file => file.ProjectId!);
        foreach (var file in repositoryFiles)
        {
            var dependencies = FindAutomaticDependencies(file.ProjectId, mods, explicitProjects);
            RepositoryFiles.Children.Add(CreateRepositoryFileCard(file, automatic: false, dependencies));
        }
    }

    private IEnumerable<RepositoryArtifact> DraftAutomaticDependencies()
    {
        var projects = repositoryFiles.Select(file => file.ProjectId).OfType<string>().ToHashSet();
        var mods = resolvedRepositoryMods.Where(file => file.ProjectId is not null).ToDictionary(file => file.ProjectId!);
        return projects.SelectMany(project => FindAutomaticDependencies(project, mods, projects))
            .DistinctBy(file => file.ProjectId);
    }

    private static IReadOnlyList<RepositoryArtifact> FindAutomaticDependencies(string? project,
        Dictionary<string, RepositoryArtifact> mods, HashSet<string> explicitProjects)
    {
        if (project is null || !mods.TryGetValue(project, out var root))
            return [];
        var visited = new HashSet<string> { project };
        var pending = new Queue<string>(root.Dependencies);
        var result = new List<RepositoryArtifact>();
        while (pending.TryDequeue(out var id))
        {
            if (!visited.Add(id) || !mods.TryGetValue(id, out var dependency))
                continue;
            if (!explicitProjects.Contains(id))
                result.Add(dependency);
            foreach (var child in dependency.Dependencies)
                pending.Enqueue(child);
        }
        return result;
    }

    private Control CreateDependencyCards(IReadOnlyList<RepositoryArtifact> dependencies)
    {
        var chips = new WrapPanel();
        var details = new ContentControl { IsVisible = false };
        foreach (var dependency in dependencies)
        {
            var file = DependencyFile(dependency);
            var chip = new Button
            {
                Content = CreateCard(dependency.Card, compact: true),
                Margin = new Thickness(0, 0, 5, 5),
                Padding = new Thickness(6, 3),
                BorderThickness = new Thickness(2, 0, 0, 0),
                BorderBrush = TypeBrush(dependency.Type)
            };
            ToolTip.SetTip(chip, dependency.Card.Title);
            chip.Click += (_, _) =>
            {
                if (Equals(details.Tag, dependency.ProjectId) && details.IsVisible)
                {
                    details.IsVisible = false;
                    return;
                }
                details.Tag = dependency.ProjectId;
                details.Content = CreateRepositoryFileCard(file, automatic: true, pin: CreateDependencyPin(file, dependency.ModVersionId));
                details.IsVisible = true;
            };
            chips.Children.Add(chip);
        }
        return new StackPanel
        {
            Spacing = 4,
            Children = { new TextBlock { Text = T("repository.dependencies"), FontSize = 12, Opacity = .8 }, chips, details }
        };
    }

    private static RepositoryFile DependencyFile(RepositoryArtifact artifact) =>
        MoveToFolder(new("", artifact.Type, artifact.Card, ProjectId: artifact.ProjectId), ArtifactFolder(artifact.Path));

    private Button CreateDependencyPin(RepositoryFile file, string? versionId)
    {
        var pin = new Button { Content = T("repository.pin_dependency"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        ToolTip.SetTip(pin, T("repository.pin_dependency_hint"));
        Avalonia.Automation.AutomationProperties.SetName(pin, T("repository.pin_dependency_hint"));
        pin.Click += (_, _) =>
        {
            if (repositoryFiles.Any(entry => entry.ProjectId == file.ProjectId))
                return;

            AddRepositoryFolder(file.Folder);
            repositoryFiles = [.. repositoryFiles, file with { VersionLock = versionId }];
            RenderRepositoryFiles();
        };
        return pin;
    }

    private Control CreateRepositoryFileCard(RepositoryFile file, bool automatic, IReadOnlyList<RepositoryArtifact>? dependencies = null, Button? pin = null)
    {
        var row = new StackPanel { Spacing = 6 };
        var card = CreateCard(file.Card);
        var sides = CreateFileSides(file, automatic);
        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
        if (pin is not null)
            actions.Children.Add(pin);
        actions.Children.Add(sides);
        Grid.SetColumn(actions, 1);
        row.Children.Add(new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12, Children = { card, actions } });
        var location = new FolderChoice(file.EffectiveFolder).ToString();
        if (automatic)
        {
            var parents = resolvedRepositoryMods.Where(entry => entry.Dependencies.Contains(file.ProjectId))
                .Select(entry => entry.Card.Title).Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(title => title, StringComparer.CurrentCultureIgnoreCase).ToArray();
            var label = parents.Length == 0 ? T("repository.dependency") : T("repository.dependency_by", string.Join(", ", parents));
            row.Children.Add(new TextBlock { Text = label, Opacity = .8, TextWrapping = TextWrapping.Wrap });
        }
        row.Children.Add(new TextBlock { Text = location });
        var buttons = new WrapPanel();
        var type = CreateTypePicker(file.Type);
        type.SelectionChanged += (_, _) =>
        {
            var chosen = PickerType(type);
            if (chosen == file.Type)
                return;
            UpdateRepositoryFile(file, file with { Type = chosen }, automatic);
        };
        buttons.Children.Add(type);
        if (!automatic)
        {
            var remove = new Button { Content = T("repository.remove") };
            remove.Click += (_, _) =>
            {
                repositoryFiles = repositoryFiles.Where(entry => !ReferenceEquals(entry, file)).ToArray();
                RenderRepositoryFiles();
            };
            buttons.Children.Add(remove);
            if (file.ProjectId is { } project)
                buttons.Children.Add(CreateLockPicker(file, project));
        }
        row.Children.Add(buttons);
        var editor = CreateCardEditor(file, automatic);
        var edit = new Button { Content = T("repository.edit_card") };
        if (automatic)
            ToolTip.SetTip(edit, T("repository.pin_dependency_hint"));
        edit.Click += (_, _) => editor.IsVisible = !editor.IsVisible;
        buttons.Children.Add(edit);
        row.Children.Add(editor);
        if (dependencies is { Count: > 0 })
            row.Children.Add(CreateDependencyCards(dependencies));
        return new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            Background = Brush.Parse("#222D3D"),
            BorderThickness = new Thickness(4, 0, 0, 0),
            BorderBrush = TypeBrush(file.Type),
            Child = row
        };
    }

    private Control CreateFileSides(RepositoryFile file, bool automatic)
    {
        var resolved = resolvedRepositoryMods.FirstOrDefault(entry => file.ProjectId is not null && entry.ProjectId == file.ProjectId);
        var auto = file.ClientSide is null && file.ServerSide is null;
        var server = file.ServerSide ?? resolved?.ServerSide ?? true;
        var client = file.ClientSide ?? resolved?.ClientSide ?? true;

        var autoCheck = SideCheck("A", "repository.side_auto", auto);
        var serverCheck = SideCheck("S", "repository.side_server", server);
        var clientCheck = SideCheck("C", "repository.side_client", client);
        serverCheck.IsEnabled = clientCheck.IsEnabled = !auto;
        autoCheck.Click += (_, _) => UpdateRepositoryFile(file, auto
            ? file with { ClientSide = client, ServerSide = server }
            : file with { ClientSide = null, ServerSide = null }, automatic);
        serverCheck.Click += (_, _) => UpdateRepositoryFile(file,
            file with { ClientSide = client, ServerSide = serverCheck.IsChecked == true }, automatic);
        clientCheck.Click += (_, _) => UpdateRepositoryFile(file,
            file with { ClientSide = clientCheck.IsChecked == true, ServerSide = server }, automatic);

        return new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
            Children = { autoCheck, serverCheck, clientCheck }
        };

    }

    private void UpdateRepositoryFile(RepositoryFile file, RepositoryFile updated, bool automatic = false)
    {
        AddRepositoryFolder(updated.Folder);
        repositoryFiles = automatic ? [.. repositoryFiles, updated] :
            repositoryFiles.Select(entry => ReferenceEquals(entry, file) ? updated : entry).ToArray();
        RenderRepositoryFiles();
    }

    private static CheckBox SideCheck(string label, string key, bool value)
    {
        var check = new CheckBox { Content = label, IsChecked = value, MinWidth = 0 };
        var description = T(key);
        ToolTip.SetTip(check, description);
        Avalonia.Automation.AutomationProperties.SetName(check, description);
        return check;
    }


    private static ComboBox CreateTypePicker(RepositoryFileType selected)
    {
        var options = Enum.GetValues<RepositoryFileType>().Select(type => new RepositoryType(type)).ToArray();
        var picker = new ComboBox { ItemsSource = options, SelectedItem = options.First(option => option.Type == selected) };
        Avalonia.Automation.AutomationProperties.SetName(picker, T("repository.type"));
        return picker;
    }

    private static RepositoryFileType PickerType(ComboBox picker) =>
        (picker.SelectedItem as RepositoryType)?.Type ?? RepositoryFileType.Strong;

    private static IBrush TypeBrush(RepositoryFileType type) => Brush.Parse(type switch
    {
        RepositoryFileType.Strong => "#E77B72",
        RepositoryFileType.Soft => "#58AEEC",
        _ => "#8FCB82"
    });

    private Control CreateCardEditor(RepositoryFile file, bool automatic)
    {
        var title = new TextBox { Text = file.Card.Title, Watermark = T("repository.title") };
        var description = new TextBox { Text = file.Card.Description, Watermark = T("repository.description") };
        var iconUrl = file.Card.IconUrl;
        var uploadedName = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var upload = new Button { Content = T("repository.choose_icon") };
        upload.Click += async (_, _) => await ChooseRepositoryUploadAsync(icon: true, (uploaded, name) =>
        {
            iconUrl = RepositoryStorage.FileUrl(uploaded.Domain, uploaded.FileId);
            uploadedName.Text = name;
        });
        var apply = new Button { Content = T("repository.apply_card") };
        apply.Click += (_, _) =>
        {
            if (Value(title).Length == 0)
                return;
            var card = new ModCard(Value(title), Value(description), iconUrl);
            UpdateRepositoryFile(file, file with { Card = card }, automatic);
        };
        return new StackPanel
        {
            IsVisible = false, Spacing = 6,
            Children = { title, description, upload, uploadedName, apply }
        };
    }

    private Control CreateLockPicker(RepositoryFile file, string project)
    {
        var panel = new WrapPanel();
        var button = new Button { Content = file.VersionLock ?? T("repository.lock") };
        var picker = new ComboBox { IsVisible = false };
        button.Click += async (_, _) => await RunRepositoryAsync(async () =>
        {
            var versions = await Api.GetFromJsonAsync($"{RepositoryPath}/mods/{Uri.EscapeDataString(project)}/versions?{Compatibility()}",
                AdminJsonContext.Default.ModVersionChoiceArray) ?? [];
            RepositoryVersion[] options = [new(null, T("repository.automatic")),
                .. versions.Select(v => new RepositoryVersion(v.Id, v.Number))];
            picker.ItemsSource = options;
            picker.SelectedItem = options.FirstOrDefault(option => option.Id == file.VersionLock);
            picker.IsVisible = true;
        });
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is RepositoryVersion version && version.Id != file.VersionLock)
                UpdateRepositoryFile(file, file with { VersionLock = version.Id });
        };
        panel.Children.Add(button);
        panel.Children.Add(picker);
        return panel;
    }

    private static Control CreateCard(ModCard card, bool compact = false)
    {
        var size = compact ? 18 : 40;
        var image = new Image { Width = size, Height = size, Stretch = Stretch.UniformToFill };
        var placeholder = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(6),
            Background = Brush.Parse("#344259"), ClipToBounds = true, Child = image };
        var details = new StackPanel { Spacing = 3 };
        details.Children.Add(new TextBlock { Text = card.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!compact)
            details.Children.Add(new TextBlock { Text = card.Description, TextWrapping = TextWrapping.Wrap, Opacity = .75 });
        var cardLayout = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10 };
        cardLayout.Children.Add(placeholder);
        Grid.SetColumn(details, 1);
        cardLayout.Children.Add(details);
        if (!string.IsNullOrWhiteSpace(card.IconUrl))
            _ = LoadRepositoryIconAsync(image, card.IconUrl);
        return cardLayout;
    }

    private static async Task LoadRepositoryIconAsync(Image image, string url)
    {
        var source = ModrinthAssets.IsCdnUrl(url)
            ? $"{RepositoryPath}/mod-icon?url={Uri.EscapeDataString(url)}" : url;
        if (!Uri.TryCreate(Api.BaseAddress, source, out var address) ||
            address.GetLeftPart(UriPartial.Authority) != Api.BaseAddress!.GetLeftPart(UriPartial.Authority))
            return;
        try
        {
            using var stream = new MemoryStream(await Api.GetByteArrayAsync(address), writable: false);
            image.Source = Bitmap.DecodeToWidth(stream, (int)image.Width);
        }
        catch (Exception error) when (error is HttpRequestException or ArgumentException or InvalidOperationException or IOException or OperationCanceledException)
        {
        }
    }

    private async Task RunRepositoryAsync(Func<Task> action)
    {
        RepositoryPanel.IsEnabled = false;
        RepositoryMessage.Text = null;
        try
        {
            await action();
        }
        catch (HttpRequestException error)
        {
            RepositoryMessage.Text = T(error.StatusCode switch
            {
                System.Net.HttpStatusCode.BadRequest => "repository.invalid",
                System.Net.HttpStatusCode.Conflict => "repository.conflict",
                _ => "common.failed"
            });
        }
        catch (Exception error) when (error is JsonException or OperationCanceledException or IOException)
        {
            RepositoryMessage.Text = T("common.failed");
        }
        finally
        {
            RepositoryPanel.IsEnabled = true;
        }
    }

    private RepositoryFileType SelectedType() =>
        (RepositoryTypePicker.SelectedItem as RepositoryType)?.Type ?? RepositoryFileType.Strong;

    private string Compatibility() =>
        $"minecraft={Uri.EscapeDataString((RepositoryMinecraft.SelectedItem as string ?? ""))}&loader={Uri.EscapeDataString(((RepositoryLoader.SelectedItem as RepositoryLoaderChoice)?.Loader.Id ?? ""))}";

    private static string Value(TextBox input) => input.Text?.Trim() ?? "";
}

internal sealed record BrowserUploadResponse(int Status, string Body);
