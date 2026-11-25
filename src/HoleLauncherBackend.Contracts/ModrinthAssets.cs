namespace HoleLauncherBackend.Contracts;

public static class ModrinthAssets
{
    public static bool IsCdnUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var address) && address.Scheme == Uri.UriSchemeHttps &&
        address.Host == "cdn.modrinth.com" && address.IsDefaultPort && address.UserInfo.Length == 0 &&
        address.AbsolutePath.StartsWith("/data/", StringComparison.Ordinal);
}
