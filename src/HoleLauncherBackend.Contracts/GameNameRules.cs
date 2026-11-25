using System.Text.RegularExpressions;

namespace HoleLauncherBackend.Contracts;

public static partial class GameNameRules
{
    public static bool IsValid(string? name) => name is not null && Pattern().IsMatch(name);



    [GeneratedRegex(@"\A[A-Za-z0-9_]{3,16}\z")]
    private static partial Regex Pattern();
}
