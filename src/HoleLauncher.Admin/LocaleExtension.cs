using System.Globalization;
using Avalonia.Markup.Xaml;

namespace HoleLauncher.Admin;

public static class Locale
{
    public static string T(string key, params object[] arguments)
    {
        var text = Program.Translate(key);
        return arguments.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, arguments);
    }

    public static string ServiceName(string name) => T(name switch
    {
        "Public API" => "server.public_api",
        "Access Service" => "server.access",
        "Accounts PostgreSQL" => "server.postgres",
        "Init" => "server.init",
        "Kafka" => "server.kafka",
        "Orchestrator" => "server.orchestrator",
        "Drasl" => "server.drasl",
        "Storage" => "nav.storage",
        "Repository" => "nav.repository",
        "Orchestrator PostgreSQL" => "server.orchestrator_postgres",
        "Game Profiles PostgreSQL" => "server.game_postgres",
        _ => "status.unknown"
    });

    public static string ServiceState(string state) => T(state switch
    {
        "Online" => "status.online",
        "Offline" => "status.offline",
        "Schema ready" => "status.ready",
        "Not configured" => "status.not_configured",
        _ => "status.unknown"
    });

    public static string StorageSize(long bytes)
    {
        const long gibibyte = 1024L * 1024 * 1024;
        const long tebibyte = 1024L * gibibyte;
        return bytes switch
        {
            >= tebibyte => T("server.storage_tib", (double)bytes / tebibyte),
            >= gibibyte => T("server.storage_gib", (double)bytes / gibibyte),
            >= 1024L * 1024 => T("repository.upload_mib", bytes / (1024d * 1024)),
            >= 1024 => T("repository.upload_kib", bytes / 1024d),
            _ => T("server.storage_bytes", bytes)
        };
    }

    public static string OperationError(string? key, string fallback) =>
        key is not null && key.StartsWith("error.", StringComparison.Ordinal) && Program.Translate(key) != key
            ? T(key) : fallback;
}

public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => Locale.T(key);
}
