using System.Text.Json.Serialization;

namespace HoleLauncherBackend.Contracts;

public enum AccountMutation { ChangePermissions, ChangeSuppression }
[JsonConverter(typeof(JsonStringEnumConverter<OperationState>))]
public enum OperationState { Pending, Done, Failed, Reverting }
public sealed record OperationAccepted(Guid OperationId);
public sealed record OperationStatus(Guid OperationId, OperationState State, AccessOutcome? Outcome, string? ErrorMessage = null);
public sealed record AccountOperationCommand(
    Guid OperationId, Guid ActorId, Guid AccountId, AccountMutation Mutation, string[]? Permissions, bool? Suppressed);
public sealed record OrchestrationMessage(
    Guid CorrelationId, Guid ActorId, Guid OperationId, DateTimeOffset? ExpiresAt = null, AccountOperationCommand? Command = null, AccessOutcome? Result = null, string? UploadDomain = null);
