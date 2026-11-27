using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using HoleLauncherBackend.Contracts;

using static HoleLauncher.Admin.Locale;

namespace HoleLauncher.Admin;

public sealed partial class AccountView
{
    private const string GameProfilePath = "/api/auth/game-profile";
    private static readonly TimeSpan TextureWaitTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromMilliseconds(500);
    private Guid? gameAccountId;
    private Guid? cachedSkinId;
    private Guid? cachedCapeId;
    private int profileLoadVersion;
    private Guid? savedSkinId;
    private string savedSkinModel = SkinModels.Classic;
    private bool restoringSkinModel;
    private string SelectedSkinModel => SlimSkinSwitch.IsChecked == true ? SkinModels.Slim : SkinModels.Classic;

    private void ShowGameProfile(AccountIdentity? account)
    {
        if (gameAccountId != account?.AccountId)
        {
            ClearPreviewTextures();
            RemoveSkinButton.IsVisible = RemoveCapeButton.IsVisible = false;
            GameNameInput.Text = null;
            GameNameText.Text = null;
            GameNameHistoryText.Text = null;
            GameNameEditor.IsVisible = false;
            GameNotices.Children.Clear();
        }

        gameAccountId = account?.AccountId;
        GameProfilePanel.IsVisible = account is not null;
        GameRenameFields.IsVisible = account?.Permissions.Contains(Permissions.EditGameUser) == true;
        UploadSkinButton.IsVisible = account?.Permissions.Contains(Permissions.EditGameSkin) == true;
        SkinModelEditor.IsVisible = UploadSkinButton.IsVisible;
        UploadCapeButton.IsVisible = account?.Permissions.Contains(Permissions.EditGameCape) == true;
    }

    private async Task LoadGameProfileAsync()
    {
        if (gameAccountId is not { } accountId)
            return;
        var version = ++profileLoadVersion;
        try
        {
            var profile = await Api.GetFromJsonAsync(GameProfilePath, AdminJsonContext.Default.GameProfile);
            if (profile is null || gameAccountId != accountId || version != profileLoadVersion)
                return;

            GameNameInput.Text = profile.GameName;
            GameNameText.Text = profile.GameName ?? T("game.name_empty");
            GameNameHistoryText.Text = profile.RecentNames is { Length: > 0 } names
                ? string.Join(Environment.NewLine, names)
                : profile.GameName ?? T("game.history_empty");
            UploadSkinButton.Content = profile.SkinFileId is null ? T("game.upload_skin") : T("game.replace_skin");
            UploadCapeButton.Content = profile.CapeFileId is null ? T("game.upload_cape") : T("game.replace_cape");
            RemoveSkinButton.IsVisible = UploadSkinButton.IsVisible && profile.SkinFileId is not null;
            RemoveCapeButton.IsVisible = UploadCapeButton.IsVisible && profile.CapeFileId is not null;
            savedSkinId = profile.SkinFileId;
            savedSkinModel = profile.SkinModel;
            RestoreSkinModel();
            await Task.WhenAll(
                LoadPreviewTextureAsync(profile.SkinFileId, cachedSkinId, accountId, version, PlayerPreview.SetSkin, id => cachedSkinId = id),
                LoadPreviewTextureAsync(profile.CapeFileId, cachedCapeId, accountId, version, PlayerPreview.SetCape, id => cachedCapeId = id, cape: true));
            try
            {
                await PlayerPreview.LoadGeometryAsync();
                PreviewError.IsVisible = false;
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
            {
                PreviewError.IsVisible = true;
            }
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            if (gameAccountId == accountId)
                CreateGameNotice()(T("game.profile_load_failed"));
        }
    }

    private async Task LoadPreviewTextureAsync(Guid? fileId, Guid? cachedId, Guid accountId, int version,
        Action<byte[]?> setTexture, Action<Guid?> setCachedId, bool cape = false)
    {
        if (fileId == cachedId)
            return;

        setTexture(null);
        setCachedId(null);
        if (fileId is not { } id)
            return;

        var path = cape ? $"{GameProfilePath}/cape-preview/{id:D}" : $"/api/storage/userdata/{id:D}";
        using var response = await Api.GetAsync(path);
        if (!response.IsSuccessStatusCode || gameAccountId != accountId || version != profileLoadVersion)
            return;
        var png = await response.Content.ReadAsByteArrayAsync();
        if (gameAccountId != accountId || version != profileLoadVersion)
            return;

        setTexture(png);
        setCachedId(id);
    }

    private void ClearPreviewTextures()
    {
        PlayerPreview.SetSkin(null);
        PlayerPreview.SetCape(null);
        savedSkinId = cachedSkinId = cachedCapeId = null;
    }

    private void OnToggleGameName(object? sender, RoutedEventArgs e) =>
        GameNameEditor.IsVisible = !GameNameEditor.IsVisible;

    private async void OnSaveGameName(object? sender, RoutedEventArgs e) => await RunGameProfileActionAsync(async report =>
    {
        var change = new GameNameChange(GameNameInput.Text);
        using var response = await Api.PutAsJsonAsync($"{GameProfilePath}/name", change, AdminJsonContext.Default.GameNameChange);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            report(T("game.name_invalid"));
            return;
        }
        if (!await WaitForMutationAsync(response, T("game.name_save_failed"), report))
            return;

        report(T("game.name_saved"));
        await LoadGameProfileAsync();
    });

    private async void OnUploadSkin(object? sender, RoutedEventArgs e) => await PickTextureAsync("skin");

    private async void OnUploadCape(object? sender, RoutedEventArgs e) => await PickTextureAsync("cape");

    private async void OnRemoveSkin(object? sender, RoutedEventArgs e) => await RemoveTextureAsync("skin");

    private async void OnRemoveCape(object? sender, RoutedEventArgs e) => await RemoveTextureAsync("cape");

    private Task RemoveTextureAsync(string texture) => RunTextureActionAsync(texture, async report =>
    {
        using var response = await Api.DeleteAsync($"{GameProfilePath}/{texture}");
        if (!await WaitForMutationAsync(response, T("game.texture_failed"), report))
            return;
        report(T(texture == "skin" ? "game.skin_removed" : "game.cape_removed"));
        await LoadGameProfileAsync();
    });

    private async Task PickTextureAsync(string texture)
    {
        var picker = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (picker is null)
            return;
        var account = gameAccountId;
        var files = await picker.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = texture == "skin" ? T("game.choose_skin") : T("game.choose_cape"),
            FileTypeFilter = [FilePickerFileTypes.ImagePng]
        });
        if (files.Count == 0)
            return;

        using var file = files[0];
        if (gameAccountId == account && account is not null)
            await RunTextureActionAsync(texture, report => UploadTextureAsync(texture, file, report));
    }

    private async Task UploadTextureAsync(string texture, IStorageFile file, Action<string> report)
    {
        await using var png = await file.OpenReadAsync();
        using var content = new StreamContent(png);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Headers.TryAddWithoutValidation("X-Storage-File-Name", Uri.EscapeDataString(file.Name));
        report(T("common.loading"));
        var path = $"{GameProfilePath}/{texture}";
        using var response = await Api.PostAsync(path, content);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            report(texture == "skin"
                ? T("game.skin_dimensions")
                : T("game.cape_dimensions"));
            return;
        }
        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            report(T("game.file_too_large"));
            return;
        }
        response.EnsureSuccessStatusCode();

        var upload = await response.Content.ReadFromJsonAsync(AdminJsonContext.Default.StorageUploadAccepted)
            ?? throw new JsonException("The upload response is empty.");
        using var deadline = new CancellationTokenSource(TextureWaitTimeout);
        while (true)
        {
            using var status = await Api.GetAsync($"/api/auth/operations/{upload.OperationId:D}", deadline.Token);
            status.EnsureSuccessStatusCode();
            var operation = await status.Content.ReadFromJsonAsync(AdminJsonContext.Default.OperationStatus, deadline.Token);
            if (operation?.State == OperationState.Done)
                break;
            if (operation?.State == OperationState.Failed)
            {
                report(OperationError(operation.ErrorMessage, T("game.upload_failed")));
                return;
            }
            await Task.Delay(StatusPollInterval, deadline.Token);
        }

        var change = new GameTextureChange(upload.FileId, texture == "skin" ? SelectedSkinModel : null);
        using var saved = await Api.PutAsJsonAsync(path, change, AdminJsonContext.Default.GameTextureChange, deadline.Token);
        if (!await WaitForMutationAsync(saved, T("game.texture_failed"), report))
            return;
        report(texture == "skin" ? T("game.skin_saved") : T("game.cape_saved"));
        await LoadGameProfileAsync();
    }

    private Task RunTextureActionAsync(string texture, Func<Action<string>, Task> action) =>
        texture == "skin"
            ? RunGameProfileActionAsync(action, UploadSkinButton, RemoveSkinButton, SlimSkinSwitch)
            : RunGameProfileActionAsync(action, UploadCapeButton, RemoveCapeButton);

    private async void OnSkinModelSelected(object? sender, RoutedEventArgs e)
    {
        if (restoringSkinModel)
            return;

        var selected = SelectedSkinModel;
        PlayerPreview.SetModel(selected);
        if (savedSkinId is not { } id || selected == savedSkinModel)
            return;

        await RunTextureActionAsync("skin", async report =>
        {
            try
            {
                var change = new GameTextureChange(id, selected);
                using var response = await Api.PutAsJsonAsync($"{GameProfilePath}/skin", change, AdminJsonContext.Default.GameTextureChange);
                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    report(T("game.slim_dimensions"));
                    return;
                }
                if (!await WaitForMutationAsync(response, T("game.model_failed"), report))
                    return;

                savedSkinModel = selected;
                report(T("game.model_saved"));
            }
            finally
            {
                RestoreSkinModel();
            }
        });
    }

    private void RestoreSkinModel()
    {
        restoringSkinModel = true;
        try
        {
            SlimSkinSwitch.IsChecked = savedSkinModel == SkinModels.Slim;
            PlayerPreview.SetModel(savedSkinModel);
        }
        finally
        {
            restoringSkinModel = false;
        }
    }

    private Action<string> CreateGameNotice()
    {
        var accountId = gameAccountId;
        TextBlock? message = null;
        return text =>
        {
            if (accountId is null || gameAccountId != accountId)
                return;
            if (message is null)
            {
                message = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                var dismiss = new Button { Content = "×", Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Top };
                Avalonia.Automation.AutomationProperties.SetName(dismiss, T("common.dismiss"));
                Grid.SetColumn(dismiss, 1);
                var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
                layout.Children.Add(message);
                layout.Children.Add(dismiss);
                var notice = new Border
                {
                    Child = layout, Background = new SolidColorBrush(Color.Parse("#1A2230")),
                    Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(10)
                };
                dismiss.Click += (_, _) => GameNotices.Children.Remove(notice);
                GameNotices.Children.Insert(0, notice);
            }
            message.Text = text;
        };
    }

    private async Task RunGameProfileActionAsync(Func<Action<string>, Task> action, params Control[] controls)
    {
        if (controls.Length == 0)
            controls = [GameNameEditor];
        if (controls.Any(control => !control.IsEnabled))
            return;
        var report = CreateGameNotice();
        foreach (var control in controls)
            control.IsEnabled = false;
        try
        {
            await action(report);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException or OperationCanceledException)
        {
            report(T("game.profile_save_failed"));
        }
        finally
        {
            foreach (var control in controls)
                control.IsEnabled = true;
        }
    }
}
