namespace HoleLauncherBackend.Contracts;

public sealed record UserAccess(
    Guid AccountId,
    string Name,
    IReadOnlyList<string> Permissions);
