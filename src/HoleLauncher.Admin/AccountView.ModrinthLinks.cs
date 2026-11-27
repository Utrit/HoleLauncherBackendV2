using System.Net;
using System.Net.Http.Json;
using Avalonia.Controls;
using HoleLauncherBackend.Contracts;
using static HoleLauncher.Admin.Locale;

namespace HoleLauncher.Admin;

public sealed partial class AccountView
{
    private TextBox? FocusedModrinthSearch() =>
        RepositoryPanel.IsEnabled && RepositoryModQuery.IsFocused ? RepositoryModQuery :
        OfferPanel.IsEnabled && OfferQuery.IsFocused ? OfferQuery : null;

    private TextBox? FocusedPasteInput() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox { IsReadOnly: false } input ? input : null;

    private async void PasteText(string clipboard)
    {
        if (FocusedPasteInput() is not { } input)
            return;

        if (input == FocusedModrinthSearch() && ModrinthProjectFromLink(clipboard) is not null)
        {
            input.Text = clipboard.Trim();
            input.CaretIndex = input.Text.Length;
            if (input == RepositoryModQuery)
                await SearchRepositoryModsAsync();
            else
                await SearchOfferAsync();
            return;
        }

        var text = input.Text ?? "";
        var start = Math.Clamp(Math.Min(input.SelectionStart, input.SelectionEnd), 0, text.Length);
        var pasted = input.AcceptsReturn ? clipboard : clipboard.Replace("\r", "").Replace("\n", " ");
        input.SelectedText = pasted;
        input.CaretIndex = input.SelectionStart = input.SelectionEnd = Math.Min(start + pasted.Length, input.Text?.Length ?? 0);
    }

    private static string? ModrinthProjectFromLink(string value)
    {
        value = value.Trim();
        if (value.StartsWith('[') && value.EndsWith(')') && value.LastIndexOf("](", StringComparison.Ordinal) is var marker && marker >= 0)
            value = value[(marker + 2)..^1].Trim();
        if (!Uri.TryCreate(value.Trim('<', '>'), UriKind.Absolute, out var link) ||
            link.Scheme != Uri.UriSchemeHttps || !link.IsDefaultPort || link.UserInfo.Length != 0 ||
            link.Host is not ("modrinth.com" or "www.modrinth.com"))
            return null;
        var segments = link.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is ["mod", var slug, ..] && slug.Length is > 0 and <= 64 &&
            slug.All(value => char.IsAsciiLetterOrDigit(value) || value is '-' or '_') ? slug : null;
    }

    private static async Task<ModSearchHit?> FindModrinthLinkAsync(string id, string minecraft, string loader, TextBlock message)
    {
        using var response = await Api.GetAsync($"{RepositoryPath}/mods/{id}?minecraft={Uri.EscapeDataString(minecraft)}&loader={Uri.EscapeDataString(loader)}");
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            message.Text = T(response.StatusCode == HttpStatusCode.NotFound ? "repository.link_missing" : "repository.link_incompatible", id);
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(AdminJsonContext.Default.ModSearchHit);
    }

    private Task AddRepositoryModFromLinkAsync(string id) => RunRepositoryAsync(async () =>
    {
        if (RepositoryMinecraft.SelectedItem is not string minecraft || RepositoryLoader.SelectedItem is not RepositoryLoaderChoice loader)
            return;
        var hit = await FindModrinthLinkAsync(id, minecraft, loader.Loader.Id, RepositoryMessage);
        if (hit is null)
            return;

        if (repositoryFiles.All(file => file.ProjectId != hit.ProjectId))
        {
            var folder = Value(RepositoryFolder);
            if (folder.Length > 0 && !RepositoryStorage.IsSafeFolder(folder))
            {
                RepositoryMessage.Text = T("build.invalid_folder", folder);
                return;
            }
            AddRepositoryFolder(folder);
            repositoryFiles = [.. repositoryFiles, new(folder, SelectedType(), hit.Card, ProjectId: hit.ProjectId)];
            RenderRepositoryFiles();
        }
        RepositoryMessage.Text = T("repository.link_added", hit.Card.Title);
    });
}
