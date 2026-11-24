namespace HoleLauncherBackend.Api;

internal static class SessionCookie
{
    public const string CookieName = "holelauncher.session";

    public static CookieOptions CookieSettings(HttpContext context, IWebHostEnvironment environment) => new()
    {
        HttpOnly = true,
        Secure = context.Request.IsHttps || !environment.IsDevelopment(),
        SameSite = SameSiteMode.Strict,
        Path = "/api"
    };
}
