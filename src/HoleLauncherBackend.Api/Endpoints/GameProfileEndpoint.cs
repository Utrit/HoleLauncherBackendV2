using System.Net;

using HoleLauncherBackend.Contracts;

using Microsoft.AspNetCore.Http.Features;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class GameProfileEndpoint(
    AccountManagementProxy accounts, GameProfileStore profiles, StorageProxy storage, IHttpClientFactory clients) : IAccountEndpoint
{
    public void Map(IEndpointRouteBuilder routes)
    {
        var profile = routes.MapGroup("/game-profile");
        profile.MapGet("", async Task<IResult> (HttpContext context) => await WithAccountAsync(context, async account =>
            Results.Ok(await profiles.FindAsync(account, context.RequestAborted))));
        profile.MapPut("/name", ChangeNameAsync);
        profile.MapGet("/cape-preview/{fileId:guid}", (Guid fileId, HttpContext context) =>
            WithAccountAsync(context, account => ReadCapePreviewAsync(account.AccountId, fileId, context.RequestAborted),
                Permissions.StorageDownload));
        profile.MapPost("/{texture}", (string texture, HttpContext context) => WithTextureAsync(context, texture,
            (_, skin) => UploadAsync(context, skin)));
        profile.MapPut("/{texture}", (string texture, GameTextureChange change, HttpContext context) =>
            WithTextureAsync(context, texture, (account, skin) => SetTextureAsync(account, change, skin, context)));
    }

    private Task<IResult> WithTextureAsync(HttpContext context, string texture, Func<AccountIdentity, bool, Task<IResult>> handler) =>
        texture switch
        {
            "skin" => WithAccountAsync(context, account => handler(account, true), Permissions.EditGameSkin),
            "cape" => WithAccountAsync(context, account => handler(account, false), Permissions.EditGameCape),
            _ => Task.FromResult(Results.NotFound())
        };

    private async Task<IResult> WithAccountAsync(HttpContext context, Func<AccountIdentity, Task<IResult>> handler,
        string? permission = null)
    {
        var reply = await accounts.SendAsync(context, AccessOperation.Session, context.RequestAborted);
        if (reply is not { Outcome: AccessOutcome.Ok, Account: { } account })
            return AccountManagementProxy.Result(reply);

        return permission is null || account.Permissions.Contains(permission)
            ? await handler(account)
            : Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    private async Task<IResult> ChangeNameAsync(GameNameChange change, HttpContext context)
    {
        if (!GameNameRules.IsValid(change.GameName))
            return Results.BadRequest();
        var reply = await accounts.SendAsync(context, AccessOperation.ChangeGameName, context.RequestAborted,
            gameName: change.GameName);
        return AccountManagementProxy.Result(reply);
    }

    private async Task<IResult> UploadAsync(HttpContext context, bool skin)
    {
        if (context.Request.ContentLength > GameTextures.MaxInputBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = GameTextures.MaxInputBytes;

        using var body = await GameTextures.ReadAsync(context.Request.Body, context.RequestAborted, GameTextures.MaxInputBytes);
        if (body is null)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        using var prepared = GameTextures.PrepareUpload(body, skin);
        if (prepared is null)
            return Results.BadRequest();

        return await storage.UploadAsync(StorageAddress.UserDataDomain, context, prepared);
    }

    private async Task<IResult> SetTextureAsync(AccountIdentity account, GameTextureChange change, bool skin, HttpContext context)
    {
        var cancellation = context.RequestAborted;
        var fileId = change.FileId;
        var model = skin ? change.SkinModel ?? SkinModels.Classic : SkinModels.Classic;
        if (fileId == Guid.Empty || (skin && !SkinModels.IsValid(model)))
            return Results.BadRequest();
        using var response = await OpenTextureAsync(account.AccountId, fileId, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return Results.NotFound();
        response.EnsureSuccessStatusCode();

        using var bytes = await ReadTextureAsync(response, cancellation);
        if (bytes is null || !GameTextures.IsValid(bytes, skin, model))
            return Results.BadRequest();

        var reply = await accounts.SendAsync(context, AccessOperation.ChangeGameTexture, cancellation,
            gameTexture: new GameTextureCommand(fileId, skin, model));
        return AccountManagementProxy.Result(reply);
    }

    private async Task<IResult> ReadCapePreviewAsync(Guid accountId, Guid fileId, CancellationToken cancellation)
    {
        using var response = await OpenTextureAsync(accountId, fileId, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return Results.NotFound();
        response.EnsureSuccessStatusCode();
        using var bytes = await ReadTextureAsync(response, cancellation);
        using var texture = bytes is null ? null : GameTextures.PrepareUpload(bytes, skin: false);
        return texture is null ? Results.BadRequest() : Results.Bytes(texture.ToArray(), "image/png");
    }

    private Task<HttpResponseMessage> OpenTextureAsync(Guid accountId, Guid fileId, CancellationToken cancellation)
    {
        var domain = StorageAddress.ResolveDomain(StorageAddress.UserDataDomain, accountId);
        return clients.CreateClient("storage").GetAsync($"files/{domain}/{fileId:D}",
            HttpCompletionOption.ResponseHeadersRead, cancellation);
    }

    private static async Task<MemoryStream?> ReadTextureAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.Content.Headers.ContentLength > GameTextures.MaxBytes)
            return null;
        await using var source = await response.Content.ReadAsStreamAsync(cancellation);
        return await GameTextures.ReadAsync(source, cancellation);
    }

}
