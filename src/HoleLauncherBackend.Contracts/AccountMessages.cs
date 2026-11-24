namespace HoleLauncherBackend.Contracts;

public sealed record Credentials(string? UserName, string? Password);

public sealed record AccountIdentity(Guid AccountId, string UserName, string[] Permissions, bool Suppressed = false);

public sealed record CreatedSession(AccountIdentity Account, string SessionId);

public sealed record SessionLookupRequest(string SessionLookup);
