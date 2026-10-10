using System.Reflection;

namespace Hrs.Fiscal.Server;

/// <summary>The running release: Version from Directory.Build.props, plus the git commit it was built from.</summary>
public static class AppVersion
{
    /// <summary>Product name as registered with ATK (SEF certification).</summary>
    public const string ProductName = "Opera Cloud Fiscal Solution - Kosovo";

    /// <summary>Developer and maintainer registered with ATK.</summary>
    public const string Developer = "Behar Lushaku";

    private static readonly string Informational =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>e.g. "1.0.0".</summary>
    public static string Version { get; } = Informational.Split('+')[0];

    /// <summary>e.g. "1a2b3c4d", or "" when built outside git.</summary>
    public static string Commit { get; } = Informational.Contains('+') ? Informational.Split('+')[1] : "";

    /// <summary>e.g. "1.0.0 (1a2b3c4d)".</summary>
    public static string Display { get; } = Commit.Length > 0 ? $"{Version} ({Commit})" : Version;
}
