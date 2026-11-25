using System.IO.Compression;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.Runtime.InteropServices.JavaScript;
using static HoleLauncher.Admin.Locale;
using HoleLauncherBackend.Contracts;

namespace HoleLauncher.Admin;

public sealed partial class AccountView
{
    private const int MaxZipBytes = 64 * 1024 * 1024;
    private const int MaxExpandedZipBytes = 256 * 1024 * 1024;
    private const int MaxZipFiles = 100;

    private static bool IsZip(JSObject file) =>
        file.GetPropertyAsString("name")?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true;

    private async void OnChooseRepositoryZip(object? sender, RoutedEventArgs e)
    {
        var account = gameAccountId;
        var version = selectedRepositoryVersion;
        var name = Value(RepositoryName);
        try
        {
            using var zip = await Program.ChooseRepositoryFile(false, zip: true);
            if (zip is not null && account == gameAccountId && version == selectedRepositoryVersion &&
                name == Value(RepositoryName) && RepositoryPanel.IsVisible)
                await ImportRepositoryZipAsync(zip);
        }
        catch (JSException)
        {
            RepositoryMessage.Text = T("repository.zip_invalid");
        }
    }

    private async void OnRepositoryDropZip(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        using var zip = Program.TakeRepositoryDrop();
        if (zip is not null)
            await ImportRepositoryZipAsync(zip);
    }

    private async Task ImportRepositoryZipAsync(JSObject file)
    {
        if (repositoryUploading || !RepositoryPanel.IsEnabled)
            return;
        if (!IsZip(file))
        {
            RepositoryMessage.Text = T("repository.zip_expected");
            return;
        }
        if (RepositoryStorage.DomainFor(Value(RepositoryName)) is null)
        {
            RepositoryMessage.Text = T("repository.upload_name");
            return;
        }

        var prefix = Value(RepositoryFolder);
        var type = SelectedType();
        await RunRepositoryAsync(async () =>
        {
            await ImportZipAsync(file, prefix, async (entry, folder, name) =>
            {
                StorageUploadAccepted? stored = null;
                await UploadRepositoryFileAsync(entry, false, (result, _) => stored = result);
                if (stored is null)
                    return false;

                AddRepositoryFolder(folder);
                repositoryFiles = [.. repositoryFiles, new(folder, type, new(name, "", null),
                    StorageDomain: stored.Domain, StorageId: stored.FileId)];
                RenderRepositoryFiles();
                return true;
            }, AddRepositoryFolder, message => RepositoryMessage.Text = message);
            RenderRepositoryFiles();
        });
    }

    private void AddRepositoryFolder(string folder)
    {
        if (folder.Length == 0)
            return;

        if (!repositoryFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            repositoryFolders = [.. repositoryFolders, folder];
    }

    private static async Task ImportZipAsync(JSObject file, string prefix,
        Func<JSObject, string, string, Task<bool>> add, Action<string> addFolder, Action<string> report)
    {
        if (file.GetPropertyAsDouble("size") > MaxZipBytes)
        {
            report(T("repository.zip_limits"));
            return;
        }

        try
        {
            using var buffer = await Program.ReadRepositoryFile(file);
            using var stream = new MemoryStream(Program.CopyRepositoryBytes(buffer), writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entries = new List<(ZipArchiveEntry Entry, string Path, bool Directory)>();
            var allPaths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var directory = entry.FullName.EndsWith('/');
                var path = directory ? entry.FullName[..^1] : entry.FullName;
                var resolved = prefix.Length == 0 ? path : $"{prefix}/{path}";
                if (!path.Split('/').All(RepositoryStorage.IsSafePathSegment) || !resolved.Split('/').All(RepositoryStorage.IsSafePathSegment) ||
                    (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000 || !allPaths.TryAdd(resolved, directory))
                {
                    report(T("repository.zip_path", entry.FullName));
                    return;
                }

                total += entry.Length;
                entries.Add((entry, resolved, directory));
                if (entry.Length > MaxZipBytes || total > MaxExpandedZipBytes || entries.Count > MaxZipFiles)
                {
                    report(T("repository.zip_limits"));
                    return;
                }
            }

            foreach (var (_, path, _) in entries)
            {
                for (var index = path.IndexOf('/'); index >= 0; index = path.IndexOf('/', index + 1))
                {
                    if (allPaths.TryGetValue(path[..index], out var directory) && !directory)
                    {
                        report(T("repository.zip_path", path));
                        return;
                    }
                }
            }

            var count = 0;
            foreach (var (entry, path, directory) in entries)
            {
                if (directory)
                {
                    addFolder(path);
                    continue;
                }

                var data = new byte[checked((int)entry.Length)];
                await using var content = entry.Open();
                await content.ReadExactlyAsync(data);
                if (content.ReadByte() != -1)
                    throw new InvalidDataException();

                var slash = path.LastIndexOf('/');
                var folder = slash >= 0 ? path[..slash] : "";
                var name = path[(slash + 1)..];
                using var extracted = Program.CreateRepositoryFile(name, data);
                if (!await add(extracted, folder, name))
                {
                    report(T("repository.zip_partial", path, count));
                    return;
                }
                count++;
            }

            report(T("repository.zip_done", count));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JSException or NotSupportedException)
        {
            report(T("repository.zip_invalid"));
        }
    }
}
