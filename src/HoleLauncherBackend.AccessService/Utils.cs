using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.AccessService;

internal static partial class Utils
{
    [GeneratedRegex(@"\A[A-Za-z0-9]{3,}\z")]
    private static partial Regex UserNamePattern();

    public static bool IsValid(Credentials credentials) =>
        credentials.UserName is { } name &&
        UserNamePattern().IsMatch(name) &&
        credentials.Password is not null;

    public static byte[] HashPassword(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
}
