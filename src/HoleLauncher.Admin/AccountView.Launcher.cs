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
            if (!response.IsSuccessStatusCode)
            {
                await ShowLauncherErrorAsync(response, cancellation);
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
            await ShowLauncherErrorAsync(response, cancellation);
            return;
        }
        ShowLauncherRelease(await response.Content.ReadFromJsonAsync(AdminJsonContext.Default.LauncherRelease, cancellation));
    });

    private async Task ShowLauncherErrorAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.Content.Headers.ContentType?.MediaType == "application/json" &&
            await response.Content.ReadFromJsonAsync(AdminJsonContext.Default.LauncherImportFailure, cancellation) is { } error)
        {
            LauncherMessage.Text = T("launcher.import_error", T($"launcher.stage.{error.Stage}"), error.Failure);
            return;
        }
        LauncherMessage.Text = T(response.StatusCode switch
        {
            System.Net.HttpStatusCode.BadRequest => "launcher.invalid",
            System.Net.HttpStatusCode.ServiceUnavailable => "launcher.unavailable",
            _ => "launcher.failed"
        });
    }

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
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(16));
        try { await action(deadline.Token); }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            LauncherMessage.Text = T("launcher.failed");
        }
        finally { LauncherPanel.IsEnabled = true; }
    }
}
