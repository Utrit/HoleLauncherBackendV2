namespace HoleLauncherBackend.Contracts;

public sealed record LauncherSource(string Repository);
public sealed record LauncherRelease(string Repository, string Tag);
