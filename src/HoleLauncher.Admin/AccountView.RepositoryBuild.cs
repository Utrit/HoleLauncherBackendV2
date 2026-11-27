using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Avalonia.Interactivity;
using HoleLauncherBackend.Contracts;
using static HoleLauncher.Admin.Locale;

namespace HoleLauncher.Admin;

public partial class AccountView
{
    private void OnShowRepositoryReport(object? sender, RoutedEventArgs e) =>
        RepositoryBuildReport.IsVisible = !RepositoryBuildReport.IsVisible;

    private async Task<RepositoryManifest?> ReadRepositoryBuildAsync(Guid id, RepositoryDraft draft)
    {
        RepositoryReportButton.IsVisible = false;
        RepositoryBuildReport.IsVisible = false;
        RepositoryBuildProgressText.Text = "";
        RepositoryBuildProgressText.IsVisible = true;

        using var request = new HttpRequestMessage(HttpMethod.Put, $"{RepositoryPath}/builds/{id:D}/build-stream")
        {
            Content = JsonContent.Create(draft, AdminJsonContext.Default.RepositoryDraft)
        };
        request.Options.Set(new HttpRequestOptionsKey<bool>("WebAssemblyEnableStreamingResponse"), true);
        using var response = await Api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var progress = new SortedDictionary<int, RepositoryBuildProgress>();
        var repaint = Stopwatch.StartNew();
        var started = false;
        while (await reader.ReadLineAsync() is { } line)
        {
            var item = JsonSerializer.Deserialize(line, AdminJsonContext.Default.RepositoryBuildEvent);
            if (item?.Progress is { } step)
            {
                progress[step.Index] = step;
                if (repaint.ElapsedMilliseconds >= 100 || (!started && step.State != "Wait"))
                {
                    started = true;
                    RenderProgress();
                }
                continue;
            }

            RenderProgress();
            if (item?.Errors is { Length: > 0 } errors)
            {
                RepositoryMessage.Text = T("repository.build_failures", errors.Length);
                RepositoryBuildReport.Text = string.Join("\n", errors.Select(error => T(error.Key, error.Arguments)));
                RepositoryReportButton.IsVisible = true;
                return null;
            }
            if (item?.Manifest is { } manifest)
                return manifest;
        }

        throw new HttpRequestException();

        void RenderProgress()
        {
            RepositoryBuildProgressText.Text = string.Join("\n", progress.Values.Select(step =>
                step.Dependency is null ? $"{step.Name}: {step.State}" : $"{step.Name}: {step.State}: [{step.Dependency}]"));
            repaint.Restart();
        }
    }
}
