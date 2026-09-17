using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Peggy.Core;

namespace Peggy.Core.Tests;

internal static class MonitoringScopeSmokeTests
{
    private const string Account = "monitor@example.com";
    private static readonly string[] EnvironmentNames =
    [
        "PEGGY_TEAMS_MODE", "PEGGY_TEAMS_SCOPE", "PEGGY_TEAMS_MONITOR_CHATS",
        "PEGGY_TEAMS_WATCHLIST", "PEGGY_TEAMS_TEAM_WATCHLIST",
        "PEGGY_OUTLOOK_SCOPE", "PEGGY_OUTLOOK_FOLDERS",
        "PEGGY_INCLUDE_ACTIVE_CASES_FOLDER", "PEGGY_FAKE_WORKIQ_SCENARIO",
        "PEGGY_FAKE_WORKIQ_LOG", "PEGGY_FAKE_SCOPE_NEW_FOLDER"
    ];

    public static async Task RunAsync(string directory)
    {
        var previous = EnvironmentNames.ToDictionary(
            name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in EnvironmentNames)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
            await TestDefaultsAndMigrationAsync(directory);
            TestEnvironmentCompatibility();
            foreach (var name in EnvironmentNames)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SCENARIO", "monitoring-modes");
            var logPath = Path.Combine(directory, "monitoring-modes.log");
            Environment.SetEnvironmentVariable("PEGGY_FAKE_WORKIQ_LOG", logPath);
            await TestOutlookModesAsync(logPath);
            await TestTeamsModesAsync(logPath);
            await TestMockModesAsync();
        }
        finally
        {
            foreach (var pair in previous)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }

    private static async Task TestDefaultsAndMigrationAsync(string directory)
    {
        var fresh = await new EncryptedStateStore(
            Path.Combine(directory, "new-monitoring-modes.dat")).LoadAsync();
        TestAssert.True(
            fresh.Settings.TeamsScope == TeamsMonitoringScope.ChatsOnly &&
            fresh.Settings.OutlookScope == OutlookMonitoringScope.InboxOnly &&
            !fresh.Settings.IncludeActiveCasesFolder &&
            fresh.Settings.TeamsChannelWatchlist.Count == 0,
            "Fresh monitoring defaults were not chats and root Inbox only.");

        var cases = new[]
        {
            ("""{}""", TeamsMonitoringScope.AllActivity,
                OutlookMonitoringScope.InboxAndSelectedFolders),
            ("""{"includeActiveCasesFolder":false}""",
                TeamsMonitoringScope.AllActivity, OutlookMonitoringScope.InboxOnly),
            ("""{"includeActiveCasesFolder":false,"additionalOutlookFolders":["Inbox/Cases"]}""",
                TeamsMonitoringScope.AllActivity, OutlookMonitoringScope.InboxAndSelectedFolders),
            ("""{"additionalOutlookFolders":["\\Inbox\\"]}""",
                TeamsMonitoringScope.AllActivity, OutlookMonitoringScope.InboxAndAllSubfolders),
            ("""{"teamsScope":1,"monitorTeamsChatsInSelectedScope":true}""",
                TeamsMonitoringScope.ChatsAndSelectedChannels,
                OutlookMonitoringScope.InboxAndSelectedFolders),
            ("""{"teamsScope":1,"monitorTeamsChatsInSelectedScope":false}""",
                TeamsMonitoringScope.SelectedTeamsAndChannels,
                OutlookMonitoringScope.InboxAndSelectedFolders),
            ("""{"teamsScope":2,"monitorTeamsChatsInSelectedScope":true,"outlookScope":4}""",
                TeamsMonitoringScope.Off, OutlookMonitoringScope.Off),
            ("""{"teamsScope":3,"monitorTeamsChatsInSelectedScope":true,"outlookScope":0,"includeActiveCasesFolder":true,"additionalOutlookFolders":["Inbox"]}""",
                TeamsMonitoringScope.ChatsOnly, OutlookMonitoringScope.InboxOnly),
            ("""{"monitoringScopeVersion":1,"teamsScope":1,"monitorTeamsChatsInSelectedScope":true,"outlookScope":2,"includeActiveCasesFolder":false}""",
                TeamsMonitoringScope.SelectedTeamsAndChannels,
                OutlookMonitoringScope.SelectedFoldersOnly)
        };
        foreach (var (json, teams, outlook) in cases)
        {
            var state = EncryptedStateStore.DeserializeState(
                Encoding.UTF8.GetBytes($"{{\"version\":1,\"settings\":{json}}}"),
                DateTimeOffset.UtcNow);
            TestAssert.True(
                state.Settings.TeamsScope == teams &&
                state.Settings.OutlookScope == outlook &&
                state.Settings.MonitoringScopeVersion == 1,
                $"Scope migration did not preserve legacy intent: {json}");
        }
        var retained = EncryptedStateStore.DeserializeState(
            Encoding.UTF8.GetBytes("""
                {"settings":{"teamsScope":3,"outlookScope":4,
                 "teamsChannelWatchlist":[{"teamId":"team","channelId":"channel"}],
                 "additionalOutlookFolders":["Inbox/Cases"]}}
                """),
            DateTimeOffset.UtcNow);
        TestAssert.True(
            retained.Settings.TeamsChannelWatchlist.Count == 1 &&
            retained.Settings.AdditionalOutlookFolders.Count == 1 &&
            !retained.Settings.IncludeActiveCasesFolder,
            "Explicit modes discarded selections or injected the legacy Active folder.");
        var store = new EncryptedStateStore(
            Path.Combine(directory, "persisted-monitoring-modes.dat"));
        await store.SaveAsync(fresh);
        var reloaded = await store.LoadAsync();
        TestAssert.True(
            reloaded.Settings.TeamsScope == TeamsMonitoringScope.ChatsOnly &&
            reloaded.Settings.OutlookScope == OutlookMonitoringScope.InboxOnly,
            "Fresh defaults were mistaken for missing legacy settings after save.");
    }

    private static void TestEnvironmentCompatibility()
    {
        PeggySettings Read()
            => (PeggySettings)typeof(WorkIqMessageProvider).GetMethod(
                "ReadSettingsFromEnvironment",
                BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;

        var defaults = Read();
        TestAssert.True(
            defaults.TeamsScope == TeamsMonitoringScope.ChatsOnly &&
            defaults.OutlookScope == OutlookMonitoringScope.InboxOnly,
            "Empty environment did not use fresh defaults.");
        Environment.SetEnvironmentVariable("PEGGY_TEAMS_SCOPE", "1");
        Environment.SetEnvironmentVariable("PEGGY_TEAMS_MONITOR_CHATS", "true");
        Environment.SetEnvironmentVariable("PEGGY_OUTLOOK_FOLDERS", "Inbox/Cases");
        var legacy = Read();
        TestAssert.True(
            legacy.TeamsScope == TeamsMonitoringScope.ChatsAndSelectedChannels &&
            legacy.OutlookScope == OutlookMonitoringScope.InboxAndSelectedFolders,
            "Legacy scope/bool or folder environment lost its monitoring intent.");
        Environment.SetEnvironmentVariable("PEGGY_TEAMS_MONITOR_CHATS", "false");
        TestAssert.Equal(
            TeamsMonitoringScope.SelectedTeamsAndChannels, Read().TeamsScope,
            "Legacy selected-only environment enabled chats.");
        Environment.SetEnvironmentVariable("PEGGY_OUTLOOK_FOLDERS", "Inbox");
        TestAssert.Equal(
            OutlookMonitoringScope.InboxAndAllSubfolders, Read().OutlookScope,
            "Legacy root Inbox selection did not migrate to recursive monitoring.");
        Environment.SetEnvironmentVariable("PEGGY_OUTLOOK_FOLDERS", null);
        Environment.SetEnvironmentVariable("PEGGY_INCLUDE_ACTIVE_CASES_FOLDER", "true");
        TestAssert.Equal(
            OutlookMonitoringScope.InboxAndSelectedFolders, Read().OutlookScope,
            "Legacy Active-folder environment did not retain its selection.");

        Environment.SetEnvironmentVariable("PEGGY_TEAMS_MONITOR_CHATS", "true");
        Environment.SetEnvironmentVariable("PEGGY_OUTLOOK_FOLDERS", "Inbox");
        foreach (var mode in Enum.GetValues<TeamsMonitoringScope>())
        {
            Environment.SetEnvironmentVariable("PEGGY_TEAMS_MODE", mode.ToString());
            TestAssert.Equal(mode, Read().TeamsScope,
                "Explicit Teams mode was overridden by legacy scope/bool.");
        }
        foreach (var mode in Enum.GetValues<OutlookMonitoringScope>())
        {
            Environment.SetEnvironmentVariable("PEGGY_OUTLOOK_SCOPE", mode.ToString());
            TestAssert.Equal(mode, Read().OutlookScope,
                "Explicit Outlook mode was overridden by retained folders.");
        }
        Environment.SetEnvironmentVariable("PEGGY_TEAMS_MODE", null);
        Environment.SetEnvironmentVariable("PEGGY_TEAMS_SCOPE", "AllActivity");
        TestAssert.Equal(TeamsMonitoringScope.AllActivity, Read().TeamsScope,
            "Legacy AllActivity environment changed scope.");
        Environment.SetEnvironmentVariable("PEGGY_TEAMS_SCOPE", null);
        TestAssert.Equal(TeamsMonitoringScope.AllActivity, Read().TeamsScope,
            "Explicit legacy chat flag without scope lost its old default.");
    }

    private static WorkIqMessageProvider Provider(PeggySettings settings)
        => new(
            executablePath: Environment.ProcessPath,
            account: Account,
            principalId: "user-me",
            settings: settings);

    private static async Task TestOutlookModesAsync(string logPath)
    {
        foreach (var mode in Enum.GetValues<OutlookMonitoringScope>())
        {
            var provider = Provider(new PeggySettings
            {
                OutlookScope = mode,
                TeamsScope = TeamsMonitoringScope.Off,
                IncludeActiveCasesFolder = true,
                AdditionalOutlookFolders = ["Inbox", "Inbox/Cases"]
            });
            string[] expected = mode switch
            {
                OutlookMonitoringScope.InboxOnly => ["inbox"],
                OutlookMonitoringScope.InboxAndSelectedFolders =>
                    ["inbox", "cases", "active", "active-child"],
                OutlookMonitoringScope.SelectedFoldersOnly =>
                    ["cases", "active", "active-child"],
                OutlookMonitoringScope.InboxAndAllSubfolders =>
                    ["inbox", "cases", "active", "active-child", "archive"],
                _ => []
            };
            foreach (var unread in new[] { true, false })
            {
                File.WriteAllText(logPath, string.Empty);
                var messages = unread
                    ? await provider.ListUnreadAsync(MessageSource.Email, 100)
                    : (await provider.GetChangesAsync(
                        new MonitoringCursorState(), 100, DateTimeOffset.UtcNow)).Messages;
                TestAssert.True(
                    messages.Select(message => message.Subject)
                        .ToHashSet(StringComparer.Ordinal).SetEquals(expected),
                    $"{mode} {(unread ? "unread" : "monitoring")} read the wrong folders.");
                var requests = File.ReadAllLines(logPath);
                TestAssert.True(
                    requests.All(request =>
                        !request.Contains("/me/messages", StringComparison.Ordinal) &&
                        !request.Contains("/me/mailFolders?", StringComparison.Ordinal)),
                    "Monitoring broadened beyond the Inbox hierarchy.");
                if (mode == OutlookMonitoringScope.Off)
                {
                    TestAssert.Equal(0, requests.Length,
                        "Both services Off still performed identity or mailbox reads.");
                }
                if (mode == OutlookMonitoringScope.InboxOnly)
                {
                    TestAssert.True(requests.All(request =>
                        !request.Contains("childFolders", StringComparison.Ordinal)),
                        "Inbox-only enumerated retained subfolder selections.");
                }
                if (mode == OutlookMonitoringScope.SelectedFoldersOnly)
                {
                    TestAssert.True(requests.All(request =>
                        !request.Contains("/inbox/messages", StringComparison.Ordinal)),
                        "Selected-only fetched root Inbox messages.");
                }
            }
            if (mode == OutlookMonitoringScope.Off)
            {
                var folders = await provider.ListOutlookChildFoldersAsync(null);
                TestAssert.True(folders.Count > 0,
                    "Outlook Off disabled the independent folder browser.");
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await TestAssert.ThrowsAsync<OperationCanceledException>(
                    () => provider.ListUnreadAsync(MessageSource.Email, 10, cancelled.Token),
                    "Off ignored cancellation.");
            }
        }

        var recursive = Provider(new PeggySettings
        {
            OutlookScope = OutlookMonitoringScope.InboxAndAllSubfolders,
            TeamsScope = TeamsMonitoringScope.Off
        });
        _ = await recursive.ListUnreadAsync(MessageSource.Email, 100);
        Environment.SetEnvironmentVariable("PEGGY_FAKE_SCOPE_NEW_FOLDER", "true");
        typeof(WorkIqMessageProvider).GetField(
            "_monitoredMailFoldersLoadedUtc",
            BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(
                recursive, DateTimeOffset.UtcNow.AddHours(-1));
        var refreshed = await recursive.ListUnreadAsync(MessageSource.Email, 100);
        TestAssert.True(refreshed.Any(message => message.Subject == "new-folder"),
            "Recursive Inbox monitoring did not discover a newly-created descendant.");
        Environment.SetEnvironmentVariable("PEGGY_FAKE_SCOPE_NEW_FOLDER", null);
    }

    private static async Task TestTeamsModesAsync(string logPath)
    {
        foreach (var mode in Enum.GetValues<TeamsMonitoringScope>())
        {
            var provider = Provider(new PeggySettings
            {
                OutlookScope = OutlookMonitoringScope.Off,
                TeamsScope = mode,
                MonitorTeamsChatsInSelectedScope = true,
                TeamsTeamWatchlist =
                [
                    new() { TeamId = "team-1", ExcludedChannelIds = ["channel-2"] }
                ],
                TeamsChannelWatchlist =
                [
                    new() { TeamId = "team-2", ChannelId = "selected-channel" }
                ]
            });
            File.WriteAllText(logPath, string.Empty);
            var batch = await provider.GetChangesAsync(
                new MonitoringCursorState
                {
                    ChannelScanResumeKey = "stale-team\nstale-channel",
                    ChannelContinuationPass = true
                },
                100, DateTimeOffset.UtcNow);
            var requests = File.ReadAllLines(logPath);
            var channelsExpected = MonitoringScopes.IncludesSelectedChannels(mode);
            var directExpected = mode is TeamsMonitoringScope.ChatsOnly or
                TeamsMonitoringScope.ChatsAndSelectedChannels;
            TestAssert.True(
                requests.Any(request => request.Contains(
                    "/me/chats?", StringComparison.Ordinal)) == directExpected &&
                requests.Any(request => request.Contains(
                    "/search/query", StringComparison.Ordinal)) ==
                    (mode == TeamsMonitoringScope.AllActivity) &&
                requests.Any(request => request.Contains(
                    "/teams/", StringComparison.Ordinal)) == channelsExpected &&
                requests.All(request =>
                    !request.Contains("/meeting/messages", StringComparison.Ordinal) &&
                    !request.Contains("/channels/channel-2/messages", StringComparison.Ordinal)),
                $"{mode} monitoring requested an excluded Teams source.");
            TestAssert.Equal(directExpected ? 2 : 0,
                batch.Messages.Count(message => message.ChannelId is null),
                $"{mode} did not retrieve both one-on-one and group chats.");
            TestAssert.Equal(channelsExpected ? 3 : 0,
                batch.Messages.Count(message => message.ChannelId is not null),
                $"{mode} lost selected channels/whole-team exclusions.");
            File.WriteAllText(logPath, string.Empty);
            var unread = await provider.ListUnreadAsync(MessageSource.Teams, 100);
            var unreadRequests = File.ReadAllLines(logPath);
            TestAssert.True(
                unread.All(message => message.ChannelId is null) &&
                unreadRequests.All(request =>
                    !request.Contains("/teams/", StringComparison.Ordinal) &&
                    !request.Contains("/search/query", StringComparison.Ordinal)),
                $"{mode} unread retrieval mislabeled channel activity as unread.");
            if (mode == TeamsMonitoringScope.Off)
            {
                TestAssert.True(
                    unread.Count == 0 && unreadRequests.Length == 0,
                    "Teams Off still fetched unread metadata/messages.");
            }
            else if (mode != TeamsMonitoringScope.AllActivity)
            {
                TestAssert.True(
                    unreadRequests.Any(request => request.Contains(
                        "/me/chats?", StringComparison.Ordinal)) == directExpected &&
                    unreadRequests.All(request =>
                        !request.Contains("/meeting/messages", StringComparison.Ordinal)),
                    $"{mode} unread retrieval ignored monitoring scope.");
                TestAssert.Equal(directExpected ? 2 : 0, unread.Count,
                    $"{mode} did not return only its supported unread chats.");
            }
        }
    }

    private static async Task TestMockModesAsync()
    {
        var provider = new MockMessageProvider(new PeggySettings
        {
            TeamsScope = TeamsMonitoringScope.Off,
            OutlookScope = OutlookMonitoringScope.Off
        });
        TestAssert.Equal(0, (await provider.ListUnreadAsync(null, 100)).Count,
            "Mock unread ignored Off modes.");
        TestAssert.Equal(0, (await provider.GetChangesAsync(
            new MonitoringCursorState(), 100, DateTimeOffset.UtcNow)).Messages.Count,
            "Mock monitoring ignored Off modes.");
        var selected = new MockMessageProvider(new PeggySettings
        {
            TeamsScope = TeamsMonitoringScope.SelectedTeamsAndChannels,
            OutlookScope = OutlookMonitoringScope.SelectedFoldersOnly
        });
        TestAssert.Equal(0, (await selected.ListUnreadAsync(null, 100)).Count,
            "Mock selected-only modes leaked unselected root mail or chats.");
    }

    public static void HandleFakeRequest(string command, string url)
    {
        var logPath = Environment.GetEnvironmentVariable("PEGGY_FAKE_WORKIQ_LOG")!;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.AppendAllText(logPath, $"{command} {url}{Environment.NewLine}");
                break;
            }
            catch (IOException) when (attempt < 100)
            {
                Thread.Sleep(10);
            }
        }
        var path = url.Split('?')[0];
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var hasNewFolder = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_SCOPE_NEW_FOLDER") == "true";
        object Folder(string id, string name, string? parent, int children)
            => new { id, displayName = name, parentFolderId = parent, childFolderCount = children };
        object ChatMessage(string id) => new
        {
            id, createdDateTime = now, lastModifiedDateTime = now,
            messageType = "message",
            from = new { user = new { id = "other-user", displayName = "Other user" } },
            body = new { contentType = "text", content = $"Message {id}" },
            webUrl = $"https://teams.microsoft.com/l/message/direct/{id}"
        };
        object response;
        if (path == "/me")
        {
            response = new { id = "user-me", mail = Account, userPrincipalName = Account };
        }
        else if (path == "/me/mailFolders/inbox")
        {
            response = Folder("inbox", "Inbox", null, hasNewFolder ? 3 : 2);
        }
        else if (path == "/me/mailFolders/inbox/childFolders")
        {
            response = url.Contains("page=2", StringComparison.Ordinal)
                ? new { value = hasNewFolder
                    ? new[] { Folder("archive", "Archive", "inbox", 0),
                              Folder("new-folder", "New folder", "inbox", 0) }
                    : new[] { Folder("archive", "Archive", "inbox", 0) } }
                : new Dictionary<string, object>
                {
                    ["value"] = new[] { Folder("cases", "Cases", "inbox", 1) },
                    ["@odata.nextLink"] =
                        "https://graph.microsoft.com/v1.0/me/mailFolders/inbox/childFolders?page=2"
                };
        }
        else if (path == "/me/mailFolders/cases/childFolders")
        {
            response = new { value = new[] { Folder("active", "Active", "cases", 1) } };
        }
        else if (path == "/me/mailFolders/active/childFolders")
        {
            response = new { value = new[] { Folder("active-child", "Child", "active", 0) } };
        }
        else if (parts is ["me", "mailFolders", var folderId, "messages"])
        {
            response = new { value = new[]
            {
                new
                {
                    id = $"mail-{folderId}", conversationId = $"conversation-{folderId}",
                    from = new { emailAddress = new { name = "Other", address = "other@example.com" } },
                    subject = folderId, bodyPreview = "Unread message",
                    receivedDateTime = now, isRead = false
                }
            } };
        }
        else if (path == "/me/chats")
        {
            response = new { value = new[]
            {
                ("direct", "oneOnOne"), ("group", "group"), ("meeting", "meeting")
            }.Select(chat => new
            {
                id = chat.Item1, topic = chat.Item1, chatType = chat.Item2,
                viewpoint = new { lastMessageReadDateTime = now.AddHours(-1) },
                lastMessagePreview = ChatMessage($"message-{chat.Item1}")
            }).ToArray() };
        }
        else if (parts is ["me", "chats", var chatId, "messages"])
        {
            response = new { value = new[] { ChatMessage($"message-{chatId}") } };
        }
        else if (path == "/search/query")
        {
            response = new { value = new[]
            {
                new { hitsContainers = new[]
                {
                    new { total = 0, moreResultsAvailable = false, hits = Array.Empty<object>() }
                } }
            } };
        }
        else if (path == "/teams/team-1/channels")
        {
            response = new { value = Enumerable.Range(1, 3).Select(index => new
            {
                id = $"channel-{index}", displayName = $"Channel {index}"
            }).ToArray() };
        }
        else if (parts is ["teams", _, "channels", var channelId, "messages", "delta()"])
        {
            response = new Dictionary<string, object>
            {
                ["value"] = url.Contains("$deltatoken=", StringComparison.Ordinal)
                    ? Array.Empty<object>()
                    : new[] { ChatMessage($"channel-message-{channelId}") },
                ["@odata.deltaLink"] =
                    $"https://graph.microsoft.com/v1.0{path}?$deltatoken=complete"
            };
        }
        else
        {
            throw new InvalidOperationException($"Unexpected monitoring-mode request: {command} {url}");
        }
        Console.WriteLine(JsonSerializer.Serialize(response));
    }
}
