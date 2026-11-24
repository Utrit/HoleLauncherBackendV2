using HoleLauncherBackend.Contracts;
using static HoleLauncherBackend.Api.SessionCookie;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class LogoutEndpoint : IAccountEndpoint
{
    public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/logout", LogoutAsync);

    private static async Task<IResult> LogoutAsync(HttpContext context, IWebHostEnvironment environment,
        AccountManagementProxy proxy, CancellationToken cancellation)
    {
        if (context.Request.Headers["X-HoleLauncher-Request"] != "1")
            return Results.BadRequest();

        if (context.Request.Cookies.ContainsKey(CookieName))
        {
            var reply = await proxy.SendAsync(context, AccessOperation.Logout, cancellation);
            if (reply.Outcome is not (AccessOutcome.NoContent or AccessOutcome.InvalidSession))
                return AccountManagementProxy.Result(reply);
        }

        context.Response.Cookies.Delete(CookieName, CookieSettings(context, environment));
        return Results.NoContent();
    }
}
