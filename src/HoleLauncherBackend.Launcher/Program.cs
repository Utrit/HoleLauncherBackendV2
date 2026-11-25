using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Launcher;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<LauncherStore>();
builder.Services.AddHttpClient("github", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("HoleLauncherBackend/1.0");
    client.Timeout = Timeout.InfiniteTimeSpan;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

var app = builder.Build();
app.MapGet("/health", () => Results.Ok());
app.MapGet("/release", (LauncherStore store) => Results.Json(store.Current));
app.MapPut("/release", async (LauncherSource request, LauncherStore store, CancellationToken cancellation) =>
{
    try
    {
        return await store.ImportAsync(request.Repository, cancellation) is { } release
            ? Results.Ok(release) : Results.BadRequest();
    }
    catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException ||
        error is OperationCanceledException && !cancellation.IsCancellationRequested)
    {
        app.Logger.LogWarning("Launcher import failed ({ErrorType})", error.GetType().Name);
        return Results.StatusCode(StatusCodes.Status502BadGateway);
    }
});
app.MapGet("/windows", (LauncherStore store) => Download(store, false));
app.MapGet("/linux", (LauncherStore store) => Download(store, true));
app.Run();

static IResult Download(LauncherStore store, bool linux) => store.Open(linux) is { } file
    ? Results.File(file, "application/octet-stream", linux ? "HoleLauncher-linux-x64.tar.gz" : "HoleLauncher.exe")
    : Results.NotFound();
