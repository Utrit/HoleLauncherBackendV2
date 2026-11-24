namespace HoleLauncherBackend.Contracts;

public sealed record ServiceStatus(string Name, string State);

public static class Permissions
{
    public const string EditUsers = "system.user.edit";
    public const string ReadSystemStatus = "system.status.read";
    public const string StorageUpload = "storage.upload";
    public const string StorageDownload = "storage.download";
    public const string StorageDelete = "storage.delete";
    public const string StorageUserDataUpload = "storage.userdata.upload";

    public static IReadOnlyList<string> All { get; } =
        [EditUsers, ReadSystemStatus, StorageUpload, StorageDownload, StorageDelete, StorageUserDataUpload];
}
