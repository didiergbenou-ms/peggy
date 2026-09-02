using System;
using System.Collections.Generic;
using System.Linq;
using Peggy.Core;

namespace Peggy.Tray;

internal static class AlertSourceLink
{
    private static readonly string[] SupportedHosts =
    [
        "outlook.office.com",
        "outlook.office365.com",
        "outlook.live.com",
        "mail.microsoft.com",
        "teams.microsoft.com",
        "teams.cloud.microsoft",
        "teams.live.com"
    ];

    public static string SelectReference(
        string? proposedReference,
        IEnumerable<Message> messages)
    {
        var ordered = messages
            .OrderByDescending(message => message.ReceivedAt)
            .ToList();
        var messageUri = GetMessageUri(ordered);
        if (messageUri is not null)
        {
            return messageUri.AbsoluteUri;
        }

        if (TryCreate(proposedReference, out var proposedUri))
        {
            return proposedUri.AbsoluteUri;
        }

        if (!string.IsNullOrWhiteSpace(proposedReference))
        {
            return proposedReference.Trim();
        }

        return ordered
            .Select(message => message.Id)
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))
            ?? string.Empty;
    }

    public static Uri? GetMessageUri(IEnumerable<Message> messages)
    {
        foreach (var message in messages.OrderByDescending(
                     item => item.ReceivedAt))
        {
            if (TryCreate(message.WebUrl, out var uri))
            {
                return uri;
            }
        }

        return null;
    }

    public static bool TryCreate(string? value, out Uri uri)
    {
        if (Uri.TryCreate(
                value?.Trim(),
                UriKind.Absolute,
                out var candidate) &&
            string.Equals(
                candidate.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) &&
            SupportedHosts.Any(host =>
                string.Equals(
                    candidate.Host,
                    host,
                    StringComparison.OrdinalIgnoreCase) ||
                candidate.Host.EndsWith(
                    "." + host,
                    StringComparison.OrdinalIgnoreCase)) &&
            (!IsTeamsHost(candidate.Host) ||
             IsSpecificTeamsMessageUri(candidate)))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    private static bool IsTeamsHost(string host)
        => host.EndsWith(
               "teams.microsoft.com",
               StringComparison.OrdinalIgnoreCase) ||
           host.EndsWith(
               "teams.cloud.microsoft",
               StringComparison.OrdinalIgnoreCase) ||
           host.EndsWith(
               "teams.live.com",
               StringComparison.OrdinalIgnoreCase);

    private static bool IsSpecificTeamsMessageUri(Uri uri)
    {
        var path = uri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        return path.Length >= 4 &&
               string.Equals(
                   path[0],
                   "l",
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   path[1],
                   "message",
                   StringComparison.OrdinalIgnoreCase);
    }
}
