using System.Net.Http.Json;
using System.Text.Json;
using Avalonia.Interactivity;
using HoleLauncherBackend.Contracts;
using static HoleLauncher.Admin.Locale;

namespace HoleLauncher.Admin;

public sealed partial class AccountView
{
    private const string LauncherEndpointPath = "/api/auth/launcher";

    private async void OnLauncherNav(object? sender, RoutedEventArgs e)
    {
        SelectSection(LauncherNav);
        LauncherDownloads.IsVisible = false;
        LauncherWindowsDownload.NavigateUri = new Uri(Api.BaseAddress!, "/launcher");
        LauncherLinuxDownload.NavigateUri = new Uri(Api.BaseAddress!, "/launcherlinux");
        await RunLauncherActionAsync(async cancellation =>
        {
            LauncherRepository.Text = "";
            using var response = await Api.GetAsync(LauncherEndpointPath, cancellation);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                ShowLauncherRelease(null);
                return;
            }
            if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                LauncherMessage.Text = T("launcher.unavailable");
                return;
            }
            response.EnsureSuccessStatusCode();
            ShowLauncherRelease(await response.Content.ReadFromJsonAsync(AdminJsonContext.Default.LauncherRelease, cancellation));
        });
    }

    private async void OnImportLauncher(object? sender, RoutedEventArgs e) => await RunLauncherActionAsync(async cancellation =>
    {
        var source = new LauncherSource(LauncherRepository.Text?.Trim() ?? "");
        using var response = await Api.PutAsJsonAsync(LauncherEndpointPath, source, AdminJsonContext.Default.LauncherSource, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            LauncherMessage.Text = T(response.StatusCode == System.Net.HttpStatusCode.BadRequest ? "launcher.invalid" : "launcher.failed");
            return;
        }
        ShowLauncherRelease(await response.Content.ReadFromJsonAsync(AdminJsonContext.Default.LauncherRelease, cancellation));
    });

    private void ShowLauncherRelease(LauncherRelease? release)
    {
        if (release is not null)
            LauncherRepository.Text = release.Repository;
        LauncherMessage.Text = release is null ? T("launcher.empty") : T("launcher.ready", release.Tag);
        LauncherDownloads.IsVisible = release is not null;
    }

    private async Task RunLauncherActionAsync(Func<CancellationToken, Task> action)
    {
        if (!LauncherPanel.IsEnabled)
            return;
        LauncherPanel.IsEnabled = false;
        LauncherMessage.Text = T("common.loading");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        try { await action(deadline.Token); }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            LauncherMessage.Text = T("launcher.failed");
        }
        finally { LauncherPanel.IsEnabled = true; }
    }
}
