using System.Runtime.Versioning;

namespace Peggy.UninstallCleanup;

internal static class Program
{
    private const string AccountVariable = "PEGGY_WORKIQ_ACCOUNT";

    [SupportedOSPlatform("windows")]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() ||
            args is not ["--msi"])
        {
            Console.Error.WriteLine(
                "Peggy uninstall cleanup must be run by the Windows installer.");
            return 2;
        }

        try
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                throw new InvalidOperationException(
                    "The current user's LocalAppData directory is unavailable.");
            }

            UserDataCleanup.Run(
                Path.Combine(localAppData, "Peggy"),
                AccountVariable,
                EnvironmentVariableTarget.User);
            return 0;
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            IOException or
            InvalidOperationException or
            PlatformNotSupportedException or
            System.Security.SecurityException or
            UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"Peggy user data could not be removed: {ex.Message}");
            return 1;
        }
    }
}

internal static class UserDataCleanup
{
    internal static void Run(
        string dataDirectory,
        string accountVariable,
        EnvironmentVariableTarget target)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException(
                "The Peggy data directory is required.",
                nameof(dataDirectory));
        }
        if (string.IsNullOrWhiteSpace(accountVariable))
        {
            throw new ArgumentException(
                "The Peggy account variable is required.",
                nameof(accountVariable));
        }

        var fullPath = Path.GetFullPath(dataDirectory)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        if (!string.Equals(
                Path.GetFileName(fullPath),
                "Peggy",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The cleanup target is not Peggy's data directory.");
        }

        if (Directory.Exists(fullPath))
        {
            DeleteDirectory(new DirectoryInfo(fullPath));
        }

        Environment.SetEnvironmentVariable(
            accountVariable,
            null,
            target);
    }

    private static void DeleteDirectory(DirectoryInfo directory)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            directory.Delete();
            return;
        }

        foreach (var file in directory.EnumerateFiles())
        {
            if (file.IsReadOnly)
            {
                file.IsReadOnly = false;
            }
            file.Delete();
        }
        foreach (var child in directory.EnumerateDirectories())
        {
            DeleteDirectory(child);
        }

        if ((directory.Attributes & FileAttributes.ReadOnly) != 0)
        {
            directory.Attributes &= ~FileAttributes.ReadOnly;
        }
        directory.Delete();
    }
}
