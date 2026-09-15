namespace Peggy.Core;

public static class OutlookFolderPath
{
    public const string Inbox = "Inbox";

    public static string Normalize(string path)
    {
        var segments = Parse(path);
        return string.Equals(
                segments[0],
                Inbox,
                StringComparison.OrdinalIgnoreCase)
            ? string.Join(
                "/",
                new[] { Inbox }.Concat(segments.Skip(1)))
            : string.Join("/", segments.Prepend(Inbox));
    }

    public static IReadOnlyList<string> Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "An Outlook folder path is required.",
                nameof(path));
        }

        var segments = path
            .Replace('\\', '/')
            .Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
        if (segments.Length == 0 ||
            segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException(
                $"Invalid Outlook folder path '{path}'.",
                nameof(path));
        }
        return segments;
    }

    public static bool IsSameOrDescendant(string path, string ancestor)
        => string.Equals(
               path,
               ancestor,
               StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith(
               ancestor.TrimEnd('/') + "/",
               StringComparison.OrdinalIgnoreCase);

    public static bool CanRepresentSegment(string segment)
        => !string.IsNullOrWhiteSpace(segment) &&
           segment is not "." and not ".." &&
           !segment.Contains('/') &&
           !segment.Contains('\\');

    public static string Combine(string parentPath, string childName)
        => $"{Normalize(parentPath)}/{childName.Trim()}";
}
