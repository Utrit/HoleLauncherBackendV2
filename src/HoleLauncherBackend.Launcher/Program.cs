using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Launcher;
using HoleLauncherBackend.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<LauncherStore>();
builder.Services.AddHostedService<LauncherPublicationRecovery>();
builder.Services.AddSingleton<LauncherStorage>();
builder.Services.AddSingleton(_ => new KafkaBus(builder.Configuration["Kafka:BootstrapServers"]!,
    builder.Configuration["Kafka:Username"]!, builder.Configuration["Kafka:Password"]!));
builder.Services.AddHttpClient("storage", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Storage:Url"] ?? "http://storage:8080/");
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddHttpClient("github", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("HoleLauncherBackend/1.0");
    client.Timeout = Timeout.InfiniteTimeSpan;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

var app = builder.Build();
app.MapGet("/health", () => Results.Ok());
app.MapGet("/release", (LauncherStore store) => store.Current is { } release ? Results.Ok(release) : Results.NoContent());
app.MapPut("/release", async (LauncherImport request, LauncherStore store, CancellationToken cancellation) =>
{
    try
    {
        return await store.ImportAsync(request.Repository, request.Uploader, cancellation) is { } release
            ? Results.Ok(release) : Results.BadRequest();
    }
    catch (LauncherImportException error)
    {
        return Results.Json(new LauncherImportFailure(error.Stage, error.Failure), statusCode: StatusCodes.Status502BadGateway);
    }
    catch (Exception error) when (error is IOException or System.Text.Json.JsonException ||
        error is OperationCanceledException && !cancellation.IsCancellationRequested)
    {
        app.Logger.LogWarning("Launcher import failed ({ErrorType})", error.GetType().Name);
        return Results.StatusCode(StatusCodes.Status502BadGateway);
    }
});
app.MapGet("/{platform}", (string platform, LauncherStore store, LauncherStorage storage, HttpContext context) =>
    DownloadAsync(platform, hash: false, store, storage, context));
app.MapGet("/{platform}/sha512", (string platform, LauncherStore store, LauncherStorage storage, HttpContext context) =>
    DownloadAsync(platform, hash: true, store, storage, context));
app.Run();

static async Task<IResult> DownloadAsync(string platform, bool hash, LauncherStore store, LauncherStorage storage, HttpContext context)
{
    if (platform is not ("windows" or "linux") || store.FindArtifact(platform == "linux") is not { } artifact)
        return Results.NotFound();

    try
    {
        var response = await storage.ReadAsync(artifact.FileId, hash, context.RequestAborted);
        context.Response.RegisterForDispose(response);
        if (!response.IsSuccessStatusCode)
            return Results.StatusCode((int)response.StatusCode);

        context.Response.Headers["X-Launcher-Build-Sha512"] = artifact.Fingerprint;
        context.Response.ContentLength = response.Content.Headers.ContentLength;
        return Results.Stream(await response.Content.ReadAsStreamAsync(context.RequestAborted),
            hash ? "text/plain; charset=utf-8" : "application/octet-stream");
    }
    catch (HttpRequestException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}
