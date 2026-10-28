using System.Reflection;

namespace TgCheck.Core.Config;

/// <summary>Сведения, зашитые в exe при сборке (см. Directory.Build.props).</summary>
public static class BuildInfo
{
    private static readonly Assembly Entry = Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly;

    /// <summary>Версия приложения (из &lt;Version&gt; при сборке).</summary>
    public static Version Version { get; } = Entry.GetName().Version ?? new Version(1, 0, 0);

    public static string VersionText => $"{Version.Major}.{Version.Minor}.{Math.Max(Version.Build, 0)}";

    /// <summary>GitHub-репозиторий для обновлений (owner/repo), пусто — обновления выключены.</summary>
    public static string? UpdateRepo { get; } = Entry.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateRepo")?.Value is { Length: > 0 } r ? r : null;
}
