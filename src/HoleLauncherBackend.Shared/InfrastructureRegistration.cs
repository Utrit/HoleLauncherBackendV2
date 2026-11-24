using HoleLauncherBackend.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HoleLauncherBackend.Shared;

public static class InfrastructureRegistration
{
    public static void AddKafkaBus(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => new KafkaBus(
            configuration["Kafka:BootstrapServers"]!,
            configuration["Kafka:Username"]!,
            configuration["Kafka:Password"]!));
    }

    public static void AddPostgres(this IServiceCollection services, IConfiguration configuration, string name)
    {
        services.AddSingleton(_ =>
        {
            var connection = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString(name))
            {
                Password = configuration[$"{name}:Password"]
            };
            return NpgsqlDataSource.Create(connection.ConnectionString);
        });
    }
}
