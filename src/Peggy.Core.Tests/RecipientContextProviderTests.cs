using System.IO;
using System.Reflection;
using System.Text.Json;
using Peggy.Core;

namespace Peggy.Core.Tests;

internal static class RecipientContextProviderTests
{
    private const string Account = "recipient@example.invalid";
    private const string Alias = "recipient-alias@example.invalid";
    private const string CurrentId = "11111111-1111-1111-1111-111111111111";
    private const string OtherId = "22222222-2222-2222-2222-222222222222";
    private const string TeamId = "33333333-3333-3333-3333-333333333333";
    private const string ChannelId = "19:recipient-channel@thread.tacv2";
    private const string CurrentName = "Morgan Sample";
    private static readonly DateTimeOffset Anchor = DateTimeOffset.Parse("2026-09-28T09:00:00Z");
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    public static async Task RunAsync(string directory)
    {
        var names = new[] { "PEGGY_FAKE_WORKIQ_SCENARIO", "PEGGY_FAKE_WORKIQ_LOG", "PEGGY_FAKE_RECIPIENT_PROFILE_NAME" };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var logPath = Path.Combine(directory, "recipient-context.log");
        try
        {
            Directory.CreateDirectory(directory);
            Environment.SetEnvironmentVariable("PEGGY_FAKE_WORKIQ_SCENARIO", "recipient-context");
            Environment.SetEnvironmentVariable("PEGGY_FAKE_WORKIQ_LOG", logPath);
            Environment.SetEnvironmentVariable("PEGGY_FAKE_RECIPIENT_PROFILE_NAME", null);
            var provider = Provider(TeamsMonitoringScope.ChatsOnly);
            var changes = await provider.GetChangesAsync(new MonitoringCursorState(), 100, Anchor.AddMinutes(5));
            AssertChatContexts(changes.Messages);
            TestAssert.True(changes.Messages.All(message =>
                    message.RecipientContext?.ConversationType != "meeting" && !message.IsFromMe),
                "Recipient metadata changed ChatsOnly scope or own-message filtering.");
            var requests = File.ReadAllLines(logPath);
            TestAssert.True(requests.Count(line => line.StartsWith("fetch /me?", StringComparison.Ordinal)) == 1,
                "Recipient metadata added an authentication/profile read to normal polling.");
            var unread = await provider.ListUnreadAsync(MessageSource.Teams, 100);
            AssertChatContexts(unread);
            TestAssert.True(unread.Single(message => message.Subject == "preview-only").RecipientContext is
                    { ConversationType: "group", MentionsComplete: false },
                "The unread preview fallback incorrectly claimed complete mentions.");
            var thread = await provider.GetThreadAsync(OpaqueIdentifier.Encode("teams-thread:", "group"));
            AssertChatContexts(thread!.Messages, includeDirect: false);
            TestSearchConversions(provider);
            TestUnverifiedIdentity();
            await TestProfileNamesAsync();
            await TestChannelRoutesAsync();
            await TestAcknowledgmentsAsync(provider);
            var allUnread = await Provider(TeamsMonitoringScope.AllActivity)
                .ListUnreadAsync(MessageSource.Teams, 100);
            TestAssert.True(allUnread.Single(message => message.Subject == "meeting").RecipientContext is
                    { ConversationType: "meeting", MentionsComplete: true },
                "The already-monitored meeting chat lost its conversation type.");
            var mock = await new MockMessageProvider().ListUnreadAsync(MessageSource.Teams, 100);
            TestAssert.True(mock.Count > 0 && mock.All(message => message.RecipientContext is not null),
                "Mock Teams messages did not exercise recipient-aware classification.");
            requests = File.ReadAllLines(logPath);
            TestAssert.True(requests.All(line =>
                {
                    var separator = line.IndexOf(' ');
                    return separator > 0 &&
                        IsReadOnlyRequest(line[..separator], line[(separator + 1)..]) &&
                        !line.Contains("/members", StringComparison.Ordinal);
                }) &&
                requests.Where(line => line.StartsWith("fetch /me?", StringComparison.Ordinal))
                    .All(line => line.Contains("displayName", StringComparison.Ordinal)),
                "Recipient context must reuse profile reads without LLM, writes, or chat-member lookups.");
        }
        finally
        {
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            File.Delete(logPath);
        }
    }

    private static WorkIqMessageProvider Provider(TeamsMonitoringScope scope)
        => new(Environment.ProcessPath, Account, new PeggySettings
        {
            OutlookScope = OutlookMonitoringScope.Off,
            TeamsScope = scope,
            TeamsChannelWatchlist =
            [
                new() { TeamId = TeamId, ChannelId = ChannelId, DisplayName = "Sample channel" }
            ]
        }, principalId: CurrentId);

    private static void AssertChatContexts(IReadOnlyList<Message> messages, bool includeDirect = true)
    {
        Message Find(string subject) => messages.Single(message => message.Subject == subject);
        var other = Find("other");
        TestAssert.True(other.RecipientContext is
                { ConversationType: "group", CurrentUserDisplayName: CurrentName, MentionsComplete: true } &&
                other.RecipientContext.Mentions.SequenceEqual([new TeamsMention("Alex Sample", false, "user")]) &&
                !other.IsMention,
            "An ordinary group-chat request to another teammate lost recipient context or was filtered out.");
        TestAssert.True(Find("own").IsMention && Find("own").RecipientContext!.Mentions.Single().IsCurrentUser &&
                        Find("alias").IsMention,
            "Current-user mentions were not matched using authenticated IDs/email aliases.");
        var mixed = Find("mixed");
        TestAssert.True(mixed.IsMention && mixed.RecipientContext!.Mentions.Count == 2 &&
                        mixed.RecipientContext.Mentions.Count(mention => mention.IsCurrentUser) == 1,
            "Mixed-user mentions lost either recipient or the verified current-user flag.");
        TestAssert.True(!Find("same-name").IsMention &&
                        Find("same-name").RecipientContext!.MentionsComplete,
            "A different immutable identity sharing the user's display name was mistaken for the user.");
        foreach (var kind in new[] { "team", "channel", "tag" })
        {
            var message = Find(kind);
            TestAssert.True(message.RecipientContext!.Mentions.Single().Kind == kind &&
                            message.RecipientContext.MentionsComplete && !message.IsMention,
                $"A {kind} mention was mislabeled as an individual user.");
        }
        foreach (var subject in new[]
                 {
                     "missing", "null", "malformed", "malformed-entry", "unknown", "name-only",
                     "opaque-identity", "missing-identity", "truncated", "next-link", "count",
                     "duplicate-index", "too-many", "conflicting-identities", "malformed-name"
                 })
        {
            TestAssert.True(!Find(subject).RecipientContext!.MentionsComplete,
                $"Incomplete or unverified {subject} mention metadata was represented as complete.");
        }
        TestAssert.True(!Find("name-only").IsMention &&
                        !Find("missing-identity").IsMention &&
                        Find("unknown").RecipientContext!.Mentions.Single().Kind == "unknown",
            "Unknown identity metadata was converted into a verified current-user mention.");
        TestAssert.True(Find("empty").RecipientContext is { MentionsComplete: true, Mentions.Count: 0 },
            "An explicitly provided empty mentions array was treated as absent.");
        TestAssert.True(Find("too-many").IsMention,
            "Bounding the classifier payload lost a verified current-user mention later in the metadata.");
        if (includeDirect)
        {
            TestAssert.True(Find("direct").RecipientContext is
                    { ConversationType: "oneOnOne", MentionsComplete: true, Mentions.Count: 0 },
                "A direct request without @mentions lost its one-on-one recipient context.");
        }
        foreach (var message in messages)
        {
            AssertSafe(message.RecipientContext!);
        }
    }

    private static async Task TestChannelRoutesAsync()
    {
        var provider = Provider(TeamsMonitoringScope.SelectedTeamsAndChannels);
        var changes = await provider.GetChangesAsync(new MonitoringCursorState(), 100, Anchor.AddMinutes(5));
        TestAssert.Equal(2, changes.Messages.Count, "Selected-channel roots/replies changed monitoring scope.");
        var root = changes.Messages.Single(message => message.Subject == "root");
        var reply = changes.Messages.Single(message => message.Subject == "reply");
        TestAssert.True(changes.Messages.All(message => message.RecipientContext is
                { ConversationType: "channel", MentionsComplete: true } &&
                message.IsWatchlistedChannel && message.TeamId == TeamId &&
                message.ChannelId == ChannelId && message.RootMessageId == "root") &&
                !root.IsMention && reply.IsMention,
            "Selected-channel roots/replies lost recipient or watchlist metadata.");
        var thread = await provider.GetThreadAsync(root.ThreadId);
        TestAssert.True(thread!.Messages.Single(message => message.Subject == "reply").IsMention &&
                        thread.Messages.All(message => message.RecipientContext?.ConversationType == "channel"),
            "Exact channel-thread mapping erased the reply's current-user mention.");
        foreach (var message in changes.Messages) AssertSafe(message.RecipientContext!);
    }

    private static void TestSearchConversions(WorkIqMessageProvider provider)
    {
        var missing = Item("search", "Please review the sample.", null, includeMentions: false);
        var mentioned = Item("search", "<at id=\"0\">Alex Sample</at>, please review.",
            new[] { UserMention(0, OtherId, "Alex Sample") });
        var exact = ConvertSearch(provider, missing, mentioned);
        TestAssert.True(exact.RecipientContext is { ConversationType: "unknown", MentionsComplete: true } &&
                        exact.RecipientContext.Mentions.Single().DisplayName == "Alex Sample",
            "Search exact-item enrichment discarded structured mentions or invented a chat type.");
        var fallback = ConvertSearch(provider, mentioned, null);
        TestAssert.True(fallback.RecipientContext is { MentionsComplete: false, Mentions.Count: 1 },
            "An unverified search hit's mention hints were discarded or declared complete.");
        var noMetadata = ConvertSearch(provider, missing, null);
        TestAssert.True(noMetadata.RecipientContext is { MentionsComplete: false, Mentions.Count: 0 },
            "A search summary with omitted mentions asserted that no recipients were mentioned.");
        var missingExact = ConvertSearch(provider, mentioned, missing);
        TestAssert.True(missingExact.RecipientContext is { MentionsComplete: false, Mentions.Count: 1 },
            "An exact response without metadata silently erased the search hit's mention hints.");
        var own = Item("search", "<at id=\"0\">Morgan Sample</at>, please review.",
            new[] { UserMention(0, CurrentId, CurrentName) });
        TestAssert.True(ConvertSearch(provider, missing, own).IsMention,
            "Search enrichment did not propagate an authenticated current-user mention.");
        foreach (var item in ChannelItems())
        {
            var channel = ConvertSearch(provider, item, item, "root");
            TestAssert.True(channel.RecipientContext is { ConversationType: "channel", MentionsComplete: true } &&
                            channel.IsWatchlistedChannel &&
                            channel.IsMention == ((string)item["id"]! == "reply"),
                "All-activity search roots/replies lost recipient or watchlist metadata.");
        }
        foreach (var message in new[] { exact, fallback, noMetadata, missingExact })
            AssertSafe(message.RecipientContext!);
    }

    private static Message ConvertSearch(
        WorkIqMessageProvider provider, object searchItem, object? exactItem, string? root = null)
    {
        var hit = Deserialize("TeamsSearchHit", new { resource = searchItem });
        var exact = exactItem is null ? null : Deserialize("ChatMessage", exactItem);
        var verification = Activator.CreateInstance(Nested("TeamsSearchVerification"),
            [hit, exact, root, exactItem is null ? 503 : 200])!;
        var candidate = typeof(WorkIqMessageProvider).GetMethod(
            "CreateStructuredTeamsCandidate", PrivateInstance)!.Invoke(provider,
            [verification, CurrentId, Anchor.AddMinutes(-5), Anchor.AddMinutes(5)])!;
        return (Message)candidate.GetType().GetProperty("Message")!.GetValue(candidate)!;
    }

    private static void TestUnverifiedIdentity()
    {
        var provider = Provider(TeamsMonitoringScope.Off);
        var item = Deserialize("ChatMessage", Item("unverified", "Please review.",
            new[] { UserMention(0, Account, CurrentName) }));
        var message = (Message)typeof(WorkIqMessageProvider).GetMethod(
            "ToTeamsMessage", PrivateInstance)!.Invoke(provider,
            ["group", "Sample", item, DateTimeOffset.MinValue, "group"])!;
        TestAssert.True(message.RecipientContext is { MentionsComplete: false, CurrentUserDisplayName: "" } &&
                        !message.IsMention,
            "The configured account/display name was mistaken for an authenticated profile.");
        var noMentions = Deserialize("ChatMessage", Item("unverified-empty", "Please review.", Array.Empty<object>()));
        var empty = (Message)typeof(WorkIqMessageProvider).GetMethod(
            "ToTeamsMessage", PrivateInstance)!.Invoke(provider,
            ["group", "Sample", noMentions, DateTimeOffset.MinValue, "futureType"])!;
        TestAssert.True(empty.RecipientContext is { MentionsComplete: false, ConversationType: "unknown" },
            "An unverified identity or future conversation type was not conservative.");
    }

    private static async Task TestAcknowledgmentsAsync(WorkIqMessageProvider provider)
    {
        var result = await provider.RefreshThreadForAlertAsync(
            OpaqueIdentifier.Encode("teams-thread:", "acknowledgments"), Anchor.AddMinutes(-1));
        TestAssert.True(result.IsComplete && result.Thread is not null &&
                        result.Thread.Messages.Count(message => message.IsFromMe) == 1 &&
                        result.Thread.Messages.Count(message => message.IsLikedByMe) == 1 &&
                        result.Thread.Messages.All(message => message.Preview.Length == 0),
            "Recipient extraction changed own-reply/like handling or retained content in metadata-only refresh.");
    }

    private static async Task TestProfileNamesAsync()
    {
        try
        {
            foreach (var name in new[] { "missing", "identifiers" })
            {
                Environment.SetEnvironmentVariable("PEGGY_FAKE_RECIPIENT_PROFILE_NAME", name);
                var thread = await Provider(TeamsMonitoringScope.ChatsOnly)
                    .GetThreadAsync(OpaqueIdentifier.Encode("teams-thread:", "direct"));
                var context = thread!.Messages.Single().RecipientContext!;
                TestAssert.Equal(name == "missing" ? string.Empty : CurrentName, context.CurrentUserDisplayName,
                    "The authenticated display name was replaced by account identity or leaked identifiers.");
                TestAssert.True(context.MentionsComplete,
                    "A missing profile display name invalidated an otherwise explicit empty mentions array.");
                AssertSafe(context);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PEGGY_FAKE_RECIPIENT_PROFILE_NAME", null);
        }
    }

    private static void AssertSafe(TeamsRecipientContext context)
    {
        var json = JsonSerializer.Serialize(context);
        TestAssert.True(new[] { Account, Alias, CurrentId, OtherId, TeamId, ChannelId, "sip:", "mailto:" }
                .All(identifier => !json.Contains(identifier, StringComparison.OrdinalIgnoreCase)) &&
                !json.Contains("\"Id\"", StringComparison.Ordinal) &&
                context.Mentions.Count <= 64 &&
                context.Mentions.All(mention => mention.DisplayName.Length <= 160),
            "Recipient context exported raw identifiers or unbounded display-name/mention metadata.");
    }

    private static Type Nested(string name)
        => typeof(WorkIqMessageProvider).GetNestedType(name, BindingFlags.NonPublic)!;

    private static object Deserialize(string name, object value)
        => JsonSerializer.Deserialize(JsonSerializer.Serialize(value), Nested(name))!;

    private static bool IsReadOnlyRequest(string command, string url)
        => command == "fetch" ||
            (command == "call-function" &&
             Uri.UnescapeDataString(url.Split('?')[0]) ==
                 $"/teams/{TeamId}/channels/{ChannelId}/messages/delta()");

    public static void HandleFakeRequest(string command, string url)
    {
        TestAssert.True(IsReadOnlyRequest(command, url),
            "Recipient tests must only execute Graph fetches or the selected channel's read-only delta function.");
        var logPath = Environment.GetEnvironmentVariable("PEGGY_FAKE_WORKIQ_LOG")!;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.AppendAllText(logPath, $"{command} {url}{Environment.NewLine}");
                break;
            }
            catch (IOException) when (attempt < 100) { Thread.Sleep(10); }
        }
        var parts = Uri.UnescapeDataString(url.Split('?')[0]).Split('/', StringSplitOptions.RemoveEmptyEntries);
        object response = parts switch
        {
            ["me"] => new
            {
                id = CurrentId, displayName = Environment.GetEnvironmentVariable("PEGGY_FAKE_RECIPIENT_PROFILE_NAME") switch
                {
                    "missing" => null,
                    "identifiers" => $"{CurrentName} {CurrentId} {Account}",
                    _ => CurrentName
                },
                mail = Account, userPrincipalName = Account,
                proxyAddresses = new[] { $"SMTP:{Account}", $"smtp:{Alias}" }
            },
            ["me", "chats"] => new { value = new[] { Chat("group", "group"), Chat("direct", "oneOnOne"),
                Chat("meeting", "meeting"), Chat("preview-only", "group") } },
            ["me", "chats", var id] => Chat(id, "group"),
            ["me", "chats", "group", "messages"] => new { value = GroupItems() },
            ["me", "chats", "preview-only", "messages"] => new { value = Array.Empty<object>() },
            ["me", "chats", "acknowledgments", "messages"] => new { value = AcknowledgmentItems() },
            ["me", "chats", var id, "messages"] => new
            {
                value = new[] { Item(id, "Please review the synthetic example.", Array.Empty<object>(), chatId: id) }
            },
            ["teams", TeamId, "channels", ChannelId] => new { id = ChannelId, displayName = "Sample channel" },
            ["teams", TeamId, "channels", ChannelId, "messages"] => ChannelPage(),
            ["teams", TeamId, "channels", ChannelId, "messages", "delta()"] => ChannelPage(),
            ["teams", TeamId, "channels", ChannelId, "messages", "root"] => ChannelItems()[0],
            ["teams", TeamId, "channels", ChannelId, "messages", "root", "replies"] =>
                new { value = new[] { ChannelItems()[1] } },
            _ => throw new InvalidOperationException($"Unexpected recipient fixture request: {url}")
        };
        Console.WriteLine(JsonSerializer.Serialize(response));
    }

    private static object Chat(string id, string type)
        => new
        {
            id, topic = "Sample conversation", chatType = type,
            viewpoint = new { lastMessageReadDateTime = Anchor.AddHours(-1) },
            lastMessagePreview = Item(id, "Please review.", null, includeMentions: false, chatId: id)
        };

    private static object UserMention(int index, string? id, string? name)
        => new { id = index, mentionText = name, mentioned = new { user = new { id, displayName = name } } };

    private static object ConversationMention(string type)
        => new { id = 0, mentionText = "Sample audience",
            mentioned = new { conversation = new { id = TeamId, displayName = "Sample audience",
                conversationIdentityType = type } } };

    private static Dictionary<string, object?> Item(
        string id, string content, object? mentions, bool includeMentions = true, string chatId = "group")
    {
        var item = new Dictionary<string, object?>
        {
            ["id"] = id, ["subject"] = id, ["chatId"] = chatId,
            ["createdDateTime"] = Anchor, ["lastModifiedDateTime"] = Anchor,
            ["messageType"] = "message",
            ["from"] = new { user = new { id = OtherId, displayName = "Taylor Sample" } },
            ["body"] = new { contentType = "html", content },
            ["webUrl"] = $"https://teams.microsoft.com/l/message/{Uri.EscapeDataString(chatId)}/{id}"
        };
        if (includeMentions) item["mentions"] = mentions;
        return item;
    }

    private static List<Dictionary<string, object?>> GroupItems()
    {
        var other = UserMention(0, OtherId, "Alex Sample");
        var own = UserMention(0, CurrentId, CurrentName);
        var items = new List<Dictionary<string, object?>>
        {
            Item("other", "<at id=\"0\">Alex Sample</at>, please review.", new[] { other }),
            Item("own", "<at id=\"0\">Morgan Sample</at>, please review.", new[] { own }),
            Item("alias", "Please review.", new[] { UserMention(0, Alias, CurrentName) }),
            Item("mixed", "<at id=\"0\">Alex Sample</at> and <at id=\"1\">Morgan Sample</at>, please review.",
                new[] { other, UserMention(1, CurrentId, CurrentName) }),
            Item("same-name", "Please review.", new[] { UserMention(0, OtherId, CurrentName) }),
            Item("team", "Sample audience, please review.", new[] { ConversationMention("team") }),
            Item("channel", "Sample audience, please review.", new[] { ConversationMention("channel") }),
            Item("tag", "Reviewers, please review.", new[] { new { id = 0, mentionText = "Reviewers",
                mentioned = new { tag = new { id = TeamId, displayName = "Reviewers" } } } }),
            Item("unknown", "Sample audience, please review.", new[] { ConversationMention("chat") }),
            Item("missing", "Please review.", null, includeMentions: false),
            Item("null", "Please review.", null),
            Item("malformed", "Please review.", new { truncated = true }),
            Item("malformed-entry", "Please review.", new object?[] { null, 17 }),
            Item("name-only", "Morgan Sample, please review.", new[] { UserMention(0, null, CurrentName) }),
            Item("opaque-identity", "Please review.", new[] { UserMention(0, "unverified-opaque-user", "Sample") }),
            Item("missing-identity", "Please review.", new[] { new { id = 0, mentionText = CurrentName } }),
            Item("empty", "Morgan Sample, please review.", Array.Empty<object>()),
            Item("truncated", "<at id=\"0\">Alex</at> and <at id=\"1\">Morgan</at>, please review.", new[] { other }),
            Item("next-link", "Please review.", new[] { other }),
            Item("count", "Please review.", new[] { other }),
            Item("duplicate-index", "Please review.", new[] { other, own }),
            Item("too-many", "Please review.", Enumerable.Range(0, 65)
                .Select(index => UserMention(index, index == 64 ? CurrentId : OtherId,
                    index == 64 ? CurrentName : "Alex Sample")).ToArray()),
            Item("conflicting-identities", "Please review.", new[] { new { id = 0,
                mentioned = new { user = new { id = CurrentId, displayName = CurrentName },
                    conversation = new { id = TeamId, conversationIdentityType = "team" } } } }),
            Item("malformed-name", "Please review.", new[] { new { id = 0,
                mentioned = new { user = new { id = OtherId, displayName = 42 } } } }),
            Item("private-name", "Please review.", new[] { UserMention(0, OtherId,
                $"<b>Alex Sample</b> {OtherId} {Account} sip:{Alias}") }),
            Item("long-name", "Please review.", new[] { UserMention(0, OtherId, new string('x', 200)) })
        };
        items.Single(item => (string)item["id"]! == "next-link")["mentions@odata.nextLink"] = "/more-mentions";
        items.Single(item => (string)item["id"]! == "count")["mentions@odata.count"] = 2;
        return items;
    }

    private static Dictionary<string, object?>[] ChannelItems()
    {
        var items = new[]
        {
            Item("root", "<at id=\"0\">Alex Sample</at>, please review.",
                new[] { UserMention(0, OtherId, "Alex Sample") }, chatId: ChannelId),
            Item("reply", "<at id=\"0\">Morgan Sample</at>, please review.",
                new[] { UserMention(0, CurrentId, CurrentName) }, chatId: ChannelId)
        };
        foreach (var item in items)
        {
            item.Remove("chatId");
            item["channelIdentity"] = new { teamId = TeamId, channelId = ChannelId };
            item["webUrl"] = $"{item["webUrl"]}?parentMessageId=root";
        }
        items[1]["replyToId"] = "root";
        return items;
    }

    private static object ChannelPage()
    {
        var items = ChannelItems();
        items[0]["replies"] = new[] { items[1] };
        return new { value = new[] { items[0] } };
    }

    private static object[] AcknowledgmentItems()
    {
        var liked = Item("liked", "Please review.", new { malformed = true }, chatId: "acknowledgments");
        liked["reactions"] = new[] { new { reactionType = "like", user = new { user = new { id = CurrentId } } } };
        var own = Item("own-reply", "Reviewed.", null, chatId: "acknowledgments");
        own["from"] = new { user = new { id = CurrentId, displayName = CurrentName } };
        return [liked, own];
    }
}
