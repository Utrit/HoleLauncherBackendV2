namespace HoleLauncherBackend.Contracts;

public sealed record RepositoryBuildProgress(int Index, string Name, string State, string? Dependency = null);

public sealed record RepositoryBuildEvent(RepositoryBuildProgress? Progress = null,
    RepositoryBuildError[]? Errors = null, RepositoryManifest? Manifest = null);
