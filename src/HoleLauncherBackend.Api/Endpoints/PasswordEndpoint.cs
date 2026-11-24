using System.Net;
using HoleLauncherBackend.Contracts;
using static HoleLauncherBackend.Api.SessionCookie;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class PasswordEndpoint : IAccountEndpoint
{
    public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/password", ChangeAsync);

    private static async Task<IResult> ChangeAsync(PasswordChange change, HttpContext context,
        SessionLookup lookup, IHttpClientFactory clients, CancellationToken cancellation)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var sessionId))
            return Results.Unauthorized();

        var request = new ChangePasswordRequest(lookup.Compute(sessionId), change);
        using var response = await clients.CreateClient("accounts").PostAsJsonAsync("password", request, cancellation);
        return response.StatusCode switch
        {
            HttpStatusCode.NoContent or HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                or HttpStatusCode.TooManyRequests => Results.StatusCode((int)response.StatusCode),
            _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        };
    }
}
