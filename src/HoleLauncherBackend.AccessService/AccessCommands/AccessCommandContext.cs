using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Messaging;

namespace HoleLauncherBackend.AccessService.AccessCommands;

internal sealed record AccessCommandContext(AccessRequest Request, AccountSession Session, KafkaBus Bus)
{
    public AccountIdentity Account => Session.Account;

    public Task ReplyAsync(AccessOutcome outcome, CancellationToken cancellation,
        AccountIdentity? account = null, AccountIdentity[]? users = null, AdminAccountName[]? directory = null) =>
        Bus.ReplyAsync(new AccessReply(Request.CorrelationId, outcome, Account: account, Users: users, Directory: directory), cancellation);

    public Task ForwardOperationAsync(Guid operationId, CancellationToken cancellation, AccountOperationCommand? command = null, string? uploadDomain = null, string? gameName = null, GameTextureCommand? gameTexture = null) =>
        Bus.PublishAsync(KafkaBus.OperationsTopic, operationId.ToString("N"),
            new OrchestrationMessage(Request.CorrelationId, Account.AccountId, operationId,
                Request.ExpiresAt, command, UploadDomain: uploadDomain, GameName: gameName, GameTexture: gameTexture), cancellation);
}
