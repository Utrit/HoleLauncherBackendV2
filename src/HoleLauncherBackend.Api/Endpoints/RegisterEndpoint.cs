using System.Net;
using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class RegisterEndpoint : IAccountEndpoint
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/register", async (Credentials credentials, IHttpClientFactory clients, CancellationToken cancellation) =>
        {
            using var response = await clients.CreateClient("accounts").PostAsJsonAsync("register", credentials, cancellation);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => Results.StatusCode(201),
                HttpStatusCode.BadRequest => Results.BadRequest(),
                HttpStatusCode.Conflict => Results.Conflict(),
                HttpStatusCode.TooManyRequests => Results.StatusCode(429),
                _ => Results.StatusCode(503)
            };
        });
    }
}
