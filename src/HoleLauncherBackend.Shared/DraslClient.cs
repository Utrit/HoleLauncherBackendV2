using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using HoleLauncherBackend.Contracts;
using Microsoft.Extensions.Configuration;

namespace HoleLauncherBackend.Shared;

public sealed class DraslClient(IConfiguration configuration) : IDisposable
{
    private const string ManagementPath = "drasl/api/v3";
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private readonly HttpClient http = new() { Timeout = RequestTimeout };

    public bool IsConfigured => Uri.TryCreate(configuration["Drasl:Url"], UriKind.Absolute, out _) &&
        !string.IsNullOrWhiteSpace(configuration["Drasl:ApiToken"]) &&
        !string.IsNullOrWhiteSpace(configuration["Drasl:CredentialKey"]);

    public async Task<ServiceStatus> GetStatusAsync(CancellationToken cancellation)
    {
        if (!IsConfigured)
            return new("Drasl", "Not configured");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(StatusTimeout);
        try
        {
            using var response = await http.GetAsync(Address("auth"), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            return new("Drasl", response.IsSuccessStatusCode ? "Online" : "Offline");
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested &&
            error is HttpRequestException or OperationCanceledException)
        {
            return new("Drasl", "Offline");
        }
    }

    private static string UserName(Guid accountId) => $"hl_{accountId:N}"[..16];

    private string Password(Guid accountId) =>
        Convert.ToHexString(HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(configuration["Drasl:CredentialKey"]!),
            accountId.ToByteArray()));

    public async Task<string?> FindNameAsync(Guid accountId, CancellationToken cancellation)
    {
        using var request = Management(HttpMethod.Get, $"players/{accountId:D}");
        using var response = await http.SendAsync(request, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        var player = await response.Content.ReadFromJsonAsync<ManagedPlayer>(cancellation);
        if (player?.Uuid != accountId || string.IsNullOrEmpty(player.Name))
            throw new JsonException("Drasl returned a different or invalid player.");
        return player.Name;
    }

    public async Task<string> ProvisionAsync(Guid accountId, string playerName, CancellationToken cancellation)
    {
        if (await FindNameAsync(accountId, cancellation) is { } existingName)
            return existingName;

        using var request = Management(HttpMethod.Post, "users");
        request.Content = JsonContent.Create(new
        {
            username = UserName(accountId), password = Password(accountId),
            playerName, chosenUuid = accountId.ToString("D"), maxPlayerCount = 1
        });
        using var response = await http.SendAsync(request, cancellation);
        response.EnsureSuccessStatusCode();
        return await FindNameAsync(accountId, cancellation)
            ?? throw new JsonException("Drasl did not create the requested player UUID.");
    }

    public async Task<AccessOutcome> RenameAsync(Guid accountId, string name, CancellationToken cancellation)
    {
        using var request = Management(HttpMethod.Patch, $"players/{accountId:D}");
        request.Content = JsonContent.Create(new { name });
        using var response = await http.SendAsync(request, cancellation);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>(cancellation);
            return response.StatusCode == HttpStatusCode.Conflict ||
                error?.Message?.Contains("player name is taken", StringComparison.OrdinalIgnoreCase) == true ||
                error?.Message?.Contains("player name is in use", StringComparison.OrdinalIgnoreCase) == true
                ? AccessOutcome.Conflict : AccessOutcome.InvalidRequest;
        }
        response.EnsureSuccessStatusCode();
        return AccessOutcome.NoContent;
    }

    public async Task<HttpStatusCode> SetTextureAsync(Guid accountId, ArraySegment<byte> png, bool skin,
        string model, CancellationToken cancellation)
    {
        var base64 = Convert.ToBase64String(png.AsSpan());
        using var request = Management(HttpMethod.Patch, $"players/{accountId:D}");
        request.Content = skin
            ? JsonContent.Create(new { skinBase64 = base64, skinModel = model })
            : JsonContent.Create(new { capeBase64 = base64 });
        using var response = await http.SendAsync(request, cancellation);
        return response.StatusCode;
    }

    public async Task<HttpStatusCode> ClearTextureAsync(Guid accountId, bool skin, CancellationToken cancellation)
    {
        using var request = Management(HttpMethod.Patch, $"players/{accountId:D}");
        request.Content = JsonContent.Create(new { deleteSkin = skin, deleteCape = !skin });
        using var response = await http.SendAsync(request, cancellation);
        return response.StatusCode;
    }

    public async Task<DraslSession?> AuthenticateAsync(Guid accountId, CancellationToken cancellation)
    {
        var playerName = await FindNameAsync(accountId, cancellation);
        if (playerName is null)
            return null;

        using var response = await http.PostAsJsonAsync(Address("auth/authenticate"), new
        {
            agent = new { name = "Minecraft", version = 1 },
            username = playerName,
            password = Password(accountId),
            clientToken = Guid.NewGuid().ToString("N")
        }, cancellation);
        if (!response.IsSuccessStatusCode)
            return null;
        var session = await response.Content.ReadFromJsonAsync<DraslSession>(cancellation);
        return Guid.TryParseExact(session?.SelectedProfile?.Id, "N", out var id) && id == accountId ? session : null;
    }

    private HttpRequestMessage Management(HttpMethod method, string path)
    {
        // Drasl 4 mounts its management API at the server root, independently of BaseURL.
        var address = new Uri(Address(""), $"/{ManagementPath}/{path}");
        var request = new HttpRequestMessage(method, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration["Drasl:ApiToken"]);
        request.Headers.AcceptLanguage.ParseAdd("en");
        return request;
    }

    private Uri Address(string path) => new($"{configuration["Drasl:Url"]!.TrimEnd('/')}/{path}", UriKind.Absolute);

    public void Dispose() => http.Dispose();

    private sealed record ManagedPlayer(Guid Uuid, string Name);
    private sealed record ApiError(string? Message);
}
