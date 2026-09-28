using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Peggy.Core;
using Peggy.Tray;

namespace Peggy.Core.Tests;

internal static class RecipientTargetingSmokeTests
{
    internal static async Task RunLiveAsync()
    {
        var now = DateTimeOffset.UtcNow;
        Message Sample(string id, string thread, string text, string type, TeamsMention[] mentions) =>
            new(id, MessageSource.Teams, thread, "Synthetic Colleague", "Synthetic support discussion", text, now, false)
            {
                RecipientContext = new(type, "Taylor Example", mentions, true),
                WebUrl = $"https://teams.microsoft.com/l/message/synthetic/{id}"
            };
        var messages = new[]
        {
            Sample("other-request", "shared-thread",
                "Morgan, the customer is asking when the next step on your case will be. Please send them an update.",
                "group", [new("Morgan", false, "user")]),
            Sample("user-request", "shared-thread",
                "Taylor Example, separately, please approve the access request assigned to you by 17:00 today.",
                "group", [new("Taylor Example", true, "user")]),
            Sample("direct-request", "direct-thread",
                "Please review and send me your decision on this proposal by 17:00 today.",
                "oneOnOne", []),
            Sample("team-request", "team-thread",
                "Everyone on this team, including Taylor Example: please each confirm your own on-call coverage by 17:00 today.",
                "group", [new("Everyone", false, "tag")])
        };
        var previous = Environment.GetEnvironmentVariable("PEGGY_MESSAGE_PROVIDER");
        try
        {
            Environment.SetEnvironmentVariable("PEGGY_MESSAGE_PROVIDER", "mock");
            await using var runner = new CopilotRunner(new PeggySettings(),
                account: "synthetic@example.invalid", principalId: "synthetic-principal");
            var result = await runner.RunAlertEvaluationAsync(messages, [], []);
            TestAssert.True(result.Decisions.All(decision => !decision.MessageIds.Contains("other-request")) &&
                new[] { "user-request", "direct-request", "team-request" }.All(id =>
                    result.Decisions.Any(decision => decision.MessageIds.Contains(id))),
                "Live synthetic classifier failed recipient targeting, direct-message or team-wide relevance.");
            Console.WriteLine("Synthetic recipient targeting passed through the existing single Copilot classification call.");
        }
        finally { Environment.SetEnvironmentVariable("PEGGY_MESSAGE_PROVIDER", previous); }
    }

    internal static void Run(string directory)
    {
        var now = DateTimeOffset.UtcNow;
        var other = new Message("message-1", MessageSource.Teams, "thread-1", "A colleague",
            "Shared customer discussion", "Morgan, when can you provide the next update?", now, false)
        {
            RecipientContext = new("group", "Taylor", [new("Morgan", false, "user")], true),
            WebUrl = "https://teams.microsoft.com/l/message/synthetic/1"
        };
        foreach (var tier in new[] { "critical", "attention", "awareness", "none" })
        {
            TestAssert.Equal(0, Parse(Output("other", tier), [other]).Count,
                "Work explicitly assessed as directed only at someone else became the user's alert.");
        }
        foreach (var context in new[]
        {
            new TeamsRecipientContext("oneOnOne", "Taylor", [], true),
            new TeamsRecipientContext("group", "Taylor", [new("Taylor", true, "user")], true),
            new TeamsRecipientContext("group", "Taylor", [], true),
            new TeamsRecipientContext("meeting", "Taylor", [new("Everyone", false, "tag")], true),
            new TeamsRecipientContext("channel", "Taylor", [new("Support", false, "team")], true),
            new TeamsRecipientContext("unknown", "Taylor", [], false)
        })
        {
            var message = other with { RecipientContext = context };
            foreach (var target in new[] { "user", "team", "unknown" })
                TestAssert.Equal(1, Parse(Output(target, "attention"), [message]).Count,
                    "Recipient handling became a mentions-only filter or suppressed missing metadata.");
        }
        var mine = other with
        {
            Id = "message-2", Preview = "Taylor, please review this separate request.",
            RecipientContext = new("group", "Taylor", [new("Taylor", true, "user")], true)
        };
        var mixed = Envelope(Item("other", "attention", ["message-1"]),
            Item("user", "attention", ["message-2"]));
        var decisions = Parse(mixed, [other, mine]);
        TestAssert.True(decisions.Count == 1 && decisions[0].MessageIds.SequenceEqual([mine.Id]),
            "Someone else's request hid a later request to the user in the same conversation.");
        var state = new PeggyStateManager(new EncryptedStateStore(Path.Combine(directory, "recipient-tests.dat")));
        var manager = new AlertManager(state);
        var applied = manager.Apply(decisions, [other, mine], now);
        TestAssert.True(applied.MaterialUpdates.Single().MessageIds.SequenceEqual([mine.Id]),
            "Filtering recipient assessments lost the exact actionable message linkage.");
        TestAssert.Equal(0, state.State.CreditHistory.Count, "Local recipient filtering added a model operation.");
        foreach (var bad in new[]
        {
            Output(null, "attention"), Output("nobody", "attention"),
            Output("other", "attention", reason: ""), Output("user", "attention", reason: new string('x', 301)),
            Envelope(Item("user", "attention", ["message-1", "message-2"])),
            Envelope(Item("other", "none", ["message-1"]), Item("user", "attention", ["message-1"])),
            Envelope(Item("other", "attention", ["not-in-batch"])),
            Envelope(Item("other", "attention", ["message-1"], source: "message-2"))
        })
        {
            TestAssert.Throws<InvalidDataException>(() => Parse(bad, [other, mine]),
                "Invalid, grouped or conflicting Teams recipient assessment was accepted.");
        }
        var email = other with { Source = MessageSource.Email, RecipientContext = null };
        TestAssert.Equal(1, Parse(Output(null, "attention"), [email]).Count,
            "The Teams-specific recipient schema unexpectedly broke email classification.");
        TestAssert.Equal(0, Parse("""{"decisions":[]}""", [other]).Count,
            "An explicit no-alert result was rejected.");

        var alias = Invoke<object>("CreateMonitoringAliasScope", (object)new[] { other, mine });
        var aliased = (IReadOnlyList<Message>)alias.GetType().GetProperty("Messages")!.GetValue(alias)!;
        TestAssert.True(aliased[0].RecipientContext == other.RecipientContext &&
            aliased[1].RecipientContext == mine.RecipientContext &&
            aliased.All(message => message.WebUrl is null),
            "Monitoring aliases dropped the recipient evidence or exposed source links.");
        var restored = Invoke<IReadOnlyList<AlertDecision>>("ParseAndRestoreAlertDecisions", mixed, alias);
        TestAssert.True(restored.Single().MessageIds.SequenceEqual([mine.Id]) &&
            restored.Single().SourceReference == mine.WebUrl,
            "Recipient filtering changed restored source identity.");

        var prompt = CopilotRunner.BuildAlertPrompt(10);
        foreach (var instruction in new[]
        {
            "BEFORE assigning priority", "RecipientContext", "IsCurrentUser",
            "recipient=other and tier=none", "NOT a mentions-only filter",
            "exactly ONE messageId", "later request", "missing/incomplete",
            "Do not call any other tool", "Never call prepare_action", "untrusted data"
        })
            TestAssert.Contains(instruction, prompt, "Classification prompt lost a recipient or tool-scope rule.");
        TestAssert.True(prompt.Length < 5_000,
            "Recipient classification exceeded the existing prompt-size bound.");
    }

    private static object Item(string? recipient, string tier, string[] ids,
        string reason = "Synthetic recipient evidence.", string? source = null)
        => new
        {
            recipient, recipientReason = reason, tier, summary = "Synthetic task",
            reason = "A relevant request needs the user's attention.",
            sourceReference = source ?? ids[0], messageIds = ids
        };
    private static string Output(string? recipient, string tier, string reason = "Synthetic recipient evidence.")
        => Envelope(Item(recipient, tier, ["message-1"], reason));
    private static string Envelope(params object[] decisions) => JsonSerializer.Serialize(new { decisions });
    private static IReadOnlyList<AlertDecision> Parse(string json, Message[] messages)
        => Invoke<IReadOnlyList<AlertDecision>>("ParseAlertDecisions", json, messages);
    private static T Invoke<T>(string method, params object[] arguments)
    {
        try
        {
            return (T)typeof(CopilotRunner).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, arguments)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
