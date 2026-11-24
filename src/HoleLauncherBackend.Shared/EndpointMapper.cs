namespace HoleLauncherBackend.Shared;

public interface IEndpoint<in TRoutes>
{
    void Map(TRoutes routes);
}

public sealed class EndpointMapper<TRoutes, TEndpoint>(IEnumerable<TEndpoint> endpoints)
    where TEndpoint : IEndpoint<TRoutes>
{
    public void Map(TRoutes routes)
    {
        foreach (var endpoint in endpoints)
            endpoint.Map(routes);
    }
}
