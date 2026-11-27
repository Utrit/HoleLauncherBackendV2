using System.Net;
using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Shared;
using System.Text.Json;
using static HoleLauncherBackend.Api.SessionCookie;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class LoginEndpoint : IAccountEndpoint
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/login", async (Credentials credentials, HttpContext context, IWebHostEnvironment environment, IHttpClientFactory clients, DraslClient drasl, CancellationToken cancellation) =>
        {
            var (status, session) = await CreateSessionAsync(credentials, context, environment, clients, cancellation);
            if (session is null)
                return Results.StatusCode(status);

            DraslSession? gameSession = null;
            if (drasl.IsConfigured)
            {
                try { gameSession = await drasl.AuthenticateAsync(session.Account.AccountId, cancellation); }
                catch (Exception error) when (!cancellation.IsCancellationRequested &&
                    error is HttpRequestException or OperationCanceledException or JsonException) { }
            }

            return Results.Ok(new
            {
                session.Account.AccountId, session.Account.UserName, session.Account.Permissions,
                session.Account.Suppressed, DraslSession = gameSession
            });
        });

        routes.MapPost("/game-session", RenewGameSessionAsync);

        routes.MapPost("/login/form", async (HttpContext context, IWebHostEnvironment environment,
            IHttpClientFactory clients, IConfiguration configuration, CancellationToken cancellation) =>
        {
            if (!context.Request.HasFormContentType ||
                context.Request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) != true ||
                !IsExpectedOrigin(context, configuration))
                return Results.BadRequest();

            var form = await context.Request.ReadFormAsync(cancellation);
            var (status, session) = await CreateSessionAsync(new Credentials(form["username"], form["password"]),
                context, environment, clients, cancellation);
            return Results.Redirect(session is not null ? "/" : status switch
            {
                StatusCodes.Status401Unauthorized => "/?login=invalid",
                StatusCodes.Status429TooManyRequests => "/?login=limited",
                _ => "/?login=unavailable"
            });
        });
    }

    private static async Task<IResult> RenewGameSessionAsync(HttpContext context, AccountManagementProxy accounts,
        DraslClient drasl, CancellationToken cancellation)
    {
        if (context.Request.Headers["X-HoleLauncher-Request"] != "1")
            return Results.BadRequest();

        var reply = await accounts.SendAsync(context, AccessOperation.Session, cancellation);
        if (reply is not { Outcome: AccessOutcome.Ok, Account: { } account })
            return AccountManagementProxy.Result(reply);

        context.Response.Headers.CacheControl = "no-store";
        if (drasl.IsConfigured)
        {
            try
            {
                if (await drasl.AuthenticateAsync(account.AccountId, cancellation) is { } session)
                    return Results.Ok(session);
            }
            catch (Exception error) when (!cancellation.IsCancellationRequested &&
                error is HttpRequestException or OperationCanceledException or JsonException) { }
        }
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    private static bool IsExpectedOrigin(HttpContext context, IConfiguration configuration)
    {
        var expected = Uri.TryCreate(configuration["Proxy:PublicOrigin"], UriKind.Absolute, out var origin)
            ? origin.GetLeftPart(UriPartial.Authority)
            : $"{context.Request.Scheme}://{context.Request.Host}";
        return string.Equals(context.Request.Headers.Origin, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int Status, CreatedSession? Session)> CreateSessionAsync(
        Credentials credentials, HttpContext context, IWebHostEnvironment environment,
        IHttpClientFactory clients, CancellationToken cancellation)
    {
        using var response = await clients.CreateClient("accounts").PostAsJsonAsync("login", credentials, cancellation);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests)
            return ((int)response.StatusCode, null);
        if (!response.IsSuccessStatusCode)
            return (StatusCodes.Status503ServiceUnavailable, null);

        var session = await response.Content.ReadFromJsonAsync<CreatedSession>(cancellation);
        if (session is null)
            return (StatusCodes.Status503ServiceUnavailable, null);

        var options = CookieSettings(context, environment);
        options.MaxAge = TimeSpan.FromHours(8);
        context.Response.Cookies.Append(CookieName, session.SessionId, options);
        return (StatusCodes.Status200OK, session);
    }
}
