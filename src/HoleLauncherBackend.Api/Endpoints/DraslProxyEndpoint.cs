using System.Net.Http.Headers;
using Yarp.ReverseProxy.Transforms;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class DraslProxyEndpoint(IConfiguration configuration)
{
    private const string Prefix = "/drasl";

    public void Map(IEndpointRouteBuilder routes)
    {
        if (!Uri.TryCreate(configuration["Drasl:Url"], UriKind.Absolute, out var upstream))
            return;

        var authority = upstream.GetLeftPart(UriPartial.Authority);
        var upstreamPrefix = new PathString(upstream.AbsolutePath.TrimEnd('/'));
        routes.MapForwarder(Prefix + "/{**path}", authority, transforms =>
        {
            transforms.AddRequestTransform(request =>
            {
                request.Path.StartsWithSegments(Prefix, out var relative);
                if (Drasl4MountsBearerRouteAtRoot(relative))
                {
                    if (!AuthenticationHeaderValue.TryParse(request.HttpContext.Request.Headers.Authorization, out var authorization) ||
                        !authorization.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(authorization.Parameter))
                    {
                        request.HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return ValueTask.CompletedTask;
                    }

                    request.Path = relative;
                }
                else
                {
                    request.Path = upstreamPrefix.Add(relative);
                }
                return ValueTask.CompletedTask;
            });
        });
    }

    private static bool Drasl4MountsBearerRouteAtRoot(PathString path)
    {
        // Drasl 4 mounts these bearer routes outside its BaseURL path.
        if (path.StartsWithSegments("/authlib-injector/api/user/profile"))
            return true;

        if (path.StartsWithSegments("/authlib-injector/minecraftservices", out var service) ||
            path.StartsWithSegments("/services", out service))
            return service.HasValue && service != "/publickeys";

        return path == "/mppass" || path == "/session/mppass" || path == "/authlib-injector/sessionserver/mppass";
    }
}
