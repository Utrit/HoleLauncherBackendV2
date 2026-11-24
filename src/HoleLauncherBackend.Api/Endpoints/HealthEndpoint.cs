namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class HealthEndpoint
{
    public void Map(IEndpointRouteBuilder routes) => routes.MapHealthChecks("/health/live");
}
