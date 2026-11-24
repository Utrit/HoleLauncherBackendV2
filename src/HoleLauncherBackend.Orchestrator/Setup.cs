using HoleLauncherBackend.Shared;

namespace HoleLauncherBackend.Orchestrator;

internal static class Setup
{
    public static IHost Build(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddKafkaBus(builder.Configuration);
        builder.Services.AddPostgres(builder.Configuration, "Orchestrator");
        builder.Services.AddSingleton<DBUtils>();
        builder.Services.AddSingleton<OperationStore>();
        builder.Services.AddSingleton<OperationHandler>();
        builder.Services.AddSingleton<StorageUploadHandler>();
        builder.Services.AddHostedService<StatusMessageWorker>();
        builder.Services.AddHostedService<OperationTimeoutWorker>();
        return builder.Build();
    }
}
