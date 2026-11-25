using System.Text.Json;
using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Repository;

internal sealed class CatalogStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1);
    private readonly string path;
    private volatile RepositoryCatalog catalog;

    public CatalogStore(string root)
    {
        Directory.CreateDirectory(root);
        path = Path.Combine(root, "catalog.json");
        catalog = File.Exists(path)
            ? JsonSerializer.Deserialize<RepositoryCatalog>(File.ReadAllText(path), Json)!
            : new([], []);
    }

    public RepositoryCatalog Snapshot => catalog;

    public RepositoryManifest[] Published()
    {
        var snapshot = Snapshot;
        return [.. from release in snapshot.Releases
                   join version in snapshot.Versions on (release.BuildId, release.Version) equals (version.BuildId, version.Build.Version)
                   select version with { Test = release.Test }];
    }

    public async Task<IResult> SaveVersionAsync(RepositoryManifest manifest, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var snapshot = catalog;
            var previousNumber = snapshot.Versions.Where(version => version.BuildId == manifest.BuildId)
                .Select(version => long.TryParse(version.Build.Version, out var number) ? number : 0L)
                .Append(snapshot.HighestVersions?.GetValueOrDefault(manifest.BuildId) ?? 0L).Max();
            if (previousNumber == long.MaxValue)
                return Results.Conflict(new RepositoryBuildError("build.version_limit", []));

            var numberText = (previousNumber + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var accepted = (manifest.Build.AcceptedOffers ?? []).ToHashSet();
            var offers = snapshot.Offers;
            foreach (var (offer, item) in offers?.SelectMany(offer => offer.Files.Select(item => (offer, item))) ?? [])
            {
                if (!accepted.Remove(item.Id))
                    continue;
                if (item.Accepted is not null || (offer.BuildId is { } target && target != manifest.BuildId) ||
                    !manifest.Build.Files.Any(file => file.ProjectId is { } project
                    ? project == item.File.ProjectId : file.StorageDomain == item.File.StorageDomain && file.StorageId == item.File.StorageId))
                    return Results.Conflict(new RepositoryBuildError("build.offer_conflict", []));
            }
            if (accepted.Count != 0)
                return Results.Conflict(new RepositoryBuildError("build.offer_conflict", []));

            var version = manifest with { Build = manifest.Build with { Version = numberText, AcceptedOffers = null } };
            var acceptedIds = (manifest.Build.AcceptedOffers ?? []).ToHashSet();
            if (acceptedIds.Count != 0 && offers is not null)
                offers = offers.Select(offer => offer with { Files = offer.Files.Select(item =>
                    acceptedIds.Contains(item.Id) ? item with { Accepted = true } : item).ToArray() }).ToArray();
            var highest = snapshot.HighestVersions is { } previous
                ? new Dictionary<Guid, long>(previous) : new();
            highest[manifest.BuildId] = previousNumber + 1;
            await SaveAsync(snapshot with { Versions = [.. snapshot.Versions, version], HighestVersions = highest, Offers = offers }, ct);
            return Results.Ok(version);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IResult> SetChannelAsync(Guid buildId, RepositoryChannel channel, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var snapshot = catalog;
            if (!snapshot.Versions.Any(version => version.BuildId == buildId && version.Build.Version == channel.Version))
                return Results.NotFound();

            var release = new RepositoryRelease(buildId, channel.Version, channel.Test);
            var hasChannel = snapshot.Releases.Contains(release);
            if (hasChannel == channel.Enabled)
                return Results.NoContent();

            var releases = channel.Enabled
                ? [.. snapshot.Releases, release]
                : snapshot.Releases.Where(entry => entry != release).ToArray();
            await SaveAsync(snapshot with { Releases = releases }, ct);
            return Results.NoContent();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IResult> RemoveVersionAsync(Guid buildId, string version, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var snapshot = catalog;
            var versions = snapshot.Versions.Where(entry => entry.BuildId != buildId || entry.Build.Version != version).ToArray();
            if (versions.Length == snapshot.Versions.Length)
                return Results.NotFound();

            var highest = snapshot.HighestVersions is { } previous
                ? new Dictionary<Guid, long>(previous) : new();
            if (long.TryParse(version, out var number))
                highest[buildId] = Math.Max(highest.GetValueOrDefault(buildId), number);
            await SaveAsync(snapshot with
            {
                Versions = versions,
                Releases = snapshot.Releases.Where(entry => entry.BuildId != buildId || entry.Version != version).ToArray(),
                HighestVersions = highest
            }, ct);
            return Results.NoContent();
        }
        finally
        {
            gate.Release();
        }
    }

    public OfferTarget[] OfferTargets()
    {
        var snapshot = Snapshot;
        return snapshot.Versions.Select(manifest => FindOfferTarget(snapshot, manifest.BuildId, manifest.Build.Version))
            .OfType<OfferTarget>().ToArray();
    }

    public OfferTarget? FindOfferTarget(Guid buildId, string version) => FindOfferTarget(Snapshot, buildId, version);

    private static OfferTarget? FindOfferTarget(RepositoryCatalog snapshot, Guid buildId, string version)
    {
        var versions = snapshot.Releases.Where(release => release.BuildId == buildId && release.Version == version).ToArray();
        if (versions.Length == 0)
            return null;
        var build = snapshot.Versions.FirstOrDefault(manifest => manifest.BuildId == buildId && manifest.Build.Version == version)?.Build;
        return build is null ? null : new(buildId, version, build.Name, build.Minecraft, build.Loader,
            versions.Any(release => !release.Test), versions.Any(release => release.Test), build.Folders);
    }

    public async Task<IResult> AddOfferAsync(OfferSubmission submission, CancellationToken ct)
    {
        var draft = submission.Draft;
        await gate.WaitAsync(ct);
        try
        {
            if (FindOfferTarget(catalog, draft.BuildId, draft.Version) is not { } target)
                return Results.NotFound();
            var offer = new RepositoryOffer(Guid.NewGuid(), submission.AccountId, submission.UserName, target.Name,
                target.Minecraft, target.Loader, draft.Files.Select(file => new OfferFile(Guid.NewGuid(), file)).ToArray(),
                !target.Stage, draft.Description, BuildId: target.BuildId, BuildVersion: target.Version);
            await SaveAsync(catalog with { Offers = [.. catalog.Offers ?? [], offer] }, ct);
            return Results.Ok(offer);
        }
        finally { gate.Release(); }
    }

    public async Task<IResult> DecideOfferAsync(Guid id, OfferDecision decision, CancellationToken ct)
    {
        var ids = (decision.FileIds ?? [decision.FileId]).ToHashSet();
        if (decision.Accept || ids.Count == 0 || ids.Contains(Guid.Empty))
            return Results.BadRequest();
        await gate.WaitAsync(ct);
        try
        {
            var offers = catalog.Offers ?? [];
            var offer = offers.FirstOrDefault(value => value.Id == id);
            if (offer is null)
                return Results.NotFound();
            var selected = offer.Files.Where(file => ids.Contains(file.Id)).ToArray();
            if (selected.Length != ids.Count)
                return Results.NotFound();
            if (selected.Any(file => file.Accepted == true))
                return Results.Conflict();
            if (selected.All(file => file.Accepted == false))
                return Results.NoContent();

            var updated = offer with { Files = offer.Files.Select(file => ids.Contains(file.Id)
                ? file with { Accepted = false } : file).ToArray() };
            await SaveAsync(catalog with { Offers = offers.Select(value => value.Id == id ? updated : value).ToArray() }, ct);
            return Results.NoContent();
        }
        finally { gate.Release(); }
    }

    private async Task SaveAsync(RepositoryCatalog next, CancellationToken ct)
    {
        var temporary = path + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, next, Json, ct);
        File.Move(temporary, path, overwrite: true);
        catalog = next;
    }
}