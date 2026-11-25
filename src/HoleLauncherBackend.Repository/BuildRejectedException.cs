using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Repository;

internal sealed class BuildRejectedException(string key, params string[] arguments) : Exception(key)
{
    public RepositoryBuildError Error { get; } = new(key, arguments);
}