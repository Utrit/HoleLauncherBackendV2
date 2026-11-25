using HoleLauncherBackend.Contracts;
using Microsoft.AspNetCore.Http.Features;
using SkiaSharp;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class RepositoryEndpoint(AccountManagementProxy accounts, IHttpClientFactory clients, StorageProxy storage) : IAccountEndpoint
{
    public void Map(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/repository");
        group.MapGet("/published", (Delegate)((HttpContext context) => ForwardAsync(context, "published", false)));
        group.MapGet("/storage", (Delegate)((HttpContext context) => ForwardAsync(context, "storage")));
        group.MapGet("/catalog", (Delegate)((HttpContext context) => ForwardAsync(context, "catalog")));
        group.MapGet("/mods", (Delegate)((HttpContext context) => ForwardAsync(context, "mods", offerAllowed: true)));
        group.MapGet("/mods/{id}", (string id, HttpContext context) =>
            id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                ? ForwardAsync(context, $"mods/{id}", offerAllowed: true) : Task.FromResult(Results.BadRequest()));
        group.MapGet("/mods/{id}/versions", (string id, HttpContext context) =>
            id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                ? ForwardAsync(context, $"mods/{id}/versions") : Task.FromResult(Results.BadRequest()));
        group.MapPost("/upload", UploadAsync);
        group.MapPost("/offer-upload", OfferUploadAsync);
        group.MapPut("/offers", SubmitOfferAsync);
        group.MapGet("/offer-targets", (Delegate)((HttpContext context) => ForwardAsync(context, "offer-targets", offerAllowed: true)));
        group.MapGet("/offers", (Delegate)((HttpContext context) => ForwardAsync(context, "offers")));
        group.MapPut("/offers/{id:guid}/decision", (Guid id, HttpContext context) => ForwardAsync(context, $"offers/{id:D}/decision"));
        group.MapGet("/game-versions", (Delegate)((HttpContext context) => ForwardAsync(context, "game-versions", offerAllowed: true)));
        group.MapPut("/resolve-mods", (Delegate)((HttpContext context) => ForwardAsync(context, "resolve-mods")));
        group.MapPut("/builds/{id:guid}/build", (Guid id, HttpContext context) => ForwardAsync(context, $"builds/{id:D}/build"));
        group.MapPut("/builds/{id:guid}/channel", (Guid id, HttpContext context) => ForwardAsync(context, $"builds/{id:D}/channel"));
        group.MapGet("/mod-icon", ModIconAsync);
        group.MapGet("/builds/{id:guid}/manifest", (Guid id, HttpContext context) =>
            ForwardAsync(context, $"builds/{id:D}/manifest", archive: true));
        group.MapGet("/builds/{id:guid}/download", (Guid id, HttpContext context) =>
            ForwardAsync(context, $"builds/{id:D}/download", archive: true));
        group.MapDelete("/builds/{id:guid}/version", (Guid id, HttpContext context) => ForwardAsync(context, $"builds/{id:D}/version"));
    }

    private async Task<IResult> OfferUploadAsync(bool? icon, HttpContext context)
    {
        var reply = await accounts.SendAsync(context, AccessOperation.Session, context.RequestAborted);
        if (reply is not { Outcome: AccessOutcome.Ok, Account: { } account })
            return AccountManagementProxy.Result(reply);
        if (!account.Permissions.Contains(Permissions.RepositoryOffer))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return await UploadAssetAsync(RepositoryStorage.OfferDomain(account.AccountId), icon, context,
            AccessOperation.StartOfferUpload);
    }

    private async Task<IResult> SubmitOfferAsync(OfferDraft draft, HttpContext context)
    {
        var ct = context.RequestAborted;
        var reply = await accounts.SendAsync(context, AccessOperation.Session, ct);
        if (reply is not { Outcome: AccessOutcome.Ok, Account: { } account })
            return AccountManagementProxy.Result(reply);
        if (!account.Permissions.Contains(Permissions.RepositoryOffer))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        using var response = await clients.CreateClient("repository").PutAsJsonAsync("offers",
            new OfferSubmission(account.AccountId, account.UserName, draft), ct);
        return Results.StatusCode(response.IsSuccessStatusCode ? StatusCodes.Status204NoContent : (int)response.StatusCode);
    }

    private const int MaxIconDimension = 1024;

    private async Task<IResult> ModIconAsync(string url, HttpContext context)
    {
        if (!ModrinthAssets.IsCdnUrl(url))
            return Results.BadRequest();

        var ct = context.RequestAborted;
        using var response = await clients.CreateClient("modrinth-assets")
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > GameTextures.MaxBytes)
            return Results.NotFound();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var body = await GameTextures.ReadAsync(stream, ct);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (body is null || mediaType is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif"))
            return Results.NotFound();

        using var codec = SKCodec.Create(body);
        if (codec is null || codec.Info.Width is < 1 or > MaxIconDimension || codec.Info.Height is < 1 or > MaxIconDimension)
            return Results.NotFound();
        using var pixels = SKBitmap.Decode(codec);
        using var png = pixels?.Encode(SKEncodedImageFormat.Png, 100);
        if (png is null)
            return Results.NotFound();

        context.Response.Headers.CacheControl = "public,max-age=3600";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.Bytes(png.ToArray(), "image/png");
    }

    private async Task<IResult> UploadAsync(string name, bool? icon, HttpContext context)
    {
        if (RepositoryStorage.DomainFor(name) is not { } domain)
            return Results.BadRequest();

        return await UploadAssetAsync(domain, icon, context, AccessOperation.StartRepositoryUpload);
    }

    private async Task<IResult> UploadAssetAsync(string domain, bool? icon, HttpContext context, AccessOperation operation)
    {
        if (icon != true)
            return await storage.UploadAsync(domain, context, operationType: operation);

        var request = context.Request;
        if (request.ContentLength > GameTextures.MaxBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = GameTextures.MaxBytes;
        using var body = await GameTextures.ReadAsync(request.Body, context.RequestAborted);
        if (body is null)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (!IsValidIcon(body))
            return Results.BadRequest();

        return await storage.UploadAsync(domain, context, body, operation);
    }

    private static bool IsValidIcon(MemoryStream body)
    {
        if (!body.TryGetBuffer(out var bytes))
            return false;
        using var data = SKData.CreateCopy(bytes.AsSpan());
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp) ||
            codec.Info.Width is < 1 or > MaxIconDimension || codec.Info.Height is < 1 or > MaxIconDimension)
            return false;
        using var pixels = new SKBitmap(codec.Info);
        return codec.GetPixels(pixels.Info, pixels.GetPixels()) == SKCodecResult.Success;
    }

    private async Task<IResult> ForwardAsync(HttpContext context, string path, bool admin = true, bool archive = false, bool offerAllowed = false)
    {
        var ct = context.RequestAborted;
        if (admin)
        {
            var reply = await accounts.SendAsync(context, AccessOperation.Session, ct);
            if (reply is not { Outcome: AccessOutcome.Ok, Account: { } account })
                return AccountManagementProxy.Result(reply);
            if (!account.Permissions.Contains(Permissions.RepositoryEdit) &&
                !(offerAllowed && account.Permissions.Contains(Permissions.RepositoryOffer)))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), path + context.Request.QueryString);
        if (HttpMethods.IsPut(context.Request.Method))
        {
            request.Content = new StreamContent(context.Request.Body);
            request.Content.Headers.ContentType = new("application/json");
        }
        var response = await clients.CreateClient(archive ? "repository-archive" : "repository").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        context.Response.RegisterForDispose(response);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            if (path.EndsWith("/build", StringComparison.Ordinal) && response.Content.Headers.ContentType?.MediaType == "application/json")
                return Results.Json(await response.Content.ReadFromJsonAsync<RepositoryBuildError>(ct), statusCode: (int)response.StatusCode);
            return Results.StatusCode((int)response.StatusCode);
        }
        if (archive)
        {
            context.Response.ContentLength = response.Content.Headers.ContentLength;
            context.Response.Headers.ContentDisposition = response.Content.Headers.ContentDisposition?.ToString();
        }
        return Results.Stream(await response.Content.ReadAsStreamAsync(ct), archive ? response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream" : "application/json");
    }
}
