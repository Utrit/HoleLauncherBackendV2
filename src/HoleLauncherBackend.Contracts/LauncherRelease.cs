namespace HoleLauncherBackend.Contracts;

public sealed record LauncherSource(string Repository);
public sealed record LauncherImport(string Repository, Guid Uploader);
public sealed record LauncherRelease(string Repository, string Tag);
