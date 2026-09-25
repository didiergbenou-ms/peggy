using System.IO;
using System.Text.Json;
using Peggy.Core;
using Peggy.Tray;

namespace Peggy.Core.Tests;

internal static class AlertThreadRefreshProviderTests
{
    private const string Account = "refresh@example.invalid";
    private const string Alias = "alias@example.invalid";
    private const string Conversation = "conversation'with-quote";
    private const string ChatId = "19:refresh@thread.v2";
    private const string TeamId = "refresh-team";
    private const string ChannelId = "19:refresh@thread.tacv2";
    private const string RootId = "refresh-root";
    private static readonly DateTimeOffset Anchor = DateTimeOffset.Parse("2026-09-22T10:00:00Z");
    private static readonly string EmailThread = OpaqueIdentifier.Encode("email-thread:", Conversation);
    private static readonly string ChatThread = OpaqueIdentifier.Encode("teams-thread:", ChatId);
    private static readonly string ChannelThread = "teams-channel-thread:" +
        string.Join(":", new[] { TeamId, ChannelId, RootId }.Select(id => OpaqueIdentifier.Encode("", id)));

    public static async Task RunAsync(string directory)
    {
        var names = new[]
        {
            "PEGGY_FAKE_WORKIQ_SCENARIO", "PEGGY_FAKE_WORKIQ_LOG",
            "PEGGY_FAKE_ALERT_REFRESH_CASE"
        };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var logPath = Path.Combine(directory, "alert-thread-refresh.log");
        try
        {
            Directory.CreateDirectory(directory);
            Environment.SetEnvironmentVariable("PEGGY_FAKE_WORKIQ_SCENARIO", "alert-thread-refresh");
            Environment.SetEnvironmentVariable("PEGGY_FAKE_WORKIQ_LOG", logPath);
            await TestDefaultAndMockAsync();
            await TestEmailAsync(logPath);
            await TestTeamsAsync(logPath);
            await TestAcknowledgmentMetadataAsync(logPath);
            await TestIncompleteReadsAsync(logPath);
            await TestCancellationAsync(logPath);
        }
        finally
        {
            foreach (var pair in previous)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
            File.Delete(logPath);
        }
    }

    private static WorkIqMessageProvider Provider()
        => new(Environment.ProcessPath, Account,
            new PeggySettings
            {
                OutlookScope = OutlookMonitoringScope.Off,
                TeamsScope = TeamsMonitoringScope.Off
            },
            principalId: "user-me");

    private static void SetCase(string name, string logPath)
    {
        File.WriteAllText(logPath, string.Empty);
        Environment.SetEnvironmentVariable("PEGGY_FAKE_ALERT_REFRESH_CASE", name);
    }

    private static async Task TestDefaultAndMockAsync()
    {
        IMessageProvider unsupported = new UnsupportedProvider();
        var result = await unsupported.RefreshThreadForAlertAsync("unknown", Anchor);
        TestAssert.True(!result.IsComplete && result.Thread is null && result.Warning is not null,
            "The default provider must explicitly report unsupported/incomplete refresh.");
        var mock = new MockMessageProvider();
        var known = await mock.RefreshThreadForAlertAsync("t-100", Anchor);
        TestAssert.True(known.IsComplete && known.Thread?.Messages.Count == 2,
            "Mock refresh did not include its current messages and historical context.");
        await mock.MarkReadAsync("m-1001");
        var fresh = await mock.RefreshThreadForAlertAsync("t-100", Anchor);
        TestAssert.True(fresh.Thread!.Messages.Single(message => message.Id == "m-1001").IsRead,
            "Mock refresh reused stale thread state.");
        TestAssert.True(!(await mock.RefreshThreadForAlertAsync("unknown", Anchor)).IsComplete,
            "An unknown mock thread must not be complete.");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await TestAssert.ThrowsAsync<OperationCanceledException>(
            () => unsupported.RefreshThreadForAlertAsync("unknown", Anchor, canceled.Token),
            "The default refresh ignored cancellation.");
        await TestAssert.ThrowsAsync<OperationCanceledException>(
            () => mock.RefreshThreadForAlertAsync("t-100", Anchor, canceled.Token),
            "Mock refresh ignored cancellation.");
    }

    private static async Task TestEmailAsync(string logPath)
    {
        SetCase("email-complete", logPath);
        var provider = Provider();
        var result = await provider.RefreshThreadForAlertAsync(EmailThread, Anchor);
        TestAssert.True(result.IsComplete && result.Warning is null && result.Thread is not null,
            "An exhausted email conversation was not complete.");
        var messages = result.Thread!.Messages;
        TestAssert.True(messages.All(message => message.ThreadId == EmailThread),
            "Email refresh changed the exact conversation identity.");
        var mine = messages.Single(message => message.Id == EmailId("own"));
        TestAssert.True(mine.IsFromMe && mine.SentAtUtc == Anchor.AddMinutes(2),
            "The own sent reply beyond the first page lost authenticated identity or sent time.");
        TestAssert.True(messages.Single(message => message.Id == EmailId("sender-own")).IsFromMe,
            "An authenticated Outlook sender identity was not recognized.");
        TestAssert.True(!messages.Single(message => message.Id == EmailId("same-name")).IsFromMe,
            "An Outlook sender display name was mistaken for authenticated identity.");
        TestAssert.True(messages.Select(message => message.Id).Distinct().Count() == messages.Count &&
                        messages.All(message => SourceId(message) is not ("draft" or "deleted" or "deleted-child" or "removed")),
            "Draft, deleted, tombstone, or duplicate email leaked into the context.");
        TestAssert.True(messages.All(message => message.Preview.Length == 0 && message.Subject.Length == 0),
            "Reply-only email refresh unnecessarily retained content.");
        TestAssert.Equal(20, messages.Count(message => message.ReceivedAt < Anchor),
            "Earlier email context was not bounded independently of the required window.");
        TestAssert.True(messages.SequenceEqual(messages.OrderBy(message => message.ReceivedAt)),
            "Email context was not sorted chronologically.");
        var requests = File.ReadAllLines(logPath);
        TestAssert.True(requests.Count(line => line.Contains("fetch /me/messages?", StringComparison.Ordinal)) == 2 &&
                        requests.Any(line => line.Contains("%24skiptoken=second", StringComparison.Ordinal)) &&
                        requests.Any(line => line.Contains("/deleted-root/childFolders", StringComparison.Ordinal)),
            "Email refresh did not traverse both message pages and the deleted-folder hierarchy.");
        TestAssert.True(requests.Where(line => line.StartsWith("fetch /me/messages?", StringComparison.Ordinal))
            .All(line => !line.Contains("body", StringComparison.OrdinalIgnoreCase)),
            "Reply-only email refresh requested message bodies.");

        SetCase("email-updated", logPath);
        var updated = await provider.RefreshThreadForAlertAsync(EmailThread, Anchor);
        TestAssert.True(updated.IsComplete && updated.Thread!.Messages.Count == 1 &&
                        File.ReadAllLines(logPath).Any(line => line.StartsWith("fetch /me?", StringComparison.Ordinal)),
            "A subsequent refresh reused cached message context or unauthenticated identity.");
        SetCase("email-relative", logPath);
        var relative = await provider.RefreshThreadForAlertAsync(EmailThread, Anchor);
        TestAssert.True(relative.IsComplete && relative.Thread!.Messages.Any(message => message.IsFromMe),
            "A safe relative Graph continuation was not followed.");
        SetCase("email-full-body", logPath);
        var full = await Provider().RefreshThreadForAlertAsync(EmailThread, Anchor);
        TestAssert.True(full.IsComplete && full.Thread!.Messages.Single().Preview.Length == 0,
            "Reply-only refresh incorrectly depends on interpreting the email body.");
    }

    private static async Task TestTeamsAsync(string logPath)
    {
        foreach (var (name, threadId) in new[]
                 { ("chat-complete", ChatThread), ("channel-complete", ChannelThread) })
        {
            SetCase(name, logPath);
            var result = await Provider().RefreshThreadForAlertAsync(threadId, Anchor.AddHours(-2));
            TestAssert.True(result.IsComplete && result.Thread is not null,
                $"Complete {name} context was not recognized.");
            var messages = result.Thread!.Messages;
            if (name == "chat-complete")
            {
                TestAssert.True(File.ReadAllLines(logPath).Any(line =>
                    line.Contains("$filter=lastModifiedDateTime%20gt%20", StringComparison.Ordinal) &&
                    line.Contains("$orderby=lastModifiedDateTime%20desc", StringComparison.Ordinal)),
                    "Chat refresh must bound the modified-time window rather than truncate long-lived chats.");
            }
            TestAssert.True(messages.All(message => message.ThreadId == threadId) &&
                            messages.All(message => SourceId(message) is not ("deleted" or "service" or "removed")),
                $"{name} included wrong-thread, service, or deleted content.");
            TestAssert.True(messages.Single(message => SourceId(message) == "own").IsFromMe &&
                            messages.Single(message => SourceId(message) == "own-email").IsFromMe &&
                            messages.Single(message => SourceId(message) == "own-alias").IsFromMe,
                $"{name} failed to identify the authenticated sender by ID/email.");
            TestAssert.True(!messages.Single(message => SourceId(message) == "same-name").IsFromMe,
                $"{name} inferred identity from a sender display name.");
            var edited = messages.Single(message => SourceId(message) == "edited");
            TestAssert.Equal(Anchor.AddMinutes(3), edited.ReceivedAt, $"{name} lost the edit timestamp.");
            TestAssert.Equal<DateTimeOffset?>(Anchor, edited.SentAtUtc,
                $"{name} confused a message edit with a newly sent reply.");
            var encodedItem = edited.Id.Split(':')[^1];
            TestAssert.True(OpaqueIdentifier.TryDecode(encodedItem, "", out var versioned) &&
                            versioned == $"edited\n{Anchor.AddMinutes(3).UtcTicks}",
                $"{name} lost the versioned source message identity.");
            TestAssert.Equal(Anchor.AddHours(-1),
                messages.Single(message => SourceId(message) == "reaction").ReceivedAt,
                $"{name} represented a reaction as new textual activity.");
        }
        foreach (var (name, threadId) in new[]
                 { ("chat-paged", ChatThread), ("channel-paged", ChannelThread), ("chat-relative-paged", ChatThread) })
        {
            SetCase(name, logPath);
            var result = await Provider().RefreshThreadForAlertAsync(threadId, Anchor);
            TestAssert.True(result.IsComplete && result.Warning is null && result.Thread is not null &&
                result.Thread.Messages.Any(message => SourceId(message) == "paged-request") &&
                result.Thread.Messages.Any(message => message.IsFromMe && SourceId(message) == "paged-own"),
                $"{name} did not reach the original request and own reply past the provider's ten-item page cap.");
            TestAssert.True(result.Thread!.Messages.All(message =>
                                !message.Id.Contains("truncated", StringComparison.Ordinal) &&
                                !message.Preview.Contains("paging", StringComparison.Ordinal)) &&
                            File.ReadAllLines(logPath).Count(line =>
                                line.Contains("%24skiptoken=A%2BB%2FC%3D", StringComparison.Ordinal)) == 1,
                $"{name} invented synthetic content or modified the opaque continuation token.");
            var original = result.Thread.Messages.Single(message => SourceId(message) == "paged-request");
            var decision = new AlertDecision(threadId, AlertTier.Attention, "Reply required",
                "A pending request", "", [original.Id]);
            var refresher = new AlertNotificationRefresher(Provider(), _ => { });
            TestAssert.True(await refresher.RefreshAsync(decision, [original], Anchor, CancellationToken.None) is null,
                "The paged sent reply did not suppress the notification without an LLM.");
        }
        SetCase("chat-overlap-paged", logPath);
        var overlap = await Provider().RefreshThreadForAlertAsync(ChatThread, Anchor);
        TestAssert.True(overlap.IsComplete && overlap.Thread!.Messages.Count == 12,
            "A progressing overlapping page failed to deduplicate safely.");
        SetCase("chat-full-body", logPath);
        var full = await Provider().RefreshThreadForAlertAsync(ChatThread, Anchor);
        TestAssert.True(full.IsComplete && full.Thread!.Messages.Single().Preview.Length == 0,
            "Reply-only Teams refresh incorrectly retained or interpreted body text.");
    }

    private static async Task TestAcknowledgmentMetadataAsync(string logPath)
    {
        foreach (var name in new[] { "email-missing-body", "email-large-body", "email-unknown-body-type",
                     "chat-large-body", "chat-empty-body" })
        {
            SetCase(name, logPath);
            var threadId = name.StartsWith("email-", StringComparison.Ordinal) ? EmailThread : ChatThread;
            var result = await Provider().RefreshThreadForAlertAsync(threadId, Anchor);
            TestAssert.True(result.IsComplete, "Body availability or size blocked metadata-only acknowledgment.");
        }
        foreach (var prefix in new[] { "chat", "channel" })
        foreach (var suffix in new[] { "like", "thumbs-up", "skin-tone", "other-person", "heart", "removed", "no-user" })
        {
            SetCase($"{prefix}-reaction-{suffix}", logPath);
            var threadId = prefix == "chat" ? ChatThread : ChannelThread;
            var result = await Provider().RefreshThreadForAlertAsync(threadId, Anchor);
            var expected = suffix is "like" or "thumbs-up" or "skin-tone";
            var request = result.Thread!.Messages.Single(message => SourceId(message) == "reaction-request");
            TestAssert.True(result.IsComplete && request.IsLikedByMe == expected,
                $"Reaction ownership/type was not checked for {prefix}/{suffix}.");
            var decision = new AlertDecision(threadId, AlertTier.Attention, "JIT request", "Reply requested", "", [request.Id]);
            var suppressed = await new AlertNotificationRefresher(Provider(), _ => { }).RefreshAsync(
                decision, [request], Anchor, CancellationToken.None);
            TestAssert.Equal(expected, suppressed is null, "The provider's exact triggering-message like was not applied.");
        }
        SetCase("email-sent-before-received", logPath);
        var sent = await Provider().RefreshThreadForAlertAsync(EmailThread, Anchor);
        var own = sent.Thread!.Messages.Single(message => message.IsFromMe);
        TestAssert.True(own.SentAtUtc == Anchor.AddMinutes(-1) && own.ReceivedAt == Anchor.AddMinutes(1),
            "Reply detection must use sent time, not a later received timestamp for outgoing mail.");
        var incoming = sent.Thread.Messages.Single(message => !message.IsFromMe);
        var candidate = new AlertDecision(EmailThread, AlertTier.Attention, "Request", "Pending", "", [incoming.Id]);
        TestAssert.True(await new AlertNotificationRefresher(Provider(), _ => { })
            .RefreshAsync(candidate, [incoming], Anchor, CancellationToken.None) is not null,
            "An outgoing email sent before the trigger suppressed it based on delivery time.");
    }

    private static async Task TestIncompleteReadsAsync(string logPath)
    {
        foreach (var name in new[]
                 {
                     "email-empty", "email-malformed", "email-wrong", "email-missing-draft",
                     "email-missing-folder", "email-missing-time", "email-unsafe-link",
                     "email-wrong-link", "email-repeated", "email-limit", "email-page-limit",
                     "email-deleted-unavailable", "chat-empty", "chat-wrong",
                     "chat-unsafe-paged", "chat-wrong-paged", "channel-wrong-paged",
                     "chat-repeated-paged", "chat-replayed-final-paged", "chat-limit-paged",
                     "chat-page-limit-paged", "channel-page-limit-paged",
                     "chat-wrong-message", "chat-missing-type", "channel-wrong-root",
                     "channel-deleted-root", "channel-wrong-reply", "channel-wrong-identity"
                 })
        {
            SetCase(name, logPath);
            var threadId = name.StartsWith("email-", StringComparison.Ordinal) ? EmailThread
                : name.StartsWith("chat-", StringComparison.Ordinal) ? ChatThread : ChannelThread;
            var result = await Provider().RefreshThreadForAlertAsync(threadId, Anchor);
            TestAssert.True(!result.IsComplete && result.Warning is not null,
                $"{name} was incorrectly marked complete.");
            TestAssert.True(result.Thread?.Messages.All(message => message.ThreadId == threadId) ?? true,
                $"{name} leaked another thread.");
            var requests = File.ReadAllLines(logPath);
            TestAssert.True(requests.Length <= 12, $"{name} exceeded the logical read budget.");
            if (name == "email-limit")
            {
                TestAssert.Equal(5, requests.Count(line => line.StartsWith("fetch /me/messages?", StringComparison.Ordinal)),
                    "The 250-message bound silently read further pages.");
            }
        }
        SetCase("email-unavailable", logPath);
        var exception = await TestAssert.ThrowsAsync<InvalidOperationException>(
            () => Provider().RefreshThreadForAlertAsync(EmailThread, Anchor),
            "An unavailable source must not become complete context.");
        TestAssert.Contains("403", exception.Message, "The normal source transport error was not preserved.");
        SetCase("chat-failed-paged", logPath);
        await TestAssert.ThrowsAsync<InvalidOperationException>(
            () => Provider().RefreshThreadForAlertAsync(ChatThread, Anchor),
            "A failed continuation read was treated as complete context.");
        SetCase("identity-mismatch", logPath);
        await TestAssert.ThrowsAsync<InvalidOperationException>(
            () => Provider().RefreshThreadForAlertAsync(ChatThread, Anchor),
            "The refresh trusted a different authenticated principal.");
        SetCase("email-complete", logPath);
        var unsupported = await Provider().RefreshThreadForAlertAsync("unsupported", Anchor);
        TestAssert.True(!unsupported.IsComplete && unsupported.Thread is null &&
                        File.ReadAllText(logPath).Length == 0,
            "An unsupported identifier performed a read or returned complete context.");
    }

    private static async Task TestCancellationAsync(string logPath)
    {
        SetCase("email-cancel", logPath);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await TestAssert.ThrowsAsync<OperationCanceledException>(
            () => Provider().RefreshThreadForAlertAsync(EmailThread, Anchor, canceled.Token),
            "A canceled refresh started reading.");
        TestAssert.Equal(string.Empty, File.ReadAllText(logPath), "Canceled refresh invoked WorkIQ.");
        using var inFlight = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var refresh = Provider().RefreshThreadForAlertAsync(EmailThread, Anchor, inFlight.Token);
        while (!refresh.IsCompleted && !File.ReadAllText(logPath).Contains("fetch /me/messages?", StringComparison.Ordinal))
        {
            await Task.Delay(20);
        }
        TestAssert.True(!refresh.IsCompleted, "The cancellation fixture never reached an in-flight message read.");
        inFlight.Cancel();
        await TestAssert.ThrowsAsync<OperationCanceledException>(() => refresh,
            "In-flight cancellation was swallowed or marked complete.");
    }

    public static void HandleFakeRequest(string command, string url)
    {
        var logPath = Environment.GetEnvironmentVariable("PEGGY_FAKE_WORKIQ_LOG")!;
        File.AppendAllText(logPath, $"{command} {url}{Environment.NewLine}");
        if (command != "fetch")
        {
            throw new InvalidOperationException("Alert refresh must perform only read-only fetches.");
        }
        var name = Environment.GetEnvironmentVariable("PEGGY_FAKE_ALERT_REFRESH_CASE")!;
        var path = Uri.UnescapeDataString(url.Split('?')[0]);
        object response;
        if (path == "/me")
        {
            response = new
            {
                id = name == "identity-mismatch" ? "another-user" : "user-me",
                mail = Account, userPrincipalName = "refresh-upn@example.invalid",
                proxyAddresses = new[] { $"SMTP:{Account}", $"smtp:{Alias}" }
            };
        }
        else if (path == "/me/mailFolders/deleteditems")
        {
            response = name == "email-deleted-unavailable" ? new { } : (object)new
            {
                id = "deleted-root", childFolderCount = name == "email-complete" ? 1 : 0
            };
        }
        else if (path == "/me/mailFolders/deleted-root/childFolders")
        {
            response = Page([new { id = "deleted-child", childFolderCount = 0 }]);
        }
        else if (path == "/me/messages")
        {
            response = EmailResponse(name, url);
        }
        else if (path == $"/me/chats/{ChatId}")
        {
            response = new { id = name == "chat-wrong" ? "another-chat" : ChatId, topic = "Chat" };
        }
        else if (path == $"/me/chats/{ChatId}/messages")
        {
            if (name.Contains("-reaction-", StringComparison.Ordinal))
            {
                Console.WriteLine(JsonSerializer.Serialize(Page([ReactionMessage(name, false)])));
                return;
            }
            if (name.Contains("paged", StringComparison.Ordinal))
            {
                Console.WriteLine(JsonSerializer.Serialize(TeamsPagedResponse(name, url, false)));
                return;
            }
            var items = name switch
            {
                "chat-empty" => [],
                "chat-wrong-message" => new[] { TeamsMessage("wrong", "Wrong thread", chatId: "another-chat") },
                "chat-missing-type" => new[] { TeamsMessage("missing-type", "Missing type", type: null) },
                "chat-empty-body" => new[] { TeamsMessage("reaction", "") },
                "chat-full-body" => new[] { TeamsMessage("full", new string('x', 800) + " Still need the second answer.") },
                "chat-large-body" => new[] { TeamsMessage("large", new string('x', 17_000)) },
                _ => TeamsItems(isChannel: false)
            };
            response = Page(items);
        }
        else if (path == $"/teams/{TeamId}/channels/{ChannelId}")
        {
            response = new { id = ChannelId, displayName = "Channel" };
        }
        else if (path == $"/teams/{TeamId}/channels/{ChannelId}/messages/{RootId}")
        {
            response = TeamsMessage(name == "channel-wrong-root" ? "another-root" : RootId,
                "Original root", deleted: name == "channel-deleted-root");
        }
        else if (path == $"/teams/{TeamId}/channels/{ChannelId}/messages/{RootId}/replies")
        {
            if (name.Contains("-reaction-", StringComparison.Ordinal))
            {
                Console.WriteLine(JsonSerializer.Serialize(Page([ReactionMessage(name, true)])));
                return;
            }
            if (name.Contains("paged", StringComparison.Ordinal))
            {
                Console.WriteLine(JsonSerializer.Serialize(TeamsPagedResponse(name, url, true)));
                return;
            }
            var items = name switch
            {
                "channel-wrong-reply" => new[] { TeamsMessage("wrong", "Wrong root", replyToId: "another-root") },
                "channel-wrong-identity" => new[] { TeamsMessage("wrong", "Wrong channel", replyToId: RootId, channelId: "another-channel") },
                _ => TeamsItems(isChannel: true)
            };
            response = Page(items);
        }
        else
        {
            throw new InvalidOperationException($"Unexpected alert refresh fixture request: {url}");
        }
        Console.WriteLine(JsonSerializer.Serialize(response));
    }

    private static object TeamsPagedResponse(string name, string url, bool isChannel)
    {
        var isNextPage = url.Contains("skiptoken", StringComparison.Ordinal);
        var marker = url.IndexOf("page=", StringComparison.Ordinal);
        var page = marker < 0 ? 1 : int.Parse(url[(marker + 5)..]);
        var path = isChannel
            ? $"/teams/{TeamId}/channels/{ChannelId}/messages/{RootId}/replies"
            : $"/me/chats/{ChatId}/messages";
        var next = (name == "chat-relative-paged" ? "" : "https://graph.microsoft.com/v1.0") +
            path + "?$top=10&$skiptoken=A%2BB%2FC%3D";
        var parent = isChannel ? RootId : null;
        if (name.Contains("page-limit", StringComparison.Ordinal))
            return Page([], $"https://graph.microsoft.com/v1.0{path}?page={page + 1}");
        if (name == "chat-limit-paged")
            return Page(Enumerable.Range(0, 50).Select(index =>
                TeamsMessage($"page-{page}-{index}", "Context", replyToId: parent)).ToArray(),
                $"https://graph.microsoft.com/v1.0{path}?page={page + 1}");
        if (isNextPage && name == "chat-failed-paged")
            return new { results = new[] { new { statusCode = 403, error = new { message = "Continuation unavailable" } } } };
        var first = Enumerable.Range(0, 10).Select(index =>
            TeamsMessage($"paged-{index}", $"Context {index}", replyToId: parent,
                created: Anchor.AddMinutes(20 + index))).ToArray();
        if (name == "chat-unsafe-paged")
            return Page(first, "https://example.invalid/me/chats/other/messages?$skiptoken=unsafe");
        if (name.Contains("wrong-paged", StringComparison.Ordinal))
            return Page(first, "https://graph.microsoft.com/v1.0/me/chats/other/messages?$skiptoken=wrong");
        if (name == "chat-repeated-paged") return Page(first, next);
        if (!isNextPage) return Page(first, next);
        if (name == "chat-replayed-final-paged") return Page(first);
        var last = new[]
        {
            TeamsMessage("paged-request", "Original paged request", replyToId: parent, created: Anchor),
            TeamsMessage("paged-own", "Own paged reply", senderId: "user-me",
                replyToId: parent, created: Anchor.AddMinutes(1))
        };
        return Page(name == "chat-overlap-paged" ? new[] { first[^1] }.Concat(last).ToArray() : last);
    }

    private static object EmailResponse(string name, string url)
    {
        if (name == "email-sent-before-received")
            return Page([Email("trigger", "Please reply", Anchor),
                Email("early-reply", "Old reply", Anchor.AddMinutes(1), address: Account, sent: Anchor.AddMinutes(-1))]);
        if (name == "email-cancel")
        {
            Thread.Sleep(TimeSpan.FromSeconds(15));
        }
        if (name == "email-unavailable")
        {
            return new { results = new[] { new { statusCode = 403, error = new { message = "Source unavailable" } } } };
        }
        if (name == "email-malformed")
        {
            return new { };
        }
        if (name is "email-limit" or "email-page-limit")
        {
            var marker = url.IndexOf("page=", StringComparison.Ordinal);
            var page = marker < 0 ? 1 : int.Parse(url[(marker + 5)..]);
            return Page(name == "email-page-limit" ? [] : Enumerable.Range(0, 50)
                    .Select(index => Email($"item-{page}-{index}", $"Page {page}", Anchor)).ToArray(),
                $"https://graph.microsoft.com/v1.0/me/messages?page={page + 1}");
        }
        if (name == "email-repeated")
        {
            return Page([Email("original", "Original", Anchor)],
                "https://graph.microsoft.com/v1.0/me/messages?page=repeat");
        }
        if (name is "email-unsafe-link" or "email-wrong-link")
        {
            return Page([Email("original", "Original", Anchor)],
                name == "email-unsafe-link" ? "https://example.invalid/me/messages?page=2"
                    : "https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages?page=2");
        }
        if (name == "email-complete" && !url.Contains("skiptoken", StringComparison.Ordinal))
        {
            var items = Enumerable.Range(0, 49)
                .Select(index => Email($"older-{index}", $"Earlier context {index}", Anchor.AddMinutes(-60 + index)))
                .Append(Email("original", "Old page copy", Anchor)).ToArray();
            return Page(items, "https://graph.microsoft.com/v1.0/me/messages?$skiptoken=second");
        }
        if (name == "email-relative" && !url.Contains("skiptoken", StringComparison.Ordinal))
        {
            return Page([Email("original", "Original", Anchor)], "/me/messages?$skiptoken=second");
        }
        if (name is "email-complete" or "email-relative")
        {
            return Page(
            [
                Email("original", "Fresh edited request", Anchor),
                Email("own", "Own sent reply", null, address: Alias, sent: Anchor.AddMinutes(2)),
                Email("same-name", "Same display name", Anchor.AddMinutes(1)),
                Email("sender-own", "Sent on behalf", Anchor.AddMinutes(2), senderAddress: Account),
                Email("draft", "EXCLUDED draft", Anchor, draft: true),
                Email("deleted", "EXCLUDED deleted", Anchor, folder: "deleted-root"),
                Email("deleted-child", "EXCLUDED deleted subfolder", Anchor, folder: "deleted-child"),
                new Dictionary<string, object?> { ["id"] = "removed", ["@removed"] = new { reason = "deleted" } }
            ]);
        }
        if (name == "email-empty")
        {
            return Page([]);
        }
        return Page([Email("original", name == "email-updated" ? "New read, not cached" : "Original",
            name == "email-missing-time" ? null : Anchor,
            conversation: name == "email-wrong" ? "another-conversation" : Conversation,
            draft: name == "email-missing-draft" ? null : false,
            folder: name == "email-missing-folder" ? null : "inbox",
            omitBody: name == "email-missing-body",
            bodyType: name == "email-unknown-body-type" ? "unknown" : "text",
            body: name == "email-full-body" ? new string('x', 800) + " Still need the second answer."
                : name == "email-large-body" ? new string('x', 17_000) : null)]);
    }

    private static object[] TeamsItems(bool isChannel)
    {
        var parent = isChannel ? RootId : null;
        return
        [
            TeamsMessage("own", "Own reply", senderId: "USER-ME", replyToId: parent),
            TeamsMessage("own-email", "Own email reply", emailSender: Account, replyToId: parent),
            TeamsMessage("own-alias", "Own alias reply", senderId: Alias, replyToId: parent),
            TeamsMessage("same-name", "Same display name", replyToId: parent),
            TeamsMessage("edited", "Old reply", replyToId: parent),
            TeamsMessage("edited", "Edited reply", replyToId: parent, edited: Anchor.AddMinutes(3)),
            TeamsMessage("reaction", "Existing text, later reaction", replyToId: parent,
                created: Anchor.AddHours(-1), modified: Anchor.AddMinutes(5)),
            TeamsMessage("deleted", "EXCLUDED deleted", replyToId: parent, deleted: true),
            TeamsMessage("service", "EXCLUDED service event", replyToId: parent, type: "systemEventMessage"),
            new Dictionary<string, object?>
            {
                ["id"] = "removed", ["replyToId"] = parent, ["@removed"] = new { reason = "deleted" }
            }
        ];
    }

    private static object ReactionMessage(string name, bool isChannel)
    {
        var type = name.EndsWith("thumbs-up", StringComparison.Ordinal) ? "\U0001F44D"
            : name.EndsWith("skin-tone", StringComparison.Ordinal) ? "\U0001F44D\U0001F3FD"
            : name.EndsWith("heart", StringComparison.Ordinal) ? "heart" : "like";
        var user = name.EndsWith("no-user", StringComparison.Ordinal) ? null
            : (object)new { user = new { id = name.EndsWith("other-person", StringComparison.Ordinal) ? "other-user" : "user-me" } };
        var reactions = name.EndsWith("removed", StringComparison.Ordinal) ? Array.Empty<object>()
            : [new { reactionType = type, user, createdDateTime = Anchor.AddMinutes(1) }];
        return TeamsMessage("reaction-request", "Please acknowledge", replyToId: isChannel ? RootId : null,
            reactions: reactions);
    }

    private static object TeamsMessage(
        string id, string content, string senderId = "other-user", string? replyToId = null,
        string? type = "message", bool deleted = false, DateTimeOffset? created = null,
        DateTimeOffset? edited = null, DateTimeOffset? modified = null,
        string? emailSender = null, string? chatId = null, string? channelId = null, object[]? reactions = null)
        => new
        {
            id, replyToId, messageType = type, createdDateTime = created ?? Anchor,
            lastEditedDateTime = edited, lastModifiedDateTime = modified ?? edited ?? created ?? Anchor,
            isDeleted = deleted, chatId,
            channelIdentity = channelId is null ? null : new { teamId = TeamId, channelId },
            from = emailSender is null
                ? (object)new { user = new { id = senderId, displayName = "Self Name" } }
                : new { emailAddress = new { address = emailSender, name = "Email identity" } },
            body = new { contentType = "html", content = $"<div>{content}</div>" },
            reactions = reactions ?? (id == "reaction" ? new object[] { new { reactionType = "like" } } : [])
        };

    private static object Email(
        string id, string preview, DateTimeOffset? received,
        string address = "other@example.invalid", DateTimeOffset? sent = null,
        bool? draft = false, string? folder = "inbox", string conversation = Conversation,
        string? senderAddress = null, string? body = null, bool omitBody = false, string bodyType = "text")
        => new
        {
            id, conversationId = conversation, parentFolderId = folder, isDraft = draft,
            receivedDateTime = received, sentDateTime = sent, isRead = true,
            subject = "The exact conversation", bodyPreview = preview,
            body = omitBody ? null : new { contentType = bodyType, content = body ?? preview },
            from = new { emailAddress = new { address, name = "Self Name" } },
            sender = senderAddress is null ? null : new { emailAddress = new { address = senderAddress } }
        };

    private static object Page(object[] items, string? next = null)
        => next is null ? new { value = items } : new Dictionary<string, object>
        {
            ["value"] = items, ["@odata.nextLink"] = next
        };

    private static string EmailId(string id) => OpaqueIdentifier.Encode("email-message:", id);

    private static string SourceId(Message message)
    {
        var encoded = message.Source == MessageSource.Email
            ? message.Id["email-message:".Length..] : message.Id.Split(':')[^1];
        TestAssert.True(OpaqueIdentifier.TryDecode(encoded, "", out var decoded), "Invalid message identity in fixture.");
        return decoded.Split('\n')[0];
    }

    private sealed class UnsupportedProvider : IMessageProvider
    {
        public Task<IReadOnlyList<Message>> ListUnreadAsync(MessageSource? source, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MessageChangeBatch> GetChangesAsync(MonitoringCursorState cursor, int limit, DateTimeOffset nowUtc, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MessageThread?> GetThreadAsync(string threadId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CalendarItem>> ListUpcomingEventsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TaskItem>> ListTasksAsync(bool includeCompleted, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<OutlookFolderInfo>> ListOutlookChildFoldersAsync(OutlookFolderInfo? parent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TeamsTeamInfo>> ListJoinedTeamsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TeamsChannelInfo>> ListTeamChannelsAsync(string teamId, string teamName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> AskWorkAsync(string question, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MessageContextResolution?> ResolveContextAsync(MessageSource source, string summary, string reason, DateTimeOffset activityUtc, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult> MarkReadAsync(string messageId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DraftResult> DraftReplyAsync(string messageId, string body, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkActionExecutionResult> ExecuteActionAsync(WorkActionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
