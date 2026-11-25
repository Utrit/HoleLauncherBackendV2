using System.Net.Http.Json;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using HoleLauncherBackend.Contracts;
using static HoleLauncher.Admin.Locale;

namespace HoleLauncher.Admin;

public sealed partial class AccountView
{
    private readonly List<RepositoryFile> offerFiles = [];
    private bool offerBusy;
    private readonly Dictionary<Guid, RepositoryFile> pendingOfferAcceptances = [];

    private sealed record OfferTargetChoice(OfferTarget Target)
    {
        public override string ToString()
        {
            var channel = Target.Stage && Target.Testing
                ? $"{T("repository.stage")} / {T("repository.testing")}"
                : T(Target.Testing ? "repository.testing" : "repository.stage");
            return $"{Target.Name} · {Target.Version} · {channel}";
        }
    }

    private async void OnOfferNav(object? sender, RoutedEventArgs e)
    {
        SelectSection(OfferNav);
        await RunOfferAsync(LoadOfferTargetsAsync);
    }

    private async Task LoadOfferTargetsAsync()
    {
        var selected = (OfferBuild.SelectedItem as OfferTargetChoice)?.Target;
        var targets = await Api.GetFromJsonAsync($"{RepositoryPath}/offer-targets",
            AdminJsonContext.Default.OfferTargetArray) ?? [];
        var choices = targets.Reverse().Select(target => new OfferTargetChoice(target)).ToArray();
        OfferBuild.ItemsSource = choices;
        OfferBuild.SelectedItem = selected is null ? null : choices.FirstOrDefault(choice =>
            choice.Target.BuildId == selected.BuildId && choice.Target.Version == selected.Version);
        if (choices.Length == 0)
            OfferMessage.Text = T("offer.no_targets");
    }

    private void OnOfferBuildChanged(object? sender, SelectionChangedEventArgs e)
    {
        OfferHits?.Children.Clear();
        if (OfferSelected is not null)
            RenderOfferFiles();
    }


    private async void OnOfferSearch(object? sender, RoutedEventArgs e) => await SearchOfferAsync();

    private async void OnOfferSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await SearchOfferAsync();
    }

    private Task SearchOfferAsync() => RunOfferAsync(async () =>
    {
        if (OfferBuild.SelectedItem is not OfferTargetChoice { Target: var target })
            return;
        if (ModrinthProjectFromLink(Value(OfferQuery)) is { } id)
        {
            OfferHits.Children.Clear();
            var hit = await FindModrinthLinkAsync(id, target.Minecraft, target.Loader, OfferMessage);
            if (hit is not null)
            {
                if (offerFiles.All(file => file.ProjectId != hit.ProjectId))
                    offerFiles.Add(new("", RepositoryFileType.Strong, hit.Card, ProjectId: hit.ProjectId));
                RenderOfferFiles();
                OfferMessage.Text = T("repository.link_added", hit.Card.Title);
            }
            return;
        }

        var query = Uri.EscapeDataString(Value(OfferQuery));
        var hits = await Api.GetFromJsonAsync($"{RepositoryPath}/mods?query={query}&minecraft={Uri.EscapeDataString(target.Minecraft)}&loader={Uri.EscapeDataString(target.Loader)}",
            AdminJsonContext.Default.ModSearchHitArray) ?? [];
        OfferHits.Children.Clear();
        foreach (var hit in hits)
        {
            var add = new Button { Content = T("repository.add") };
            add.Click += (_, _) =>
            {
                if (offerFiles.All(file => file.ProjectId != hit.ProjectId))
                    offerFiles.Add(new("", RepositoryFileType.Strong, hit.Card, ProjectId: hit.ProjectId));
                RenderOfferFiles();
            };
            var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10, Children = { CreateCard(hit.Card), add } };
            Grid.SetColumn(add, 1);
            OfferHits.Children.Add(row);
        }
    });

    private async void OnOfferUpload(object? sender, RoutedEventArgs e)
    {
        var account = currentAccountId;
        try
        {
            using var file = await Program.ChooseRepositoryFile(false);
            if (file is null || account != currentAccountId || activeSection != OfferNav)
                return;

            await RunOfferAsync(async () =>
            {
                if (IsZip(file))
                {
                    await ImportZipAsync(file, "", AddAsync, _ => { }, message => OfferMessage.Text = message);
                    return;
                }

                await AddAsync(file, "", file.GetPropertyAsString("name") ?? "");
            });

            async Task<bool> AddAsync(JSObject entry, string folder, string name)
            {
                if (account != currentAccountId || offerFiles.Count >= MaxZipFiles)
                    return false;
                var result = await UploadOfferAssetAsync(entry, false, account);
                if (result is null)
                    return false;
                offerFiles.Add(new(folder, RepositoryFileType.Strong, new(name, "", null),
                    StorageDomain: result.Domain, StorageId: result.FileId));
                RenderOfferFiles();
                return true;
            }
        }
        catch (JSException)
        {
            OfferMessage.Text = T("repository.upload_failed");
        }
    }

    private async Task<StorageUploadAccepted?> PickOfferAssetAsync(bool icon)
    {
        if (offerBusy)
            return null;

        var account = currentAccountId;
        using var file = await Program.ChooseRepositoryFile(icon);
        if (file is null || account != currentAccountId || activeSection != OfferNav)
            return null;

        StorageUploadAccepted? result = null;
        await RunOfferAsync(async () => result = await UploadOfferAssetAsync(file, icon, account));
        return result;
    }

    private async Task<StorageUploadAccepted?> UploadOfferAssetAsync(JSObject file, bool icon, Guid? account)
    {
        var path = $"{RepositoryPath}/offer-upload?icon={icon.ToString().ToLowerInvariant()}";
        var raw = await Program.UploadRepositoryFile(path, file, (bytes, total, speed) =>
            OfferMessage.Text = T("repository.upload_percent", total > 0 ? 100 * bytes / total : 100,
                UploadSize(bytes), UploadSize(total), UploadSize(speed)));
        var response = JsonSerializer.Deserialize(raw, AdminJsonContext.Default.BrowserUploadResponse) ?? throw new JsonException();
        if (response.Status != 202)
        {
            OfferMessage.Text = T(icon && response.Status == 400 ? "repository.invalid_icon" : "repository.upload_failed");
            return null;
        }

        var uploaded = JsonSerializer.Deserialize(response.Body, AdminJsonContext.Default.StorageUploadAccepted) ?? throw new JsonException();
        if (!await WaitForOperationAsync(uploaded.OperationId, T("repository.upload_failed"), text => OfferMessage.Text = text) ||
            account != currentAccountId)
            return null;

        OfferMessage.Text = T("repository.uploaded");
        return uploaded;
    }


    private async void OnSubmitOffer(object? sender, RoutedEventArgs e) => await RunOfferAsync(async () =>
    {
        if (offerFiles.Count == 0 || OfferBuild.SelectedItem is not OfferTargetChoice { Target: var target })
        {
            OfferMessage.Text = T("offer.incomplete");
            return;
        }

        using var response = await Api.PutAsJsonAsync($"{RepositoryPath}/offers",
            new OfferDraft(target.BuildId, target.Version, [.. offerFiles], Value(OfferDescription)), AdminJsonContext.Default.OfferDraft);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            OfferMessage.Text = T("repository.dependencies_failed");
            return;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await LoadOfferTargetsAsync();
            OfferMessage.Text = T("offer.target_unpublished");
            return;
        }
        response.EnsureSuccessStatusCode();
        ResetOffer();
        OfferMessage.Text = T("offer.sent");
    });

    private void ResetOffer()
    {
        offerFiles.Clear();
        OfferQuery.Text = OfferMessage.Text = OfferDescription.Text = null;
        OfferBuild.SelectedIndex = -1;
        OfferHits?.Children.Clear();
        if (OfferSelected is not null)
            RenderOfferFiles();
    }

    private void RenderOfferFiles()
    {
        OfferSelected.Children.Clear();
        for (var index = 0; index < offerFiles.Count; index++)
        {
            var file = offerFiles[index];
            var fileIndex = index;
            var remove = new Button { Content = T("offer.remove") };
            remove.Click += (_, _) =>
            {
                offerFiles.RemoveAll(entry => file.StorageId is { } id ? entry.StorageId == id : entry.ProjectId == file.ProjectId);
                RenderOfferFiles();
            };
            var summary = CreateCard(file.Card);
            var card = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10, Children = { summary, remove } };
            Grid.SetColumn(remove, 1);
            var target = (OfferBuild.SelectedItem as OfferTargetChoice)?.Target;
            var folders = (target?.Folders ?? []).SelectMany(FolderAndParents)
                .Append("").Append("mods").Append(file.EffectiveFolder)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                .Select(path => new FolderChoice(path)).ToArray();
            var destination = new ComboBox { ItemsSource = folders, SelectedItem = folders.First(choice =>
                string.Equals(choice.Path, file.EffectiveFolder, StringComparison.OrdinalIgnoreCase)) };
            AutomationProperties.SetName(destination, T("repository.folder_destination"));
            destination.SelectionChanged += (_, _) =>
            {
                if (destination.SelectedItem is FolderChoice choice)
                    offerFiles[fileIndex] = MoveToFolder(offerFiles[fileIndex], choice.Path);
            };
            if (file.StorageId is not { } fileId)
            {
                OfferSelected.Children.Add(new StackPanel { Spacing = 5, Children = { card, destination } });
                continue;
            }

            var description = new TextBox { Text = file.Card.Description, Watermark = T("repository.description"), MaxLength = 2000 };
            description.TextChanged += (_, _) => UpdateOfferCard(fileId, value => value with { Description = Value(description) });
            var image = new Button { Content = T("repository.choose_icon") };
            image.Click += async (_, _) =>
            {
                if (await PickOfferAssetAsync(icon: true) is not { } uploaded)
                    return;
                UpdateOfferCard(fileId, value => value with { IconUrl = RepositoryStorage.FileUrl(uploaded.Domain, uploaded.FileId) });
                RenderOfferFiles();
            };
            card.Children.Remove(summary);
            image.Margin = new Thickness(10, 0, 0, 0);
            var details = new WrapPanel { Children = { summary, image } };
            card.Children.Insert(0, details);
            OfferSelected.Children.Add(new StackPanel { Spacing = 5, Children = { card, destination, description } });
        }
    }

    private void UpdateOfferCard(Guid fileId, Func<ModCard, ModCard> update)
    {
        var index = offerFiles.FindIndex(file => file.StorageId == fileId);
        if (index >= 0)
            offerFiles[index] = offerFiles[index] with { Card = update(offerFiles[index].Card) };
    }

    private async Task RunOfferAsync(Func<Task> action)
    {
        if (offerBusy)
            return;
        offerBusy = true;
        OfferPanel.IsEnabled = false;
        OfferMessage.Text = null;
        try { await action(); }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException or JSException)
        {
            OfferMessage.Text = T("common.failed");
        }
        finally
        {
            offerBusy = false;
            OfferPanel.IsEnabled = true;
        }
    }

    private async void OnRefreshOffers(object? sender, RoutedEventArgs e) => await RunRepositoryAsync(LoadRepositoryAsync);

    private void RenderRepositoryOffers()
    {
        RepositoryOffers.Children.Clear();
        foreach (var offer in (repository.Offers ?? []).Reverse())
        {
            var pending = offer.Files.Where(file => file.Accepted is null && !pendingOfferAcceptances.ContainsKey(file.Id)).ToArray();
            if (pending.Length == 0)
                continue;

            var group = new StackPanel { Spacing = 8 };
            var name = offer.BuildVersion is { } version ? $"{offer.Name} · {version}" : offer.Name;
            group.Children.Add(CreateOfferReviewRow(CreateCard(new ModCard(name, offer.Description ?? "", offer.ImageUrl)),
                offer, pending, "offer.accept_all", "offer.reject_all"));
            var channel = offer.Test is { } test ? $" · {T(test ? "repository.testing" : "repository.stage")}" : "";
            group.Children.Add(new TextBlock { Text = $"{offer.UserName} · {offer.Minecraft} · {offer.Loader}{channel}", Opacity = .8 });
            foreach (var file in pending)
            {
                var destination = new TextBlock { Text = new FolderChoice(file.File.EffectiveFolder).ToString(), Opacity = .75 };
                var details = new StackPanel { Spacing = 3, Children = { CreateCard(file.File.Card), destination } };
                group.Children.Add(CreateOfferReviewRow(details, offer, [file], "offer.accept", "offer.reject"));
            }
            RepositoryOffers.Children.Add(new Border { Padding = new Thickness(10), CornerRadius = new CornerRadius(6),
                Background = Brush.Parse("#1B2533"), Child = group });
        }
        if (RepositoryOffers.Children.Count == 0)
            RepositoryOffers.Children.Add(new TextBlock { Text = T("offer.empty") });
    }

    private Control CreateOfferReviewRow(Control content, RepositoryOffer offer, OfferFile[] files, string acceptKey, string rejectKey)
    {
        var accept = new Button { Content = T(acceptKey), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var reject = new Button { Content = T(rejectKey), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        accept.Click += async (_, _) => await ReviewOfferAsync(offer, files, true);
        reject.Click += async (_, _) => await ReviewOfferAsync(offer, files, false);

        var actions = new UniformGrid { Rows = 1, Columns = 2, ColumnSpacing = 4, Width = 244,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Children = { accept, reject } };
        Grid.SetColumn(actions, 1);
        return new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12, Children = { content, actions } };
    }

    private Task ReviewOfferAsync(RepositoryOffer offer, OfferFile[] items, bool accept) => RunRepositoryAsync(async () =>
    {

        if (accept)
        {
            if (Value(RepositoryName).Length == 0 || (offer.BuildId is { } buildId && selectedRepositoryVersion?.BuildId != buildId) ||
                !Equals(RepositoryMinecraft.SelectedItem, offer.Minecraft) ||
                (RepositoryLoader.SelectedItem as RepositoryLoaderChoice)?.Loader.Id != offer.Loader)
            {
                RepositoryMessage.Text = T("offer.build_required");
                return;
            }

            var next = repositoryFiles.ToList();
            foreach (var item in items)
            {
                var file = item.File;
                if (!next.Any(value => file.ProjectId is not null ? value.ProjectId == file.ProjectId :
                    value.StorageDomain == file.StorageDomain && value.StorageId == file.StorageId))
                    next.Add(file);
            }

            repositoryFiles = next.ToArray();
            foreach (var file in repositoryFiles)
                AddRepositoryFolder(file.Folder);
            foreach (var item in items)
                pendingOfferAcceptances[item.Id] = item.File;
            RenderRepositoryFiles();
            RenderRepositoryOffers();
            RepositoryMessage.Text = T("offer.accepted");
            return;
        }

        using var reply = await Api.PutAsJsonAsync($"{RepositoryPath}/offers/{offer.Id:D}/decision",
            new OfferDecision(items[0].Id, false, items.Select(item => item.Id).ToArray()), AdminJsonContext.Default.OfferDecision);
        reply.EnsureSuccessStatusCode();
        await LoadRepositoryAsync();
        RepositoryMessage.Text = T(items.Length == 1 ? "offer.rejected" : "offer.rejected_all");
    });
}
