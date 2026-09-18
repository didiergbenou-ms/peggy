using System.Reflection;

namespace Peggy.Tray;

internal static class PeggyReleaseVersion
{
    public static string Display { get; } = Format(
        typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion);

    internal static string Format(string? informationalVersion)
    {
        var release = informationalVersion?.Split('+', 2)[0].Trim();
        return string.IsNullOrEmpty(release)
            ? "Version unavailable"
            : $"v{release}";
    }
}
