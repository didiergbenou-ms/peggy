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
             IsSpecificTeamsMessageUri(candidate)) &&
            (!IsOutlookHost(candidate.Host) ||
             IsSpecificOutlookMessageUri(candidate)))
        {
            uri = NormalizeOutlookUri(candidate);
            return true;
        }

        uri = null!;
        return false;
    }

    private static Uri NormalizeOutlookUri(Uri uri)
    {
        if (!string.Equals(
                uri.Host,
                "mail.microsoft.com",
                StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.EndsWith(
                ".mail.microsoft.com",
                StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        return new UriBuilder(uri)
        {
            Host = "outlook.office.com",
            Port = -1
        }.Uri;
    }

    public static Uri ScopeOutlookMailbox(Uri uri, string? mailbox)
    {
        uri = NormalizeOutlookUri(uri);
        if (!IsWorkOutlookHost(uri.Host) ||
            string.IsNullOrWhiteSpace(mailbox))
        {
            return uri;
        }

        var itemId = GetOutlookItemId(uri);
        if (!IsSafeOpaqueItemId(itemId))
        {
            return uri;
        }

        var escapedMailbox = Uri.EscapeDataString(mailbox.Trim());
        var escapedItemId = Uri.EscapeDataString(itemId!);
        return new Uri(
            $"https://outlook.office.com/mail/{escapedMailbox}/" +
            $"deeplink/read/{escapedItemId}",
            UriKind.Absolute);
    }

    private static string? GetOutlookItemId(Uri uri)
    {
        var query = uri.Query.TrimStart('?');
        foreach (var part in query.Split(
                     '&',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 ||
                !string.Equals(
                    Uri.UnescapeDataString(part[..separator]),
                    "ItemID",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                return Uri.UnescapeDataString(part[(separator + 1)..]);
            }
            catch (UriFormatException)
            {
                return null;
            }
        }

        const string marker = "/deeplink/read/";
        var markerIndex = uri.AbsolutePath.IndexOf(
            marker,
            StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var encodedId = uri.AbsolutePath[(markerIndex + marker.Length)..]
            .TrimEnd('/');
        try
        {
            return Uri.UnescapeDataString(encodedId);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static bool IsSafeOpaqueItemId(string? itemId)
        => !string.IsNullOrWhiteSpace(itemId) &&
           itemId.Length <= 4096 &&
           itemId.All(character =>
               char.IsAsciiLetterOrDigit(character) ||
               character is '+' or '/' or '=' or '-' or '_');

    private static bool IsOutlookHost(string host)
        => !IsTeamsHost(host);

    private static bool IsSpecificOutlookMessageUri(Uri uri)
        => IsSafeOpaqueItemId(GetOutlookItemId(uri));

    private static bool IsWorkOutlookHost(string host)
        => string.Equals(
               host,
               "outlook.office.com",
               StringComparison.OrdinalIgnoreCase) ||
           string.Equals(
               host,
               "outlook.office365.com",
               StringComparison.OrdinalIgnoreCase) ||
           string.Equals(
               host,
               "mail.microsoft.com",
               StringComparison.OrdinalIgnoreCase) ||
           host.EndsWith(
               ".mail.microsoft.com",
               StringComparison.OrdinalIgnoreCase);

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
