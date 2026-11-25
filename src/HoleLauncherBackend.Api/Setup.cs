using HoleLauncherBackend.Shared;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using HoleLauncherBackend.Api.Endpoints;
using HoleLauncherBackend.Contracts;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using System.Net;

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
        builder.Services.AddHttpForwarder();
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            if (IPAddress.TryParse(builder.Configuration["Proxy:TrustedAddress"], out var proxy))
                options.KnownProxies.Add(proxy);
            if (Uri.TryCreate(builder.Configuration["Proxy:PublicOrigin"], UriKind.Absolute, out var publicOrigin))
                options.AllowedHosts.Add(publicOrigin.Host);
        });
        builder.Services.AddPostgres(builder.Configuration, "GameProfiles");
        builder.Services.AddSingleton<GameProfileStore>();
        builder.Services.AddSingleton<StorageAdministration>();
        builder.Services.AddSingleton<GameRegistration>();
        builder.Services.AddSingleton<DraslClient>();
        builder.Services.AddSingleton(new SessionLookup(builder.Configuration["Auth:SessionKey"]));
        builder.Services.AddHttpClient("accounts", client =>
        {
            var address = builder.Configuration["Auth:AccountsUrl"]
                ?? throw new InvalidOperationException("Auth:AccountsUrl is required.");
            client.BaseAddress = new Uri(address);
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        builder.Services.AddHttpClient("modrinth-assets")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddHttpClient("repository-archive", client =>
        {
            client.BaseAddress = new Uri(builder.Configuration["Repository:Url"]!);
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        builder.Services.AddHttpClient("repository", client =>
            client.BaseAddress = new Uri(builder.Configuration["Repository:Url"]!));
        builder.Services.AddHttpClient("launcher", client =>
        {
            client.BaseAddress = new Uri(builder.Configuration["Launcher:Url"] ?? "http://launcher:8080/");
            client.Timeout = TimeSpan.FromMinutes(6);
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
            container.RegisterType<DraslProxyEndpoint>().SingleInstance();
            container.RegisterType<RegisterEndpoint>().As<IAccountEndpoint>().SingleInstance();
            container.RegisterType<LoginEndpoint>().As<IAccountEndpoint>().SingleInstance();
            container.RegisterType<LauncherEndpoint>().AsSelf().As<IAccountEndpoint>().SingleInstance();
            container.RegisterType<RepositoryEndpoint>().As<IAccountEndpoint>().SingleInstance();
            container.RegisterType<StorageAdminEndpoint>().As<IAccountEndpoint>().SingleInstance();
            container.RegisterType<GameProfileEndpoint>().As<IAccountEndpoint>().SingleInstance();
            container.RegisterType<MeEndpoint>().As<IAccountEndpoint>().SingleInstance();
            container.RegisterType<LogoutEndpoint>().As<IAccountEndpoint>().SingleInstance();
        });

        var app = builder.Build();
        app.UseForwardedHeaders();
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
        webAssemblyTypes.Mappings[".locale"] = "application/json";
        webAssemblyTypes.Mappings[".dat"] = "application/octet-stream";
        app.UseStaticFiles(new StaticFileOptions
        {
            ContentTypeProvider = webAssemblyTypes,
            OnPrepareResponse = response =>
            {
                if (response.File.Name is "index.html" or "main.js" or "dotnet.js" || response.File.Name.EndsWith(".locale", StringComparison.Ordinal))
                    response.Context.Response.Headers.CacheControl = "no-cache";
            }
        });
        app.Services.GetRequiredService<EndpointMapper>().Map(app);
        return app;
    }
}
