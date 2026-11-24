using Autofac;
using Autofac.Extensions.DependencyInjection;
using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Shared;

namespace HoleLauncherBackend.Storage;

internal static class Setup
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = StorageAddress.MaxUploadBytes);
        builder.Services.AddHealthChecks();
        builder.Services.AddSingleton(_ => new FileStore(builder.Configuration["Storage:Root"] ?? "/data/storage"));
        builder.Services.AddKafkaBus(builder.Configuration);
        builder.Services.AddSingleton<UploadWatchdog>();
        builder.Services.AddHostedService<StorageWorker>();
        builder.Services.AddHostedService<StagingCleanup>();
        builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
        builder.Host.ConfigureContainer<ContainerBuilder>(container =>
        {
            container.RegisterType<EndpointMapper<IEndpointRouteBuilder, IStorageEndpoint>>().SingleInstance();
            container.RegisterType<UploadEndpoint>().As<IStorageEndpoint>().SingleInstance();
            container.RegisterType<MetadataEndpoint>().As<IStorageEndpoint>().SingleInstance();
            container.RegisterType<DownloadEndpoint>().As<IStorageEndpoint>().SingleInstance();
            container.RegisterType<DeleteEndpoint>().As<IStorageEndpoint>().SingleInstance();
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var target = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget;
            if (StorageAddress.HasUnsafePath(target, "/files/") || StorageAddress.HasUnsafePath(target, "/staging/"))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            await next(context);
        });
        app.MapHealthChecks("/health/live");
        app.MapFallback("/files/{**path}", () => Results.BadRequest());
        app.MapFallback("/staging/{**path}", () => Results.BadRequest());
        app.Services.GetRequiredService<EndpointMapper<IEndpointRouteBuilder, IStorageEndpoint>>().Map(app);
        return app;
    }
}
