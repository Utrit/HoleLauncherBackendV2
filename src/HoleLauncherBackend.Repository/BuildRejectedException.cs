using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Repository;

internal sealed class BuildRejectedException : Exception
{
    public BuildRejectedException(string key, params string[] arguments) : this([new(key, arguments)]) { }

    public BuildRejectedException(RepositoryBuildError[] errors) : base(errors[0].Key) => Errors = errors;

    public RepositoryBuildError Error => Errors[0];
    public RepositoryBuildError[] Errors { get; }
}
