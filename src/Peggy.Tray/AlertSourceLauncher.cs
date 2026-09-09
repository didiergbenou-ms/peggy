using System;
using System.Diagnostics;

namespace Peggy.Tray;

internal static class AlertSourceLauncher
{
    public static Uri CreateOpenUri(Uri sourceUri, string? mailbox)
    {
        ArgumentNullException.ThrowIfNull(sourceUri);
        return AlertSourceLink.ScopeOutlookMailbox(sourceUri, mailbox);
    }

    public static void Open(Uri sourceUri, string? mailbox)
    {
        var openUri = CreateOpenUri(sourceUri, mailbox);
        Process.Start(new ProcessStartInfo(openUri.AbsoluteUri)
        {
            UseShellExecute = true
        });
    }
}
