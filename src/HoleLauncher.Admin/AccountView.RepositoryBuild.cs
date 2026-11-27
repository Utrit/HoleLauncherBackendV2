using Avalonia.Threading;
using System.Net.Http.Json;
using System.Text.Json;
using Avalonia.Interactivity;
using HoleLauncherBackend.Contracts;
using static HoleLauncher.Admin.Locale;

namespace HoleLauncher.Admin;

public partial class AccountView
{
    private CancellationTokenSource? repositoryBuildCancellation;

    private async void OnBuildRepositoryVersion(object? sender, RoutedEventArgs e)
    {
        if (repositoryBuildCancellation is not null)
            return;
        if (ReadRepositoryDraft() is not { } draft)
        {
            RepositoryBuildStatus.Text = T("repository.invalid");
            return;
        }

        var account = currentAccountId;
        var version = selectedRepositoryVersion;
        using var cancellation = new CancellationTokenSource();
        repositoryBuildCancellation = cancellation;
        RepositoryBuildButton.IsEnabled = false;
        RepositoryCancelBuildButton.IsEnabled = RepositoryCancelBuildButton.IsVisible = true;
        RepositoryBuildStatus.Text = T("repository.build_active");

        try
        {
            var manifest = await ReadRepositoryBuildAsync(version?.BuildId ?? Guid.NewGuid(), draft, cancellation.Token);
            if (manifest is null || account != currentAccountId)
                return;

            RepositoryCancelBuildButton.IsEnabled = false;
            RepositoryBuildStatus.Text = manifest.Warnings is { Length: > 0 } warnings
                ? T("repository.built_with_warnings", manifest.Build.Version, warnings.Length)
                : T("repository.built", manifest.Build.Version);
            ShowRepositoryBuildReport(manifest.Warnings);
            try
            {
                await LoadRepositoryAsync();
                if (account == currentAccountId && ReferenceEquals(version, selectedRepositoryVersion) &&
                    ReadRepositoryDraft() is { } current && IsSameDraft(draft, current))
                    SelectRepositoryVersion(manifest);
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or IOException or OperationCanceledException)
            {
                if (account == currentAccountId)
                    RepositoryMessage.Text = T("common.failed");
            }
        }
        catch (Exception error) when (cancellation.IsCancellationRequested &&
            error is OperationCanceledException or HttpRequestException or IOException)
        {
            if (account == currentAccountId)
                RepositoryBuildStatus.Text = T("repository.build_cancelled");
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException or IOException)
        {
            if (account == currentAccountId)
                RepositoryBuildStatus.Text = T("common.failed");
        }
        finally
        {
            repositoryBuildCancellation = null;
            RepositoryBuildButton.IsEnabled = true;
            RepositoryCancelBuildButton.IsVisible = false;
            RepositoryBuildProgressText.IsVisible = false;
            RepositoryBuildProgressText.Clear();
        }
    }

    private static bool IsSameDraft(RepositoryDraft previous, RepositoryDraft current) =>
        previous == (current with { Folders = previous.Folders, Files = previous.Files, AcceptedOffers = previous.AcceptedOffers }) &&
        previous.Folders.SequenceEqual(current.Folders) && previous.Files.SequenceEqual(current.Files) &&
        (previous.AcceptedOffers ?? []).SequenceEqual(current.AcceptedOffers ?? []);

    private void OnCancelRepositoryBuild(object? sender, RoutedEventArgs e)
    {
        RepositoryCancelBuildButton.IsEnabled = false;
        repositoryBuildCancellation?.Cancel();
    }

    private void ShowRepositoryBuildReport(RepositoryBuildError[]? issues)
    {
        RepositoryBuildReport.IsVisible = false;
        RepositoryReportButton.IsVisible = issues is { Length: > 0 };
        RepositoryBuildReport.Text = issues is null ? "" :
            string.Join("\n", issues.Select(error => T(error.Key, error.Arguments)));
    }

    private void OnShowRepositoryReport(object? sender, RoutedEventArgs e) =>
        RepositoryBuildReport.IsVisible = !RepositoryBuildReport.IsVisible;

    private async Task<RepositoryManifest?> ReadRepositoryBuildAsync(Guid id, RepositoryDraft draft, CancellationToken cancellation)
    {
        RepositoryReportButton.IsVisible = false;
        RepositoryBuildReport.IsVisible = false;
        RepositoryBuildProgressText.Text = "";
        RepositoryBuildProgressText.IsVisible = false;

        using var request = new HttpRequestMessage(HttpMethod.Put, $"{RepositoryPath}/builds/{id:D}/build-stream")
        {
            Content = JsonContent.Create(draft, AdminJsonContext.Default.RepositoryDraft)
        };
        request.Options.Set(new HttpRequestOptionsKey<bool>("WebAssemblyEnableStreamingResponse"), true);
        using var response = await Api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation));
        var progress = new SortedDictionary<int, RepositoryBuildProgress>();
        var dirty = false;
        var repaint = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        repaint.Tick += (_, _) => RenderProgress();
        repaint.Start();
        try
        {
            while (await reader.ReadLineAsync(cancellation) is { } line)
            {
                cancellation.ThrowIfCancellationRequested();
                var item = JsonSerializer.Deserialize(line, AdminJsonContext.Default.RepositoryBuildEvent);
                if (item?.Progress is { } step)
                {
                    progress[step.Index] = step;
                    dirty = true;
                    continue;
                }

                if (item?.Errors is { Length: > 0 } errors)
                {
                    RepositoryBuildStatus.Text = T("repository.build_failures", errors.Length);
                    ShowRepositoryBuildReport(errors);
                    return null;
                }
                if (item?.Manifest is { } manifest)
                    return manifest;
            }

            throw new HttpRequestException();
        }
        finally { repaint.Stop(); }

        void RenderProgress()
        {
            if (!dirty || cancellation.IsCancellationRequested)
                return;
            dirty = false;
            var active = progress.Values.Where(step => step.State != "Done").OrderBy(step => step.State == "Wait").ToArray();
            RepositoryBuildStatus.Text = T("repository.build_active_progress", progress.Count - active.Length, draft.Files.Length);
            RepositoryBuildProgressText.IsVisible = active.Length != 0;
            RepositoryBuildProgressText.Text = string.Join("\n", active.Select(step =>
                step.Dependency is null ? $"{step.Name}: {step.State}" : $"{step.Name}: {step.State}: [{step.Dependency}]"));
        }
    }
}
