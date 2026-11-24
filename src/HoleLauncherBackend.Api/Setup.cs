using HoleLauncherBackend.Shared;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using HoleLauncherBackend.Api.Endpoints;
using HoleLauncherBackend.Contracts;
using Microsoft.AspNetCore.StaticFiles;

namespace HoleLauncherBackend.Api;

internal static class Setup
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddKafkaBus(builder.Configuration);
        builder.Services.AddSingleton<KafkaAccessClient>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<KafkaAccessClient>());
        builder.Services.AddHealthChecks();
        builder.Services.AddSingleton(new SessionLookup(builder.Configuration["Auth:SessionKey"]));
        builder.Services.AddHttpClient("accounts", client =>
        {
            var address = builder.Configuration["Auth:AccountsUrl"]
                ?? throw new InvalidOperationException("Auth:AccountsUrl is required.");
            client.BaseAddress = new Uri(address);
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        builder.Services.AddHttpClient("storage", client =>
        {
            client.BaseAddress = new Uri(builder.Configuration["Storage:Url"]!);
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
        builder.Host.ConfigureContainer<ContainerBuilder>(container =>
        {
            container.RegisterType<StorageProxy>().SingleInstance();
            container.RegisterType<StorageUploadEndpoint>().As<IStorageEndpoint>().SingleInstance();
            container.RegisterType<StorageDownloadEndpoint>().As<IStorageEndpoint>().SingleInstance();
            container.RegisterType<StorageMetadataEndpoint>().As<IStorageEndpoint>().SingleInstance();
            container.RegisterType<StorageDeleteEndpoint>().As<IStorageEndpoint>().SingleInstance();
            container.RegisterType<EndpointMapper>().SingleInstance();
            container.RegisterType<EndpointMapper<IEndpointRouteBuilder, IAccountEndpoint>>().SingleInstance();
            container.RegisterType<EndpointMapper<IEndpointRouteBuilder, IStorageEndpoint>>().SingleInstance();
            container.RegisterType<UsersEndpoint>().As<IAccountEndpoint>().SingleInstance();
            container.RegisterType<OperationStatusEndpoint>().As<IAccountEndpoint>().SingleInstance();
            container.RegisterType<UserSuppressionEndpoint>().As<IAccountEndpoint>().SingleInstance();
            container.RegisterType<UserPermissionsEndpoint>().As<IAccountEndpoint>().SingleInstance();
            container.RegisterType<PasswordEndpoint>().As<IAccountEndpoint>().SingleInstance();
            container.RegisterType<AccountManagementProxy>().SingleInstance();
            container.RegisterType<StatusEndpoint>().SingleInstance();
            container.RegisterType<HealthEndpoint>().SingleInstance();
            container.RegisterType<RegisterEndpoint>().As<IAccountEndpoint>().SingleInstance();
            container.RegisterType<LoginEndpoint>().As<IAccountEndpoint>().SingleInstance();
            container.RegisterType<MeEndpoint>().As<IAccountEndpoint>().SingleInstance();
            container.RegisterType<LogoutEndpoint>().As<IAccountEndpoint>().SingleInstance();
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var target = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget;
            if (StorageAddress.HasUnsafePath(target, "/api/storage/"))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            await next(context);
        });
        app.UseDefaultFiles();
        var webAssemblyTypes = new FileExtensionContentTypeProvider();
        webAssemblyTypes.Mappings[".dat"] = "application/octet-stream";
        app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = webAssemblyTypes });
        app.Services.GetRequiredService<EndpointMapper>().Map(app);
        return app;
    }
}
