namespace HoleLauncherBackend.Contracts;

public enum AccessOperation { Session, Logout, Users, ChangePermissions, Status, ChangeSuppression, OperationStatus, StartStorageUpload }

public sealed record AccessRequest(
    Guid CorrelationId, DateTimeOffset ExpiresAt, string SessionLookup,
    AccessOperation Operation, Guid? AccountId = null, string[]? Permissions = null, bool? Suppressed = null, Guid? OperationId = null, string? StorageDomain = null);

public enum AccessOutcome { Unavailable, Ok, NoContent, InvalidSession, Forbidden, InvalidRequest, NotFound, Accepted, Timeout }

public sealed record AccessReply(
    Guid CorrelationId, AccessOutcome Outcome, AccountIdentity? Account = null,
    AccountIdentity[]? Users = null, ServiceStatus[]? Services = null, OperationStatus? Operation = null, Guid? ActorId = null);
