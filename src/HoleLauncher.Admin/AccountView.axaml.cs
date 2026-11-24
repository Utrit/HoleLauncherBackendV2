using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HoleLauncherBackend.Contracts;

namespace HoleLauncher.Admin;

public sealed partial class AccountView : UserControl
{
    private static readonly HttpClient Api = new() { BaseAddress = new Uri(Program.GetOrigin()) };

    public AccountView()
    {
        InitializeComponent();
        foreach (var permission in Permissions.All)
            PermissionFlags.Children.Add(new CheckBox { Content = permission });
        CredentialsPanel.AddHandler(KeyDownEvent, OnCredentialsKeyDown, RoutingStrategies.Tunnel);
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
            await UpdateServiceStatusAsync();
            await LoadUsersAsync();
        });
    }

    private async void OnCredentialsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !CredentialsPanel.IsEnabled ||
            !(UserNameInput.IsFocused || PasswordInput.IsInputFocused))
            return;

        e.Handled = true;
        await SubmitAsync(register: false);
    }

    private async void OnRefreshStatus(object? sender, RoutedEventArgs e) => await UpdateSessionAsync();

    private async void OnLogin(object? sender, RoutedEventArgs e) => await SubmitAsync(register: false);

    private async void OnRegister(object? sender, RoutedEventArgs e) => await SubmitAsync(register: true);

    private Task SubmitAsync(bool register) => RunAsync(async () =>
    {
        var credentials = new Credentials(UserNameInput.Text, PasswordInput.Text ?? string.Empty);
        PasswordInput.Clear();
        var route = register ? "/api/auth/register" : "/api/auth/login";
        using var response = await Api.PostAsJsonAsync(route, credentials, AdminJsonContext.Default.Credentials);
        if (!register && response.IsSuccessStatusCode)
        {
            ShowAccount(await ReadAccountAsync(response));
            await UpdateServiceStatusAsync();
            await LoadUsersAsync();
            return;
        }

        StatusText.Text = response.StatusCode switch
        {
            HttpStatusCode.Created => "Account created.",
            HttpStatusCode.Conflict => "Error: Account already exists.",
            HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized => "Error: Invalid credentials.",
            HttpStatusCode.TooManyRequests => "Error: Too many requests.",
            _ => "Error: Unknown error."
        };
    });

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
        CredentialsPanel.IsVisible = account is null;
        LogoutButton.IsVisible = account is not null;
        PermissionsPanel.IsVisible = account is not null;
        var permissions = account?.Permissions ?? [];
        PermissionsText.Text = permissions.Length == 0 ? "Нет прав" : string.Join(Environment.NewLine, permissions);
        ServicesPanel.IsVisible = permissions.Contains(Permissions.ReadSystemStatus);
        PasswordPanel.IsVisible = account is not null;
        UserManagementPanel.IsVisible = permissions.Contains(Permissions.EditUsers);
        if (account is null)
        {
            NewPasswordInput.Clear();
            PasswordMessage.Text = null;
            UserPicker.ItemsSource = null;
            users = [];
        }
        ServiceRows.Children.Clear();
        ServicesMessage.Text = null;
        StatusText.Text = account is null ? null : $"{account.UserName}.";
    }

    private async Task UpdateServiceStatusAsync()
    {
        if (!ServicesPanel.IsVisible)
            return;

        ServicesMessage.Text = "Загрузка…";
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
                ServicesPanel.IsVisible = false;
                return;
            }

            response.EnsureSuccessStatusCode();
            var services = await response.Content.ReadFromJsonAsync(AdminJsonContext.Default.ServiceStatusArray)
                ?? throw new JsonException("The services response is empty.");
            foreach (var service in services)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
                var state = new TextBlock { Text = service.State };
                Grid.SetColumn(state, 1);
                row.Children.Add(new TextBlock { Text = service.Name, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                row.Children.Add(state);
                ServiceRows.Children.Add(row);
            }

            ServicesMessage.Text = null;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            ServicesMessage.Text = "Статусы недоступны.";
        }
    }

    private AccountIdentity[] users = [];

    private async Task LoadUsersAsync()
    {
        if (!UserManagementPanel.IsVisible)
            return;

        using var response = await Api.GetAsync("/api/auth/users");
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            UserManagementPanel.IsVisible = false;
            return;
        }
        response.EnsureSuccessStatusCode();

        var selectedId = SelectedUser?.AccountId;
        users = await response.Content.ReadFromJsonAsync(AdminJsonContext.Default.AccountIdentityArray)
            ?? throw new JsonException("The users response is empty.");
        UserPicker.ItemsSource = users.Select(user => user.Suppressed ? $"{user.UserName} (заблокирован)" : user.UserName).ToArray();
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
            flag.IsChecked = flag.Content is string permission && permissions.Contains(permission);
        UserFlagsPanel.IsVisible = SelectedUser is not null;
        ToggleSuppressionButton.Content = SelectedUser?.Suppressed == true ? "Разблокировать" : "Заблокировать";
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
                .Where(flag => flag.IsChecked == true).Select(flag => (string)flag.Content!).ToArray();
            using var response = await Api.PutAsJsonAsync(
                $"/api/auth/users/{user.AccountId}/permissions", new PermissionChange(flags),
                AdminJsonContext.Default.PermissionChange);
            if (!await WaitForMutationAsync(response, "Не удалось сохранить права."))
                return;

            await UpdateSessionAsync();
            UserManagementMessage.Text = "Права сохранены.";
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            UserManagementMessage.Text = "Не удалось сохранить права.";
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
            if (!await WaitForMutationAsync(response, "Не удалось изменить блокировку."))
                return;

            await UpdateSessionAsync();
            UserManagementMessage.Text = suppressed ? "Аккаунт заблокирован." : "Аккаунт разблокирован.";
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            UserManagementMessage.Text = "Не удалось изменить блокировку.";
        }
        finally
        {
            ToggleSuppressionButton.IsEnabled = true;
            SavePermissionsButton.IsEnabled = true;
            UserPicker.IsEnabled = true;
        }
    }


    private async Task<bool> WaitForMutationAsync(HttpResponseMessage response, string failureMessage)
    {
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            UserManagementMessage.Text = failureMessage;
            return false;
        }

        var accepted = await response.Content.ReadFromJsonAsync(AdminJsonContext.Default.OperationAccepted)
            ?? throw new JsonException("The accepted operation response is empty.");
        UserManagementMessage.Text = "Операция выполняется…";
        var path = $"/api/auth/operations/{accepted.OperationId}";
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
                    UserManagementMessage.Text = failureMessage;
                    return false;
                }

                await Task.Delay(500, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            UserManagementMessage.Text = $"Операция ещё выполняется. ID: {accepted.OperationId}";
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
                HttpStatusCode.NoContent => "Пароль изменён.",
                HttpStatusCode.BadRequest => "err: cred invalid",
                HttpStatusCode.Unauthorized => "Сессия истекла.",
                HttpStatusCode.TooManyRequests => "Слишком много попыток.",
                _ => "Не удалось изменить пароль."
            };
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            PasswordMessage.Text = "Не удалось изменить пароль.";
        }
        finally
        {
            ChangePasswordButton.IsEnabled = true;
        }
    }


    private async Task RunAsync(Func<Task> action)
    {
        CredentialsPanel.IsEnabled = false;
        LogoutButton.IsEnabled = false;
        RefreshStatusButton.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            StatusText.Text = "Error: Server unreachable.";
        }
        finally
        {
            CredentialsPanel.IsEnabled = true;
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
internal sealed partial class AdminJsonContext : JsonSerializerContext;
