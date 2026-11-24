using System.Runtime.InteropServices.JavaScript;
using Avalonia;
using Avalonia.Browser;

namespace HoleLauncher.Admin;

internal static partial class Program
{
    public static Task Main() => AppBuilder.Configure<App>().StartBrowserAppAsync("admin");

    [JSImport("getOrigin", "admin")]
    public static partial string GetOrigin();
}
