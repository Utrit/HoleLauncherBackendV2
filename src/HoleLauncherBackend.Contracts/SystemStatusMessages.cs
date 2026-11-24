namespace HoleLauncherBackend.Contracts;

public sealed record GetSystemStatusRequest(
    Guid CorrelationId, DateTimeOffset ExpiresAt, UserAccess User);
