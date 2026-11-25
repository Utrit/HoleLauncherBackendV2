using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Repository;

internal static class Setup
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton(new CatalogStore(builder.Configuration["Repository:Root"]!));
        builder.Services.AddHttpClient<ModrinthClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.modrinth.com/v2/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("HoleLauncher/1.0");
        });
        builder.Services.AddHttpClient("storage", client =>
            client.BaseAddress = new Uri(builder.Configuration["Storage:Url"]!));
        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient<LoaderCatalog>();
        builder.Services.AddScoped<ManifestBuilder>();
        builder.Services.AddScoped<ModResolver>();
        builder.Services.AddScoped<BuildArchives>();
        builder.Services.AddHttpClient("modrinth-downloads")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        var app = builder.Build();
        app.MapGet("/health", () => Results.Ok());
        app.MapGet("/storage", GetStorageFilesAsync);
        app.MapGet("/offer-targets", (CatalogStore store) => store.OfferTargets());
        app.MapGet("/offers", (CatalogStore store) => store.Snapshot.Offers ?? []);
        app.MapPut("/offers", SubmitOfferAsync);
        app.MapPut("/offers/{id:guid}/decision", (Guid id, OfferDecision decision, CatalogStore store, CancellationToken ct) =>
            store.DecideOfferAsync(id, decision, ct));
        app.MapGet("/catalog", (CatalogStore store) => store.Snapshot);
        app.MapGet("/published", (CatalogStore store) => store.Published());
        app.MapGet("/mods", (string? query, string minecraft, string loader, int? offset, ModrinthClient mods, CancellationToken ct) =>
            mods.SearchAsync(query ?? "", minecraft, loader, Math.Max(0, offset ?? 0), ct));
        app.MapGet("/mods/{id}", FindModAsync);
        app.MapGet("/mods/{id}/versions", async (string id, string minecraft, string loader, ModrinthClient mods, CancellationToken ct) =>
            await mods.VersionsAsync(id, minecraft, loader, ct) is { } versions
                ? Results.Ok(versions.Select(version => new ModVersionChoice(version.Id, version.VersionNumber)))
                : Results.BadRequest());
        app.MapGet("/game-versions", (ModrinthClient mods, LoaderCatalog loaders, CancellationToken ct) => loaders.ListAsync(mods, ct));
        app.MapPut("/resolve-mods", ResolveModsAsync);
        app.MapGet("/builds/{id:guid}/download", DownloadAsync);
        app.MapGet("/builds/{id:guid}/manifest", DownloadManifest);
        app.MapPut("/builds/{id:guid}/build", BuildAsync);
        app.MapDelete("/builds/{id:guid}/version", (Guid id, string version, CatalogStore store, CancellationToken ct) =>
            store.RemoveVersionAsync(id, version, ct));
        app.MapPut("/builds/{id:guid}/channel", (Guid id, RepositoryChannel channel, CatalogStore store, CancellationToken ct) =>
            store.SetChannelAsync(id, channel, ct));
        return app;
    }

    private static async Task<IResult> FindModAsync(string id, string minecraft, string loader, ModrinthClient mods, CancellationToken ct)
    {
        if (await mods.ProjectInfoAsync(id, ct) is not { ProjectType: "mod" } project)
            return Results.NotFound();
        if (await mods.VersionsAsync(project.Id, minecraft, loader, ct) is not { Length: > 0 })
            return Results.Conflict();
        return Results.Ok(new ModSearchHit(project.Id, new(project.Title, project.Description, project.IconUrl)));
    }

    private static async Task<IResult> SubmitOfferAsync(OfferSubmission submission, IHttpClientFactory clients, CatalogStore store,
        ModResolver resolver, CancellationToken ct)
    {
        var draft = submission.Draft;
        if (draft is null || submission.AccountId == Guid.Empty || draft.BuildId == Guid.Empty || string.IsNullOrWhiteSpace(draft.Version) ||
            draft.Description is { Length: > 2000 } || draft.Files is not { Length: > 0 and <= 100 } ||
            draft.Files.Any(file => file is null || file.Card is null || string.IsNullOrWhiteSpace(file.Card.Title) ||
                !Enum.IsDefined(file.Type) || file.Card.Description is { Length: > 2000 } ||
                (file.Folder != "" && !RepositoryStorage.IsSafeFolder(file.Folder))))
            return Results.BadRequest();

        var domain = RepositoryStorage.OfferDomain(submission.AccountId);
        var storage = clients.CreateClient("storage");
        if (store.FindOfferTarget(draft.BuildId, draft.Version) is not { } target)
            return Results.NotFound();
        foreach (var file in draft.Files.Where(file => file.ProjectId is null))
        {
            if (file.StorageId is not { } id || id == Guid.Empty || file.StorageDomain != domain || !await OfferImageIsOwnedAsync(file.Card.IconUrl, submission.AccountId, domain, storage, ct))
                return Results.BadRequest();
            using var response = await storage.GetAsync($"files/{domain}/{id:D}/metadata", ct);
            if (!response.IsSuccessStatusCode)
                return Results.BadRequest();
            var metadata = await response.Content.ReadFromJsonAsync<StorageFileMetadata>(ct);
            if (metadata?.UploadedBy != submission.AccountId || metadata.FileName is not { } name ||
                !RepositoryStorage.IsSafePathSegment(name))
                return Results.BadRequest();
        }

        var mods = draft.Files.Where(file => file.ProjectId is not null).ToArray();
        if (await resolver.ResolveAsync(new(target.Minecraft, target.Loader, mods), ct) is null)
            return Results.Conflict();
        return await store.AddOfferAsync(submission, ct);
    }

    private static async Task<bool> OfferImageIsOwnedAsync(string? url, Guid accountId, string domain, HttpClient storage, CancellationToken ct)
    {
        if (url is null)
            return true;
        var prefix = $"/api/storage/{domain}/";
        if (!url.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(url[prefix.Length..], "D", out var id))
            return false;
        using var response = await storage.GetAsync($"files/{domain}/{id:D}/metadata", ct);
        if (!response.IsSuccessStatusCode)
            return false;
        var metadata = await response.Content.ReadFromJsonAsync<StorageFileMetadata>(ct);
        return metadata is { Size: > 0 and <= StorageAddress.MaxOfferImageBytes } && metadata.UploadedBy == accountId;
    }

    private static async Task<StoredFile[]> GetStorageFilesAsync(IHttpClientFactory clients, CancellationToken ct)
    {
        var files = await clients.CreateClient("storage").GetFromJsonAsync<StoredFile[]>("inventory", ct);
        return files?.Where(file => file.Domain != StorageAddress.UserDataDomain &&
            StorageAddress.IsPublicDomain(file.Domain) && file.Metadata.FileName is not null)
            .OrderBy(file => file.Metadata.FileName).ToArray() ?? [];
    }

    private static IResult DownloadManifest(Guid id, string version, CatalogStore store)
    {
        var snapshot = store.Snapshot;
        var manifest = snapshot.Versions.FirstOrDefault(entry => entry.BuildId == id && entry.Build.Version == version);
        if (manifest is null)
            return Results.NotFound();
        var stage = snapshot.Releases.Any(release => release.BuildId == id && release.Version == version && !release.Test);
        var testing = snapshot.Releases.Any(release => release.BuildId == id && release.Version == version && release.Test);
        var data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest with { Test = testing && !stage },
            System.Text.Json.JsonSerializerOptions.Web);
        var name = RepositoryStorage.DomainFor(manifest.Build.Name) ?? id.ToString("N");
        return Results.File(data, "application/json", $"{name}-{manifest.Build.Version}-manifest.json");
    }

    private static async Task<IResult> DownloadAsync(Guid id, string version, string side, CatalogStore store,
        BuildArchives archives, CancellationToken ct)
    {
        if (side is not ("client" or "server"))
            return Results.BadRequest();
        var manifest = store.Snapshot.Versions.FirstOrDefault(entry => entry.BuildId == id && entry.Build.Version == version);
        if (manifest is null)
            return Results.NotFound();

        try
        {
            var stream = await archives.CreateAsync(manifest, side == "server", ct);
            var name = RepositoryStorage.DomainFor(manifest.Build.Name) ?? id.ToString("N");
            var number = long.TryParse(version, out var parsed) ? parsed.ToString(System.Globalization.CultureInfo.InvariantCulture) : "build";
            return Results.File(stream, "application/zip", $"{name}-{number}-{side}.zip");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> ResolveModsAsync(RepositoryModResolution request, ModResolver resolver, CancellationToken ct)
    {
        try
        {
            var result = await resolver.ResolveAsync(request, ct);
            return result is null ? Results.Conflict() : Results.Ok(result);
        }
        catch (HttpRequestException)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> BuildAsync(Guid id, RepositoryDraft draft,
        ManifestBuilder builder, CatalogStore store, LoaderCatalog loaders, ModrinthClient mods, CancellationToken ct)
    {
        try
        {

            if (draft is null)
                return Results.BadRequest(new RepositoryBuildError("build.invalid_draft", []));
            var supported = await loaders.ListAsync(mods, ct);
            var loader = supported.FirstOrDefault(game => game.Version == draft.Minecraft)?
                .Loaders.FirstOrDefault(loader => loader.Id == draft.Loader);
            if (loader?.Versions.Contains(draft.LoaderVersion) != true)
                return Results.BadRequest(new RepositoryBuildError("build.loader_mismatch",
                    [draft.Minecraft ?? "", draft.Loader ?? "", draft.LoaderVersion ?? ""]));

            var manifest = await builder.BuildAsync(id, draft, ct);
            return await store.SaveVersionAsync(manifest, ct);
        }
        catch (BuildRejectedException error)
        {
            return Results.BadRequest(error.Error);
        }
        catch (HttpRequestException)
        {
            return Results.Json(new RepositoryBuildError("build.upstream", []), statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
