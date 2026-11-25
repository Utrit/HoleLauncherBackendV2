using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Storage;

internal sealed class UploadEndpoint : IStorageEndpoint
{
    public void Map(IEndpointRouteBuilder routes) => routes.MapPut("/staging/{domain}/{fileId}", StageAsync);

    private static async Task<IResult> StageAsync(string domain, string fileId, HttpRequest request, FileStore storage,
        UploadWatchdog watchdog, CancellationToken cancellation)
    {
        if (!StorageAddress.TryParseFileId(fileId, out var id) ||
            !Guid.TryParse(request.Headers["X-Uploader-Id"], out var uploader) ||
            !StorageAddress.IsStagingDomainFor(domain, uploader))
            return Results.BadRequest();

        if (request.ContentLength > StorageAddress.MaxUploadBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        if (request.HttpContext.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } dataRate)
            dataRate.MinDataRate = null;

        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var progress = new UploadProgress();
        var watch = watchdog.RunAsync(id, progress, transfer);
        try
        {
            return await storage.StageAsync(domain, id, uploader, request.Body, progress.Record, transfer.Token, ReadFileName(request))
                ? Results.NoContent()
                : Results.Conflict();
        }
        catch (OperationCanceledException) when (progress.TimedOut)
        {
            return Results.StatusCode(StatusCodes.Status408RequestTimeout);
        }
        finally
        {
            await transfer.CancelAsync();
            await watch;
        }
    }

    private static string? ReadFileName(HttpRequest request)
    {
        var value = request.Headers["X-Storage-File-Name"].ToString();
        if (value.Length == 0 || value.Length > 1024)
            return null;

        var name = Uri.UnescapeDataString(value).Trim();
        return name.Length is > 0 and <= 255 && !name.Any(character =>
            char.IsControl(character) || character is '/' or '\\') && name is not "." and not ".." ? name : null;
    }
}
