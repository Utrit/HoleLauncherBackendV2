using System.Net.Http.Json;
using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class LauncherEndpoint(AccountManagementProxy accounts, IHttpClientFactory clients) : IAccountEndpoint
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/launcher", async Task<IResult> (HttpContext context) => await ManageAsync(context, null));
        routes.MapPut("/launcher", (LauncherSource source, HttpContext context) => ManageAsync(context, source));
    }

    public void MapPublic(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/launcher", async Task<IResult> (HttpContext context) => await DownloadAsync(context, "windows", "HoleLauncher.exe"));
        routes.MapGet("/launcherlinux", async Task<IResult> (HttpContext context) => await DownloadAsync(context, "linux", "HoleLauncher-linux-x64.tar.gz"));
        routes.MapGet("/launcher/sha512", async Task<IResult> (HttpContext context) => await DownloadAsync(context, "windows/sha512", null));
        routes.MapGet("/launcherlinux/sha512", async Task<IResult> (HttpContext context) => await DownloadAsync(context, "linux/sha512", null));
    }

    private async Task<IResult> ManageAsync(HttpContext context, LauncherSource? source)
    {
        var ct = context.RequestAborted;
        var reply = await accounts.SendAsync(context, AccessOperation.Session, ct);
        if (reply is not { Outcome: AccessOutcome.Ok, Account: { } account })
            return AccountManagementProxy.Result(reply);
        if (!account.Permissions.Contains(Permissions.EditLauncher))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var client = clients.CreateClient("launcher");
        using var response = source is null
            ? await client.GetAsync("release", ct)
            : await client.PutAsJsonAsync("release", new LauncherImport(source.Repository, account.AccountId), ct);
        return response.IsSuccessStatusCode
            ? Results.Json(await response.Content.ReadFromJsonAsync<LauncherRelease>(ct))
            : Results.StatusCode((int)response.StatusCode);
    }

    private async Task<IResult> DownloadAsync(HttpContext context, string path, string? filename)
    {
        try
        {
            var ct = context.RequestAborted;
            var response = await clients.CreateClient("launcher").GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
            context.Response.RegisterForDispose(response);
            if (!response.IsSuccessStatusCode)
                return Results.StatusCode((int)response.StatusCode);

            context.Response.Headers.CacheControl = "no-cache";
            context.Response.ContentLength = response.Content.Headers.ContentLength;
            if (response.Headers.TryGetValues("X-Launcher-Build-Sha512", out var fingerprint))
                context.Response.Headers["X-Launcher-Build-Sha512"] = fingerprint.ToArray();
            return Results.Stream(await response.Content.ReadAsStreamAsync(ct),
                filename is null ? "text/plain; charset=utf-8" : "application/octet-stream", filename);
        }
        catch (HttpRequestException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
