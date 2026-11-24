namespace HoleLauncherBackend.Contracts;

public sealed record PasswordChange(string? NewPassword);
public sealed record ChangePasswordRequest(string SessionLookup, PasswordChange Change);
public sealed record PermissionChange(string[]? Permissions);
public sealed record ChangePermissionsRequest(string SessionLookup, Guid AccountId, PermissionChange Change);

public sealed record SuppressionChange(bool? Suppressed);
