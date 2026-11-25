using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Storage;

internal sealed class FileStore(string root)
{
    private const int FileBufferSize = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, ActiveUpload> activeUploads = new();

    private sealed record ActiveUpload(CancellationTokenSource Stop, TaskCompletionSource Completion);

    public long GetUsedBytes(CancellationToken cancellation)
    {
        long used = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        if (!Directory.Exists(root))
            return used;

        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            cancellation.ThrowIfCancellationRequested();
            try { used += new FileInfo(path).Length; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        return used;
    }

    public async Task<bool> StageAsync(string domain, Guid id, Guid uploader, Stream source, Action<int> recordProgress, CancellationToken cancellation, string? fileName = null)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var active = new ActiveUpload(stop, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!activeUploads.TryAdd(id, active))
            return false;

        try
        {
            return await StageFileAsync(domain, id, uploader, source, recordProgress, stop.Token, fileName);
        }
        finally
        {
            activeUploads.TryRemove(id, out _);
            active.Completion.TrySetResult();
        }
    }

    private async Task<bool> StageFileAsync(string domain, Guid id, Guid uploader, Stream source, Action<int> recordProgress, CancellationToken cancellation, string? fileName = null)
    {
        var partial = StagingPath(domain, id, "part");
        var ready = StagingPath(domain, id, "ready");
        var aborted = StagingPath(domain, id, "aborted");
        if (File.Exists(aborted) || Directory.Exists(ready) || Directory.Exists(FinalPath(domain, id)))
            return false;

        Directory.CreateDirectory(partial);
        try
        {
            using var hash = SHA512.Create();
            await using var input = new CryptoStream(source, hash, CryptoStreamMode.Read, leaveOpen: true);
            long size;
            await using (var output = File.Open(Path.Combine(partial, "content"), new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = FileBufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            }))
            {
                using var upload = new UploadWriteStream(output, recordProgress);
                await input.CopyToAsync(upload, FileBufferSize, cancellation);
                size = upload.BytesWritten;
            }

            var publicDomain = StorageAddress.IsPrivateUserDataDomain(domain) ? StorageAddress.UserDataDomain : domain;
            var metadata = new StorageFileMetadata(publicDomain, id, uploader, DateTimeOffset.UtcNow, size,
                Convert.ToHexStringLower(hash.Hash!), fileName);
            await using (var index = File.Create(Path.Combine(partial, "metadata.json")))
                await JsonSerializer.SerializeAsync(index, metadata, Json, cancellation);

            cancellation.ThrowIfCancellationRequested();
            if (File.Exists(aborted))
                return false;
            Directory.Move(partial, ready);
            if (!File.Exists(aborted))
                return true;

            RemoveDirectory(ready);
            return false;
        }
        catch (IOException) when (File.Exists(aborted))
        {
            return false;
        }
        finally
        {
            RemoveDirectory(partial);
        }
    }

    public bool TryPublish(string domain, Guid id)
    {
        var published = FinalPath(domain, id);
        if (Directory.Exists(published))
            return true;

        var ready = StagingPath(domain, id, "ready");
        if (!Directory.Exists(ready) || File.Exists(StagingPath(domain, id, "aborted")))
            return false;

        Directory.Move(ready, published);
        return true;
    }

    public async Task RevertAsync(string domain, Guid id, CancellationToken cancellation)
    {
        Directory.CreateDirectory(Path.Combine(root, domain));
        using (new FileStream(StagingPath(domain, id, "aborted"), FileMode.OpenOrCreate, FileAccess.Write)) { }
        if (activeUploads.TryGetValue(id, out var active))
        {
            try { await active.Stop.CancelAsync(); }
            catch (ObjectDisposedException) { }
            await active.Completion.Task.WaitAsync(cancellation);
        }

        RemoveDirectory(FinalPath(domain, id));
        RemoveDirectory(StagingPath(domain, id, "ready"));
        RemoveDirectory(StagingPath(domain, id, "part"));
    }

    public FileStream? Open(string domain, Guid id, string name)
    {
        try
        {
            return new FileStream(Path.Combine(FinalPath(domain, id), name), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, FileBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async Task<StoredFile[]> ListAsync(CancellationToken cancellation)
    {
        if (!Directory.Exists(root))
            return [];

        var files = new List<StoredFile>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var domain = Path.GetFileName(directory);
            if (!StorageAddress.IsValidDomain(domain))
                continue;
            foreach (var path in Directory.EnumerateDirectories(directory))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(Path.GetFileName(path), "D", out var id))
                    continue;

                await using var metadata = Open(domain, id, "metadata.json");
                if (metadata is null)
                    continue;
                var file = await JsonSerializer.DeserializeAsync<StorageFileMetadata>(metadata, Json, cancellation);
                if (file is not null)
                    files.Add(new StoredFile(domain, file));
            }
        }
        return files.ToArray();
    }

    public void Delete(string domain, Guid id) => RemoveDirectory(FinalPath(domain, id));

    public void RemoveExpiredStaging(DateTime cutoff)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            foreach (var path in Directory.EnumerateDirectories(directory, ".*"))
            {
                if (Path.GetExtension(path) is ".part" or ".ready" &&
                    File.GetLastWriteTimeUtc(Path.Combine(path, "content")) < cutoff &&
                    Directory.GetLastWriteTimeUtc(path) < cutoff)
                    RemoveDirectory(path);
            }
            foreach (var path in Directory.EnumerateFiles(directory, "*.aborted"))
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                    File.Delete(path);
            }
        }
    }

    private static void RemoveDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private string FinalPath(string domain, Guid id) => Path.Combine(root, domain, id.ToString("D"));
    private string StagingPath(string domain, Guid id, string suffix) => Path.Combine(root, domain, $".{id:D}.{suffix}");
}
