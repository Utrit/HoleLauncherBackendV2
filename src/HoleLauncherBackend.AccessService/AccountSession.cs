using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.AccessService;

internal sealed record AccountSession(AccountIdentity Account, DateTimeOffset ExpiresAt)
{
    public bool IsValidAt(DateTimeOffset now) => now < ExpiresAt;
}
