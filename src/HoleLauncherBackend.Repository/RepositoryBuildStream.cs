using System.Text.Json;
using System.Threading.Channels;
using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Repository;

internal static class RepositoryBuildStream
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(Guid id, RepositoryDraft draft, HttpContext context,
        ManifestBuilder builder, CatalogStore store, LoaderCatalog loaders, ModrinthClient mods, CancellationToken ct)
    {
        var events = Channel.CreateUnbounded<RepositoryBuildEvent>(new() { SingleReader = true });
        context.Response.ContentType = "application/x-ndjson";
        await context.Response.StartAsync(ct);
        var build = ProduceAsync();

        await foreach (var item in events.Reader.ReadAllAsync(ct))
        {
            await context.Response.WriteAsync(JsonSerializer.Serialize(item, Json) + "\n", ct);
            await context.Response.Body.FlushAsync(ct);
        }
        await build;

        async Task ProduceAsync()
        {
            try
            {
                var result = await Setup.BuildResultAsync(id, draft, builder, store, loaders, mods, ct,
                    progress => events.Writer.TryWrite(new(Progress: progress)));
                var final = (result as IValueHttpResult)?.Value switch
                {
                    RepositoryManifest manifest => new RepositoryBuildEvent(Manifest: manifest),
                    RepositoryBuildError[] errors => new RepositoryBuildEvent(Errors: errors),
                    RepositoryBuildError error => new RepositoryBuildEvent(Errors: [error]),
                    _ => new RepositoryBuildEvent(Errors: [new("build.upstream", [])])
                };
                events.Writer.TryWrite(final);
                events.Writer.TryComplete();
            }
            catch (Exception error)
            {
                events.Writer.TryComplete(error);
            }
        }
    }
}
