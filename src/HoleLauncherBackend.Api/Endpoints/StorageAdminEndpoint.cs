using System.Net;
using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class StorageAdminEndpoint(AccountManagementProxy accounts, IHttpClientFactory clients,
    StorageAdministration administration) : IAccountEndpoint
{
    public void Map(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/storage-admin");
        group.MapGet("/files", (Delegate)ListAsync);
        group.MapGet("/users", (Delegate)ListUsersAsync);
        group.MapGet("/files/{domain}/{fileId}", DownloadAsync);
        group.MapDelete("/files/{domain}/{fileId}", DeleteAsync);
        group.MapPost("/history/cleanup", CleanHistoryAsync);
    }

    private async Task<IResult> ListUsersAsync(HttpContext context)
    {
        var reply = await accounts.SendAsync(context, AccessOperation.AdminDirectory, context.RequestAborted);
        return reply.Directory is { } directory ? Results.Ok(directory) : AccountManagementProxy.Result(reply);
    }

    private async Task<IResult> ListAsync(HttpContext context)
    {
        var forbidden = await AuthorizeAsync(context, Permissions.StorageEdit, Permissions.StorageDelete);
        if (forbidden is not null)
            return forbidden;
        var cancellation = context.RequestAborted;
        var files = await clients.CreateClient("storage").GetFromJsonAsync<StoredFile[]>("inventory", cancellation);
        if (files is null || files.Length == 0)
            return Results.Ok(Array.Empty<StoredFile>());

        var labels = await administration.GetTextureLabelsAsync(files.Select(file => file.Metadata.FileId).Distinct().ToArray(),
            cancellation);
        return Results.Ok(files.Select(file => file with { Label = labels.GetValueOrDefault(file.Metadata.FileId) }));
    }

    private async Task<IResult> DownloadAsync(string domain, string fileId, HttpContext context)
    {
        if (!StorageAddress.IsValidDomain(domain) || !StorageAddress.TryParseFileId(fileId, out var id))
            return Results.BadRequest();
        var forbidden = await AuthorizeAsync(context, Permissions.StorageEdit, Permissions.StorageDelete);
        if (forbidden is not null)
            return forbidden;

        var storage = clients.CreateClient("storage");
        using var info = await storage.GetAsync($"files/{domain}/{id:D}/metadata", context.RequestAborted);
        if (info.StatusCode == HttpStatusCode.NotFound)
            return Results.NotFound();
        info.EnsureSuccessStatusCode();
        var metadata = await info.Content.ReadFromJsonAsync<StorageFileMetadata>(context.RequestAborted);
        var name = metadata?.FileName ?? $"Файл {metadata?.UploadedAt:yyyy-MM-dd}.bin";
        var response = await storage.GetAsync($"files/{domain}/{id:D}",
            HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.RegisterForDispose(response);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return Results.NotFound();
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        return Results.File(stream, "application/octet-stream", name);
    }

    private async Task<IResult> DeleteAsync(string domain, string fileId, HttpContext context)
    {
        if (!StorageAddress.IsValidDomain(domain) || !StorageAddress.TryParseFileId(fileId, out var id))
            return Results.BadRequest();
        var forbidden = await AuthorizeAsync(context, Permissions.StorageDelete);
        if (forbidden is not null)
            return forbidden;

        return Results.StatusCode((int)await administration.DeleteAsync(domain, id, context.RequestAborted));
    }

    private async Task<IResult> CleanHistoryAsync(HistoryCleanupRequest scope, HttpContext context)
    {
        if (scope.All == scope.AccountId.HasValue || scope.AccountId == Guid.Empty)
            return Results.BadRequest();
        var forbidden = await AuthorizeAsync(context, Permissions.DatabaseEdit);
        if (forbidden is not null)
            return forbidden;

        return Results.Ok(await administration.CleanHistoryAsync(scope, context.RequestAborted));
    }

    private async Task<IResult?> AuthorizeAsync(HttpContext context, string permission, string? alternative = null)
    {
        var reply = await accounts.SendAsync(context, AccessOperation.Session, context.RequestAborted);
        if (reply is not { Outcome: AccessOutcome.Ok, Account: { } account })
            return AccountManagementProxy.Result(reply);
        return account.Permissions.Contains(permission) || (alternative is not null && account.Permissions.Contains(alternative))
            ? null : Results.StatusCode(StatusCodes.Status403Forbidden);
    }
}
