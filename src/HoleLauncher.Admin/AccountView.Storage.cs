using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HoleLauncherBackend.Contracts;

using static HoleLauncher.Admin.Locale;

namespace HoleLauncher.Admin;

public sealed record StorageTreeNode(string Name, StorageTreeNode[] Children, StoredFile? File = null);

public sealed partial class AccountView
{
    private const string StorageAdminPath = "/api/auth/storage-admin";
    private bool canDeleteStorage;
    private bool canCleanHistory;
    private bool storageBusy;
    private Guid? confirmedDelete;
    private HistoryOwner? confirmedCleanup;

    private sealed record HistoryOwner(Guid? AccountId, string Name)
    {
        public override string ToString() => Name;
    }

    private void ConfigureStorage(string[] permissions, bool changingAccount)
    {
        canDeleteStorage = permissions.Contains(Permissions.StorageDelete);
        canCleanHistory = permissions.Contains(Permissions.DatabaseEdit);
        StorageFilesPanel.IsVisible = canDeleteStorage || permissions.Contains(Permissions.StorageEdit);
        StorageCleanupPanel.IsVisible = canCleanHistory;
        StorageNav.IsVisible = StorageFilesPanel.IsVisible || StorageCleanupPanel.IsVisible;
        StorageDeleteButton.IsVisible = canDeleteStorage;

        if (changingAccount || !StorageFilesPanel.IsVisible)
        {
            StorageTree.ItemsSource = null;
            StorageMessage.Text = null;
        }
        if (changingAccount || !canCleanHistory)
        {
            HistoryOwnerPicker.ItemsSource = null;
            HistoryCleanupMessage.Text = null;
        }
    }

    private async void OnStorageNav(object? sender, RoutedEventArgs e)
    {
        SelectSection(StorageNav);
        await LoadStorageAsync();
    }

    private async void OnRefreshStorage(object? sender, RoutedEventArgs e) => await LoadStorageAsync();

    private Task LoadStorageAsync() => RunStorageAsync(async () =>
    {
        var accountId = currentAccountId;
        if (accountId is null)
            return;
        var owners = await Api.GetFromJsonAsync($"{StorageAdminPath}/users", AdminJsonContext.Default.AdminAccountNameArray) ?? [];
        if (currentAccountId != accountId)
            return;
        if (canCleanHistory)
        {
            var selectedId = (HistoryOwnerPicker.SelectedItem as HistoryOwner)?.AccountId;
            var hadSelection = HistoryOwnerPicker.SelectedItem is not null;
            HistoryOwner[] choices = [new(null, T("storage.all_players")),
                ..owners.OrderBy(owner => owner.UserName).Select(owner => new HistoryOwner(owner.AccountId, owner.UserName))];
            HistoryOwnerPicker.ItemsSource = choices;
            HistoryOwnerPicker.SelectedItem = hadSelection ? choices.FirstOrDefault(owner => owner.AccountId == selectedId) : null;
        }

        if (!StorageFilesPanel.IsVisible)
            return;
        var files = await Api.GetFromJsonAsync($"{StorageAdminPath}/files", AdminJsonContext.Default.StoredFileArray) ?? [];
        if (currentAccountId != accountId || !StorageFilesPanel.IsVisible)
            return;
        var names = owners.ToDictionary(owner => owner.AccountId, owner => owner.UserName);
        StorageTree.ItemsSource = files.GroupBy(file => file.Domain)
            .Select(group => new StorageTreeNode(FolderName(group.Key, group.First().Metadata.UploadedBy, names),
                group.OrderByDescending(file => file.Metadata.UploadedAt)
                    .Select(file => new StorageTreeNode(FileName(file), [], file)).ToArray()))
            .OrderBy(folder => folder.Name).ToArray();
        StorageMessage.Text = files.Length == 0 ? T("storage.empty") : null;
    });

    private static string FolderName(string domain, Guid owner, Dictionary<Guid, string> names) =>
        StorageAddress.TryGetUserDataOwner(domain, out var account)
            ? names.GetValueOrDefault(account, names.GetValueOrDefault(owner, T("storage.deleted_account")))
            : domain.Replace('-', ' ').Replace('_', ' ');

    private static string FileName(StoredFile file) =>
        file.Metadata.FileName ?? T("storage.file_date", T(file.Label ?? "storage.file"), file.Metadata.UploadedAt.LocalDateTime);

    private void OnStorageSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        confirmedDelete = null;
        StorageDeleteButton.Content = T("storage.delete");
        var file = (StorageTree.SelectedItem as StorageTreeNode)?.File;
        StorageSelectedPanel.IsVisible = file is not null;
        if (file is null)
            return;
        var metadata = file.Metadata;
        StorageFileDetails.Text = T("storage.details", FileName(file), metadata.Size, metadata.UploadedAt.LocalDateTime, metadata.Sha512);
        StorageDownload.NavigateUri = new Uri($"{Program.GetOrigin()}{StorageAdminPath}/files/{file.Domain}/{metadata.FileId:D}");
    }

    private async void OnDeleteStorageFile(object? sender, RoutedEventArgs e)
    {
        if (!canDeleteStorage || (StorageTree.SelectedItem as StorageTreeNode)?.File is not { } file)
            return;
        if (confirmedDelete != file.Metadata.FileId)
        {
            confirmedDelete = file.Metadata.FileId;
            StorageDeleteButton.Content = T("storage.confirm_delete");
            return;
        }

        var removed = false;
        await RunStorageAsync(async () =>
        {
            using var response = await Api.DeleteAsync($"{StorageAdminPath}/files/{file.Domain}/{file.Metadata.FileId:D}");
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                StorageMessage.Text = T("storage.in_use");
                return;
            }
            response.EnsureSuccessStatusCode();
            removed = true;
        });
        confirmedDelete = null;
        StorageDeleteButton.Content = T("storage.delete");
        if (removed)
        {
            await LoadStorageAsync();
            StorageMessage.Text = T("storage.deleted");
        }
    }

    private void OnHistoryOwnerChanged(object? sender, SelectionChangedEventArgs e)
    {
        confirmedCleanup = null;
        HistoryCleanupButton.IsEnabled = HistoryOwnerPicker.SelectedItem is not null;
        HistoryCleanupButton.Content = T("storage.clean");
    }

    private async void OnCleanHistory(object? sender, RoutedEventArgs e)
    {
        if (!canCleanHistory || HistoryOwnerPicker.SelectedItem is not HistoryOwner owner)
            return;
        if (confirmedCleanup != owner)
        {
            confirmedCleanup = owner;
            HistoryCleanupButton.Content = T("storage.confirm_cleanup");
            HistoryCleanupMessage.Text = owner.AccountId is null
                ? T("storage.cleanup_all")
                : T("storage.cleanup_one", owner.Name);
            return;
        }

        await RunStorageAsync(async () =>
        {
            var request = new HistoryCleanupRequest(owner.AccountId, All: owner.AccountId is null);
            using var response = await Api.PostAsJsonAsync($"{StorageAdminPath}/history/cleanup", request,
                AdminJsonContext.Default.HistoryCleanupRequest);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync(AdminJsonContext.Default.HistoryCleanupResult)
                ?? throw new JsonException("The cleanup response is empty.");
            HistoryCleanupMessage.Text = T("storage.cleanup_result", result.NameEntries, result.TextureEntries);
        }, HistoryCleanupMessage);
        confirmedCleanup = null;
        HistoryCleanupButton.Content = T("storage.clean");
    }

    private async Task RunStorageAsync(Func<Task> action, TextBlock? message = null)
    {
        if (storageBusy)
            return;
        storageBusy = true;
        StoragePanel.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            (message ?? (StorageFilesPanel.IsVisible ? StorageMessage : HistoryCleanupMessage)).Text =
                T("common.failed");
        }
        finally
        {
            StoragePanel.IsEnabled = true;
            storageBusy = false;
        }
    }
}
