using System.Net.Http.Json;
using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Shared;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HoleLauncherBackend.Orchestrator;

internal sealed class ServiceStatusProbes(IConfiguration configuration, NpgsqlDataSource operations,
    [FromKeyedServices("game-profiles")] NpgsqlDataSource profiles, DraslClient drasl) : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private readonly HttpClient http = new() { Timeout = Timeout };

    public Task<ServiceStatus[]> ReadAsync(CancellationToken cancellation) => Task.WhenAll(
        DatabaseAsync("Orchestrator PostgreSQL", operations, cancellation),
        DatabaseAsync("Game Profiles PostgreSQL", profiles, cancellation),
        ProbeAsync("Storage", "Storage:Url", "health/status", storage: true, cancellation),
        ProbeAsync("Launcher", "Launcher:Url", "health", storage: false, cancellation),
        ProbeAsync("Repository", "Repository:Url", "health", storage: false, cancellation),
        drasl.GetStatusAsync(cancellation));

    private async Task<ServiceStatus> ProbeAsync(string name, string key, string path, bool storage, CancellationToken cancellation)
    {
        if (!Uri.TryCreate(configuration[key], UriKind.Absolute, out var address))
            return new(name, "Not configured");

        try
        {
            using var response = await http.GetAsync(new Uri(address, path), cancellation);
            if (!response.IsSuccessStatusCode)
                return new(name, "Offline");
            if (!storage)
                return new(name, "Online");
            return await response.Content.ReadFromJsonAsync<ServiceStatus>(cancellation) ?? new(name, "Offline");
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested &&
            error is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            return new(name, "Offline");
        }
    }

    private static async Task<ServiceStatus> DatabaseAsync(string name, NpgsqlDataSource database, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(Timeout);
        try
        {
            await using var command = database.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(deadline.Token);
            return new(name, "Online");
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested &&
            error is NpgsqlException or OperationCanceledException)
        {
            return new(name, "Offline");
        }
    }

    public void Dispose() => http.Dispose();
}
