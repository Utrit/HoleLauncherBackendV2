using System.Net;
using HoleLauncherBackend.Contracts;
using static HoleLauncherBackend.Api.SessionCookie;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class LoginEndpoint : IAccountEndpoint
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/login", async (Credentials credentials, HttpContext context, IWebHostEnvironment environment, IHttpClientFactory clients, CancellationToken cancellation) =>
        {
            using var response = await clients.CreateClient("accounts").PostAsJsonAsync("login", credentials, cancellation);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests)
                return Results.StatusCode((int)response.StatusCode);
            if (!response.IsSuccessStatusCode)
                return Results.StatusCode(503);

            var session = await response.Content.ReadFromJsonAsync<CreatedSession>(cancellation);
            if (session is null)
                return Results.StatusCode(503);

            var options = CookieSettings(context, environment);
            options.MaxAge = TimeSpan.FromHours(8);
            context.Response.Cookies.Append(CookieName, session.SessionId, options);
            return Results.Ok(session.Account);
        });
    }
}
