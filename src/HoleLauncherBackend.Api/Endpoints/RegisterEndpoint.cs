using System.Net;
using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Shared;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class RegisterEndpoint : IAccountEndpoint
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/register", async (Credentials credentials, IHttpClientFactory clients, DraslClient drasl, GameRegistration registration, CancellationToken cancellation) =>
        {
            using var response = await clients.CreateClient("accounts").PostAsJsonAsync("register", credentials, cancellation);
            if (response.IsSuccessStatusCode && drasl.IsConfigured)
            {
                var account = await response.Content.ReadFromJsonAsync<AccountIdentity>(cancellation);
                if (account is null)
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                var status = await registration.WaitAsync(account.AccountId, cancellation);
                return status switch
                {
                    true => Results.StatusCode(StatusCodes.Status201Created),
                    false => Results.Json(new OperationAccepted(account.AccountId), statusCode: StatusCodes.Status503ServiceUnavailable),
                    _ => Results.Accepted($"/api/auth/operations/{account.AccountId:D}", new OperationAccepted(account.AccountId))
                };
            }
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
