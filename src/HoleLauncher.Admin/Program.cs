using System.Runtime.InteropServices.JavaScript;
using Avalonia;
using Avalonia.Browser;

namespace HoleLauncher.Admin;

internal static partial class Program
{
    public static Task Main() => AppBuilder.Configure<App>().StartBrowserAppAsync("admin");

    [JSImport("translate", "admin")]
    public static partial string Translate(string key);

    [JSImport("getLanguage", "admin")]
    public static partial string GetLanguage();

    [JSImport("setLanguage", "admin")]
    public static partial void SetLanguage(string language);

    [JSImport("showLogin", "admin")]
    public static partial void ShowLogin(bool visible, string? message = null);

    [JSImport("registerModrinthPaste", "admin")]
    public static partial void RegisterModrinthPaste(
        [JSMarshalAs<JSType.Function<JSType.Boolean>>] Func<bool> focused,
        [JSMarshalAs<JSType.Function<JSType.String>>] Action<string> paste);

    [JSImport("readRepositoryFile", "admin")]
    public static partial Task<JSObject> ReadRepositoryFile(JSObject file);

    [JSImport("copyRepositoryBytes", "admin")]
    public static partial byte[] CopyRepositoryBytes(JSObject data);

    [JSImport("createRepositoryFile", "admin")]
    public static partial JSObject CreateRepositoryFile(string name, byte[] data);

    [JSImport("chooseRepositoryFile", "admin")]
    public static partial Task<JSObject?> ChooseRepositoryFile(bool image);

    [JSImport("takeRepositoryDrop", "admin")]
    public static partial JSObject? TakeRepositoryDrop();

    [JSImport("uploadRepositoryFile", "admin")]
    public static partial Task<string> UploadRepositoryFile(string path, JSObject file,
        [JSMarshalAs<JSType.Function<JSType.Number, JSType.Number, JSType.Number>>] Action<double, double, double> progress);

    [JSImport("downloadRepositoryArchive", "admin")]
    public static partial Task<int> DownloadRepositoryArchive(string path, string fallback);

    [JSImport("getOrigin", "admin")]
    public static partial string GetOrigin();
}
