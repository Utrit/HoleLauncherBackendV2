using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HoleLauncherBackend.Contracts;

using static HoleLauncher.Admin.Locale;

namespace HoleLauncher.Admin;

public sealed partial class AccountView : UserControl
{
    private static readonly HttpClient Api = new() { BaseAddress = new Uri(Program.GetOrigin()) };

    public AccountView()
    {
        InitializeComponent();
        RepositoryFiles.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<RepositoryFile>(
            (file, _) => CreateFlatRepositoryCard(file));
        Program.RegisterRepositoryFind(
            () => RepositoryPanel.IsVisible && RepositoryBuildFilesTab.IsSelected, OpenRepositoryFind);
        AddHandler(PointerMovedEvent, OnRepositoryTreePointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnRepositoryTreePointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Program.RegisterBrowserPaste(() => FocusedPasteInput() is not null, PasteText);
        LanguagePicker.SelectedIndex = Program.GetLanguage() == "en" ? 1 : 0;
        SizeChanged += (_, _) => UpdateDashboardLayout();
        foreach (var permission in Permissions.All)
            PermissionFlags.Children.Add(new CheckBox { Content = T($"permission.{permission}"), Tag = permission });
        Loaded += async (_, _) => await UpdateSessionAsync();
    }

    private async Task UpdateSessionAsync()
    {
        await RunAsync(async () =>
        {
            using var response = await Api.GetAsync("/api/auth/me");
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                ShowAccount(null);
                return;
            }

            response.EnsureSuccessStatusCode();
            ShowAccount(await ReadAccountAsync(response));
            await LoadGameProfileAsync();
            await UpdateServiceStatusAsync();
            await LoadUsersAsync();
        });
    }

    private async void OnRefreshStatus(object? sender, RoutedEventArgs e) => await UpdateSessionAsync();

    private async void OnLogout(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("X-HoleLauncher-Request", "1");
        using var response = await Api.SendAsync(request);
        response.EnsureSuccessStatusCode();
        ShowAccount(null);
    });

    private void ShowAccount(AccountIdentity? account)
    {
        var changingAccount = currentAccountId != account?.AccountId;
        currentAccountId = account?.AccountId;
        Program.ShowLogin(account is null);
        Dashboard.IsVisible = account is not null;
        AccountTitle.Text = account?.UserName;
        if (changingAccount)
        {
            AccountOptions.IsVisible = false;
            activeSection = ProfileNav;
        }
        LogoutButton.IsVisible = account is not null;
        PermissionsPanel.IsVisible = account is not null;
        var permissions = account?.Permissions ?? [];
        PermissionsText.Text = permissions.Length == 0 ? T("account.no_permissions") : string.Join(Environment.NewLine, permissions.Select(permission => T($"permission.{permission}")));
        ServerNav.IsVisible = permissions.Contains(Permissions.ReadSystemStatus);
        PasswordPanel.IsVisible = account is not null;
        ShowGameProfile(account);
        UsersNav.IsVisible = permissions.Contains(Permissions.EditUsers);
        LauncherNav.IsVisible = permissions.Contains(Permissions.EditLauncher);
        RepositoryNav.IsVisible = permissions.Contains(Permissions.RepositoryEdit);
        OfferNav.IsVisible = permissions.Contains(Permissions.RepositoryOffer);
        if (changingAccount)
            ResetOffer();
        if (changingAccount)
            OnNewRepositoryBuild(null, new RoutedEventArgs());
        ConfigureStorage(permissions, changingAccount);
        UpdateSection();
        if (account is null)
        {
            NewPasswordInput.Clear();
            PasswordMessage.Text = null;
            UserPicker.ItemsSource = null;
            users = [];
        }
        ServiceRows.Children.Clear();
        ServicesMessage.Text = null;
        DashboardError.Text = null;
    }

    private async Task UpdateServiceStatusAsync()
    {
        if (!ServerNav.IsVisible)
            return;

        ServicesMessage.Text = T("common.loading");
        try
        {
            using var response = await Api.GetAsync("/api/status");
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                ShowAccount(null);
                return;
            }
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                ServerNav.IsVisible = false;
                UpdateSection();
                return;
            }

            response.EnsureSuccessStatusCode();
            var services = await response.Content.ReadFromJsonAsync(AdminJsonContext.Default.ServiceStatusArray)
                ?? throw new JsonException("The services response is empty.");
            foreach (var service in services)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
                var status = ServiceState(service.State);
                var state = new TextBlock { Text = service.UsedBytes is { } bytes
                    ? T("server.storage_usage", status, StorageSize(bytes)) : status };
                Grid.SetColumn(state, 1);
                row.Children.Add(new TextBlock { Text = ServiceName(service.Name), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                row.Children.Add(state);
                ServiceRows.Children.Add(row);
            }

            ServicesMessage.Text = null;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            ServicesMessage.Text = T("server.unavailable");
        }
    }

    private AccountIdentity[] users = [];

    private async Task LoadUsersAsync()
    {
        if (!UsersNav.IsVisible)
            return;

        using var response = await Api.GetAsync("/api/auth/users");
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            UsersNav.IsVisible = false;
            UpdateSection();
            return;
        }
        response.EnsureSuccessStatusCode();

        var selectedId = SelectedUser?.AccountId;
        users = await response.Content.ReadFromJsonAsync(AdminJsonContext.Default.AccountIdentityArray)
            ?? throw new JsonException("The users response is empty.");
        UserPicker.ItemsSource = users.Select(user => user.Suppressed ? T("users.suppressed_name", user.UserName) : user.UserName).ToArray();
        UserPicker.SelectedIndex = Array.FindIndex(users, user => user.AccountId == selectedId);
    }

    private AccountIdentity? SelectedUser =>
        UserPicker.SelectedIndex >= 0 && UserPicker.SelectedIndex < users.Length
            ? users[UserPicker.SelectedIndex]
            : null;

    private void OnUserSelected(object? sender, SelectionChangedEventArgs e)
    {
        var permissions = SelectedUser?.Permissions ?? [];
        foreach (var flag in PermissionFlags.Children.OfType<CheckBox>())
            flag.IsChecked = flag.Tag is string permission && permissions.Contains(permission);
        UserFlagsPanel.IsVisible = SelectedUser is not null;
        ToggleSuppressionButton.Content = SelectedUser?.Suppressed == true ? T("users.unblock") : T("users.block");
        UserManagementMessage.Text = null;
    }

    private async void OnSavePermissions(object? sender, RoutedEventArgs e)
    {
        if (SelectedUser is not { } user)
            return;

        SavePermissionsButton.IsEnabled = false;
        ToggleSuppressionButton.IsEnabled = false;
        UserPicker.IsEnabled = false;
        try
        {
            var flags = PermissionFlags.Children.OfType<CheckBox>()
                .Where(flag => flag.IsChecked == true).Select(flag => (string)flag.Tag!).ToArray();
            using var response = await Api.PutAsJsonAsync(
                $"/api/auth/users/{user.AccountId}/permissions", new PermissionChange(flags),
                AdminJsonContext.Default.PermissionChange);
            if (!await WaitForMutationAsync(response, T("users.permissions_failed")))
                return;

            await UpdateSessionAsync();
            UserManagementMessage.Text = T("users.permissions_saved");
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            UserManagementMessage.Text = T("users.permissions_failed");
        }
        finally
        {
            SavePermissionsButton.IsEnabled = true;
            ToggleSuppressionButton.IsEnabled = true;
            UserPicker.IsEnabled = true;
        }
    }

    private async void OnToggleSuppression(object? sender, RoutedEventArgs e)
    {
        if (SelectedUser is not { } user)
            return;

        var suppressed = !user.Suppressed;
        ToggleSuppressionButton.IsEnabled = false;
        SavePermissionsButton.IsEnabled = false;
        UserPicker.IsEnabled = false;
        try
        {
            using var response = await Api.PutAsJsonAsync(
                $"/api/auth/users/{user.AccountId}/suppression", new SuppressionChange(suppressed),
                AdminJsonContext.Default.SuppressionChange);
            if (!await WaitForMutationAsync(response, T("users.block_failed")))
                return;

            await UpdateSessionAsync();
            UserManagementMessage.Text = suppressed ? T("users.blocked") : T("users.unblocked");
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            UserManagementMessage.Text = T("users.block_failed");
        }
        finally
        {
            ToggleSuppressionButton.IsEnabled = true;
            SavePermissionsButton.IsEnabled = true;
            UserPicker.IsEnabled = true;
        }
    }


    private async Task<bool> WaitForMutationAsync(HttpResponseMessage response, string failureMessage, Action<string>? reportStatus = null)
    {
        reportStatus ??= text => UserManagementMessage.Text = text;
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            reportStatus(failureMessage);
            return false;
        }

        var accepted = await response.Content.ReadFromJsonAsync(AdminJsonContext.Default.OperationAccepted)
            ?? throw new JsonException("The accepted operation response is empty.");
        return await WaitForOperationAsync(accepted.OperationId, failureMessage, reportStatus);
    }

    private async Task<bool> WaitForOperationAsync(Guid operationId, string failureMessage, Action<string> reportStatus)
    {
        reportStatus(T("operation.pending"));
        var path = $"/api/auth/operations/{operationId}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                using var status = await Api.GetAsync(path, timeout.Token);
                if (status.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await UpdateSessionAsync();
                    return false;
                }
                if (status.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    await Task.Delay(500, timeout.Token);
                    continue;
                }
                status.EnsureSuccessStatusCode();
                var operation = await status.Content.ReadFromJsonAsync(AdminJsonContext.Default.OperationStatus, timeout.Token)
                    ?? throw new JsonException("The operation status response is empty.");
                if (operation.State == OperationState.Done)
                    return true;
                if (operation.State == OperationState.Failed)
                {
                    reportStatus(OperationError(operation.ErrorMessage, failureMessage));
                    return false;
                }

                await Task.Delay(500, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            reportStatus(T("operation.timeout", operationId));
            return false;
        }
    }

    private async void OnChangePassword(object? sender, RoutedEventArgs e)
    {
        var change = new PasswordChange(NewPasswordInput.Text ?? string.Empty);
        NewPasswordInput.Clear();
        ChangePasswordButton.IsEnabled = false;
        try
        {
            using var response = await Api.PostAsJsonAsync("/api/auth/password", change, AdminJsonContext.Default.PasswordChange);
            PasswordMessage.Text = response.StatusCode switch
            {
                HttpStatusCode.NoContent => T("account.password_saved"),
                HttpStatusCode.BadRequest => T("auth.invalid"),
                HttpStatusCode.Unauthorized => T("account.session_expired"),
                HttpStatusCode.TooManyRequests => T("auth.limited"),
                _ => T("account.password_failed")
            };
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            PasswordMessage.Text = T("account.password_failed");
        }
        finally
        {
            ChangePasswordButton.IsEnabled = true;
        }
    }


    private async Task RunAsync(Func<Task> action)
    {
        LogoutButton.IsEnabled = false;
        RefreshStatusButton.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            if (currentAccountId is null)
                Program.ShowLogin(true, T("common.server_unavailable"));
            else
                DashboardError.Text = T("common.server_unavailable");
        }
        finally
        {
            LogoutButton.IsEnabled = true;
            RefreshStatusButton.IsEnabled = true;
        }
    }

    private static async Task<AccountIdentity> ReadAccountAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync(AdminJsonContext.Default.AccountIdentity)
        ?? throw new JsonException("The account response is empty.");
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Credentials))]
[JsonSerializable(typeof(AccountIdentity))]
[JsonSerializable(typeof(ServiceStatus[]))]
[JsonSerializable(typeof(AccountIdentity[]))]
[JsonSerializable(typeof(PasswordChange))]
[JsonSerializable(typeof(PermissionChange))]
[JsonSerializable(typeof(SuppressionChange))]
[JsonSerializable(typeof(OperationAccepted))]
[JsonSerializable(typeof(OperationStatus))]
[JsonSerializable(typeof(GameProfile))]
[JsonSerializable(typeof(GameNameChange))]
[JsonSerializable(typeof(GameTextureChange))]
[JsonSerializable(typeof(StorageUploadAccepted))]
[JsonSerializable(typeof(BrowserUploadResponse))]
[JsonSerializable(typeof(StoredFile[]))]
[JsonSerializable(typeof(AdminAccountName[]))]
[JsonSerializable(typeof(HistoryCleanupRequest))]
[JsonSerializable(typeof(HistoryCleanupResult))]
[JsonSerializable(typeof(LauncherSource))]
[JsonSerializable(typeof(LauncherRelease))]
[JsonSerializable(typeof(RepositoryDraft))]
[JsonSerializable(typeof(RepositoryBuildEvent))]
[JsonSerializable(typeof(RepositoryBuildError))]
[JsonSerializable(typeof(OfferDraft))]
[JsonSerializable(typeof(OfferTarget[]))]
[JsonSerializable(typeof(OfferDecision))]
[JsonSerializable(typeof(RepositoryManifest))]
[JsonSerializable(typeof(RepositoryChannel))]
[JsonSerializable(typeof(RepositoryArtifact[]))]
[JsonSerializable(typeof(RepositoryGameVersion[]))]
[JsonSerializable(typeof(RepositoryRelease))]
[JsonSerializable(typeof(RepositoryCatalog))]
[JsonSerializable(typeof(ModSearchHit[]))]
[JsonSerializable(typeof(ModSearchHit))]
[JsonSerializable(typeof(ModVersionChoice[]))]
internal sealed partial class AdminJsonContext : JsonSerializerContext;
