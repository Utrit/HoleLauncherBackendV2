using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.AccessService.AccessCommands;

internal sealed class ChangeGameTextureConsumer : IAccessCommandConsumer
{
    public AccessOperation Operation => AccessOperation.ChangeGameTexture;

    public Task ConsumeAsync(AccessCommandContext context, CancellationToken cancellation)
    {
        if (context.Request.GameTexture is not { } texture || texture.FileId == Guid.Empty || !SkinModels.IsValid(texture.Model))
            return context.ReplyAsync(AccessOutcome.InvalidRequest, cancellation);
        var permission = texture.Skin ? Permissions.EditGameSkin : Permissions.EditGameCape;
        if (!context.Account.Permissions.Contains(permission))
            return context.ReplyAsync(AccessOutcome.Forbidden, cancellation);

        return context.ForwardOperationAsync(context.Request.CorrelationId, cancellation, gameTexture: texture);
    }
}
