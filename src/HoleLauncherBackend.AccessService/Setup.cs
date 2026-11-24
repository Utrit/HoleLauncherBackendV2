using Autofac;
using Autofac.Extensions.DependencyInjection;
using HoleLauncherBackend.AccessService.AccessCommands;
using HoleLauncherBackend.AccessService.Endpoints;
using HoleLauncherBackend.AccessService.KafkaHandlers;
using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Shared;
using Microsoft.AspNetCore.RateLimiting;

namespace HoleLauncherBackend.AccessService;

internal static class Setup
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        ConfigureKafka(builder.Services, builder.Configuration);
        ConfigureAccounts(builder.Services, builder.Configuration);
        ConfigureHttp(builder.Services);
        ConfigureAutofac(builder.Host);

        var app = builder.Build();
        ConfigurePipeline(app);
        return app;
    }

    private static void ConfigureKafka(IServiceCollection services, IConfiguration configuration)
    {
        services.AddKafkaBus(configuration);
        services.AddHostedService<AccessMessageWorker>();
    }

    private static void ConfigureAccounts(IServiceCollection services, IConfiguration configuration)
    {
        services.AddPostgres(configuration, "Accounts");

        services.AddSingleton(new SessionLookup(configuration["Auth:SessionKey"]));
        services.AddSingleton<DBUtils>();
        services.AddSingleton<AccountStore>();
        services.AddSingleton<AccountOperationExecutor>();
    }

    private static void ConfigureHttp(IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter("credentials", limiter =>
            {
                limiter.PermitLimit = 60;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
            });
        });
    }

    private static void ConfigureAutofac(ConfigureHostBuilder host)
    {
        host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
        host.ConfigureContainer<ContainerBuilder>(container =>
        {
            RegisterKafkaHandlers(container);
            RegisterAccountMutations(container);
            RegisterEndpoints(container);
        });
    }

    private static void RegisterKafkaHandlers(ContainerBuilder container)
    {
        container.RegisterType<AccessRequestMessageHandler>().As<IKafkaMessageHandler>().SingleInstance();
        container.RegisterType<AccountExecutionMessageHandler>().As<IKafkaMessageHandler>().SingleInstance();
        container.RegisterType<AccessCommandRouter>().SingleInstance();
        container.RegisterType<LogoutConsumer>().As<IAccessCommandConsumer>().SingleInstance();
        container.RegisterType<SessionConsumer>().As<IAccessCommandConsumer>().SingleInstance();
        container.RegisterType<UsersConsumer>().As<IAccessCommandConsumer>().SingleInstance();
        container.RegisterType<SystemStatusConsumer>().As<IAccessCommandConsumer>().SingleInstance();
        container.RegisterType<OperationStatusConsumer>().As<IAccessCommandConsumer>().SingleInstance();
        container.RegisterType<ChangePermissionsConsumer>().As<IAccessCommandConsumer>().SingleInstance();
        container.RegisterType<StorageUploadConsumer>().As<IAccessCommandConsumer>().SingleInstance();
        container.RegisterType<ChangeSuppressionConsumer>().As<IAccessCommandConsumer>().SingleInstance();
    }

    private static void RegisterAccountMutations(ContainerBuilder container)
    {
        container.RegisterType<ChangeAccountPermissionsHandler>().As<IAccountMutationHandler>().SingleInstance();
        container.RegisterType<ChangeAccountSuppressionHandler>().As<IAccountMutationHandler>().SingleInstance();
    }

    private static void RegisterEndpoints(ContainerBuilder container)
    {
        container.RegisterType<EndpointMapper<IEndpointRouteBuilder, IEndpoint>>().SingleInstance();
        container.RegisterType<RegisterEndpoint>().As<IEndpoint>().SingleInstance();
        container.RegisterType<LoginEndpoint>().As<IEndpoint>().SingleInstance();
        container.RegisterType<PasswordEndpoint>().As<IEndpoint>().SingleInstance();
    }

    private static void ConfigurePipeline(WebApplication app)
    {
        app.UseRateLimiter();
        app.Services.GetRequiredService<EndpointMapper<IEndpointRouteBuilder, IEndpoint>>().Map(app);
    }
}
