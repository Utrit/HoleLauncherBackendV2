namespace HoleLauncherBackend.Contracts;

public sealed record ServiceStatus(string Name, string State, long? UsedBytes = null);

public static class Permissions
{
    public const string EditLauncher = "system.launcher.edit";
    public const string EditUsers = "system.user.edit";
    public const string ReadSystemStatus = "system.status.read";
    public const string StorageUpload = "storage.upload";
    public const string StorageDownload = "storage.download";
    public const string StorageEdit = "storage.edit";
    public const string RepositoryOffer = "repository.offer";
    public const string RepositoryEdit = "repository.edit";
    public const string DatabaseEdit = "database.edit";
    public const string StorageDelete = "storage.delete";
    public const string StorageUserDataUpload = "storage.userdata.upload";

    public const string EditGameUser = "game.user.edit";
    public const string EditGameSkin = "game.user.skin";
    public const string EditGameCape = "game.user.cape";

    public static IReadOnlyList<string> All { get; } =
        [EditUsers, EditLauncher, ReadSystemStatus, StorageUpload, StorageDownload, StorageDelete, StorageUserDataUpload,
            EditGameUser, EditGameSkin, EditGameCape, StorageEdit, DatabaseEdit, RepositoryEdit, RepositoryOffer];
}
