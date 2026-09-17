using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Peggy.Core;
using Peggy.Tray;

namespace Peggy.Core.Tests;

internal static class SettingsWindowCatalogSmokeTests
{
    internal static void RunOnStaThread()
    {
        TestModes();
        TestReopenAndRefresh();
        TestFailureRetryAndClose();
        TestWholeTeamExclusionsPreserveFutureChannels();
    }

    private static PeggySettings SelectedSettings() => new()
    {
        TeamsScope = TeamsMonitoringScope.ChatsAndSelectedChannels,
        OutlookScope = OutlookMonitoringScope.InboxAndSelectedFolders,
        AdditionalOutlookFolders = ["Inbox/Unavailable"],
        IncludeActiveCasesFolder = true,
        TeamsTeamWatchlist =
        [
            new() { TeamId = "missing-team", DisplayName = "Retained team",
                ExcludedChannelIds = ["excluded"] }
        ],
        TeamsChannelWatchlist =
        [
            new() { TeamId = "missing", ChannelId = "channel", DisplayName = "Retained channel" }
        ]
    };

    private static void TestModes()
    {
        var defaults = new SettingsWindow(new PeggySettings(), new MockMessageProvider());
        TestAssert.True(
            Invoke<TeamsMonitoringScope>(defaults, "GetSelectedTeamsScope") ==
                TeamsMonitoringScope.ChatsOnly &&
            Invoke<OutlookMonitoringScope>(defaults, "GetSelectedOutlookScope") ==
                OutlookMonitoringScope.InboxOnly &&
            Find<StackPanel>(defaults, "TeamsSelectionPanel").Visibility == Visibility.Collapsed &&
            Find<StackPanel>(defaults, "OutlookSelectionPanel").Visibility == Visibility.Collapsed &&
            defaults.FindName("MonitorTeamsChatsCheck") is null &&
            defaults.FindName("AllInboxFoldersCheck") is null &&
            Find<TextBlock>(defaults, "TeamsScopeDescription").Text.Contains("excluding meeting chats"),
            "Fresh defaults or replacement of the legacy hidden controls regressed.");
        defaults.Close();

        var window = new SettingsWindow(SelectedSettings(), new MockMessageProvider());
        var teams = Find<ComboBox>(window, "TeamsScopeCombo");
        var outlook = Find<ComboBox>(window, "OutlookScopeCombo");
        TestAssert.True(teams.Items.Cast<ComboBoxItem>().Select(item => item.Content?.ToString())
            .SequenceEqual(["Chats only", "Chats + selected channels", "Selected channels only",
                "All Teams activity", "Off"]) &&
            outlook.Items.Count == 5, "The five explicit monitoring choices changed.");
        foreach (var scope in Enum.GetValues<TeamsMonitoringScope>())
        {
            Select(teams, scope.ToString());
            var show = scope is TeamsMonitoringScope.AllActivity or
                TeamsMonitoringScope.ChatsAndSelectedChannels or
                TeamsMonitoringScope.SelectedTeamsAndChannels;
            TestAssert.True(
                Find<StackPanel>(window, "TeamsSelectionPanel").Visibility ==
                    (show ? Visibility.Visible : Visibility.Collapsed) &&
                Invoke<TeamsMonitoringScope>(window, "GetSelectedTeamsScope") == scope &&
                Invoke<List<TeamsTeamWatch>>(window, "GetSavedTeamWatchlist") is
                    [{ ExcludedChannelIds: ["excluded"] }] &&
                Invoke<List<TeamsChannelWatch>>(window, "GetSavedWatchlist").Count == 1,
                "Switching Teams modes lost retained sources or showed an irrelevant picker.");
        }
        foreach (var scope in Enum.GetValues<OutlookMonitoringScope>())
        {
            Select(outlook, scope.ToString());
            var show = scope is OutlookMonitoringScope.InboxAndSelectedFolders or
                OutlookMonitoringScope.SelectedFoldersOnly;
            TestAssert.True(
                Find<StackPanel>(window, "OutlookSelectionPanel").Visibility ==
                    (show ? Visibility.Visible : Visibility.Collapsed) &&
                Invoke<OutlookMonitoringScope>(window, "GetSelectedOutlookScope") == scope &&
                Invoke<List<string>>(window, "GetSavedAdditionalOutlookFolders") is ["Inbox/Unavailable"] &&
                Invoke<bool>(window, "IsOutlookFolderIncluded", PeggyDefaults.ActiveCasesFolder),
                "Switching Outlook modes lost retained or legacy Active selections.");
        }
        Select(teams, "Off");
        Select(outlook, "Off");
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(new Action(() =>
            Invoke<object?>(window, "Save_Click", window, new RoutedEventArgs())));
        TestAssert.True(window.ShowDialog() == true && window.SavedSettings is
            {
                TeamsScope: TeamsMonitoringScope.Off,
                OutlookScope: OutlookMonitoringScope.Off,
                MonitorTeamsChatsInSelectedScope: false,
                IncludeActiveCasesFolder: true,
                AdditionalOutlookFolders: ["Inbox/Unavailable"]
            } && window.SavedSettings.TeamsTeamWatchlist.Count == 1,
            "Saving Off discarded source selections or retained a hidden chat override.");

        var allInbox = new SettingsWindow(new PeggySettings
        {
            OutlookScope = OutlookMonitoringScope.InboxAndAllSubfolders,
            AdditionalOutlookFolders = ["Inbox"]
        }, new MockMessageProvider());
        Select(Find<ComboBox>(allInbox, "OutlookScopeCombo"), "SelectedFoldersOnly");
        TestAssert.True(
            Invoke<List<string>>(allInbox, "GetSavedAdditionalOutlookFolders") is ["Inbox"] &&
            !Invoke<bool>(allInbox, "IsOutlookFolderIncluded", "Inbox/New folder"),
            "The legacy Inbox selection was erased or activated selected-only checkboxes.");
        allInbox.Close();
    }

    private static void TestReopenAndRefresh()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new SettingsCatalogCache(() => now);
        var provider = new MockMessageProvider();
        var first = NewWindow(provider, cache);
        Wait(Invoke<Task>(first, "LoadTeamsAsync"));
        Wait(Invoke<Task>(first, "LoadOutlookFoldersAsync"));
        var team = Find<StackPanel>(first, "TeamsPanel").Children.OfType<Expander>().First();
        var folder = Find<StackPanel>(first, "OutlookFoldersPanel").Children.OfType<Expander>()
            .First(node => ((OutlookFolderInfo)node.Tag).HasChildren);
        Wait(Invoke<Task>(first, "LoadTeamChannelsAsync", team.Tag));
        Wait(Invoke<Task>(first, "LoadOutlookChildFoldersAsync", folder.Tag));
        first.Close();

        var reopenedProvider = new MockMessageProvider();
        var reopened = NewWindow(reopenedProvider, cache);
        TestAssert.True(Find<StackPanel>(reopened, "TeamsPanel").Children.Count > 0 &&
            Find<StackPanel>(reopened, "OutlookFoldersPanel").Children.Count > 0 &&
            reopenedProvider.ListJoinedTeamsCallCount == 0 &&
            reopenedProvider.ListOutlookFolderCallCount == 0,
            "Reopening Settings did not immediately display account-cached metadata.");
        Wait(Invoke<Task>(reopened, "LoadTeamsAsync"));
        Wait(Invoke<Task>(reopened, "LoadOutlookFoldersAsync"));
        var sameTeam = Find<StackPanel>(reopened, "TeamsPanel").Children.OfType<Expander>()
            .Single(node => ((TeamsTeamInfo)node.Tag).TeamId == ((TeamsTeamInfo)team.Tag).TeamId);
        var sameFolder = Find<StackPanel>(reopened, "OutlookFoldersPanel").Children.OfType<Expander>()
            .Single(node => ((OutlookFolderInfo)node.Tag).FolderId == ((OutlookFolderInfo)folder.Tag).FolderId);
        Wait(Invoke<Task>(reopened, "LoadTeamChannelsAsync", sameTeam.Tag));
        Wait(Invoke<Task>(reopened, "LoadOutlookChildFoldersAsync", sameFolder.Tag));
        TestAssert.True(reopenedProvider.ListJoinedTeamsCallCount == 0 &&
            reopenedProvider.ListTeamChannelsCallCount == 0 &&
            reopenedProvider.ListOutlookFolderCallCount == 0,
            "Fresh root or lazy child metadata was needlessly fetched on reopen.");
        sameTeam.IsExpanded = true;
        sameFolder.IsExpanded = true;
        now += TimeSpan.FromMinutes(31);
        Wait(Invoke<Task>(reopened, "LoadVisibleCatalogsAsync"));
        TestAssert.True(reopenedProvider.ListJoinedTeamsCallCount == 1 &&
            reopenedProvider.ListTeamChannelsCallCount == 1 &&
            reopenedProvider.ListOutlookRootFolderCallCount == 1 &&
            reopenedProvider.ListOutlookChildFolderCallCount == 1 &&
            sameTeam.IsExpanded && sameFolder.IsExpanded &&
            ReferenceEquals(sameTeam, Find<StackPanel>(reopened, "TeamsPanel")
                .Children.OfType<Expander>().First(node => node.Tag.Equals(sameTeam.Tag))),
            "Stale refresh reset expansions or did not refresh expanded lazy children.");
        Wait(Invoke<Task>(reopened, "RefreshTeamsAsync", true));
        Wait(Invoke<Task>(reopened, "RefreshOutlookFoldersAsync", true));
        TestAssert.True(reopenedProvider.ListJoinedTeamsCallCount == 2 &&
            reopenedProvider.ListOutlookRootFolderCallCount == 2,
            "Top-level forced refresh did not fetch fresh metadata.");
        reopened.Close();
    }

    private static void TestFailureRetryAndClose()
    {
        var proxy = DispatchProxy.Create<IMessageProvider, SettingsCatalogProviderProxy>();
        var control = (SettingsCatalogProviderProxy)proxy;
        var now = DateTimeOffset.UtcNow;
        var cache = new SettingsCatalogCache(() => now);
        var window = NewWindow(proxy, cache);
        control.Teams = _ => throw new InvalidOperationException("offline");
        control.Folders = (_, _) => throw new InvalidOperationException("folders offline");
        Wait(Invoke<Task>(window, "LoadTeamsAsync"));
        Wait(Invoke<Task>(window, "LoadOutlookFoldersAsync"));
        TestAssert.True(Find<Button>(window, "RefreshTeamsButton").Content?.ToString() == "Retry" &&
            Find<Button>(window, "RefreshOutlookFoldersButton").Content?.ToString() == "Retry" &&
            Find<StackPanel>(window, "TeamsPanel").Children.Count == 0,
            "Root failures were treated as successful emptiness or offered no Retry.");
        control.Teams = null;
        control.Folders = null;
        Wait(Invoke<Task>(window, "RefreshTeamsAsync", true));
        Wait(Invoke<Task>(window, "RefreshOutlookFoldersAsync", true));
        var oldTeam = Find<StackPanel>(window, "TeamsPanel").Children.OfType<Expander>().First();
        var oldFolder = Find<StackPanel>(window, "OutlookFoldersPanel").Children.OfType<Expander>().First();
        Wait(Invoke<Task>(window, "LoadTeamChannelsAsync", oldTeam.Tag));
        Wait(Invoke<Task>(window, "LoadOutlookChildFoldersAsync", oldFolder.Tag));
        oldTeam.IsExpanded = true;
        control.Channels = (_, _, _) => throw new InvalidOperationException("channel denied");
        control.Folders = (_, _) => throw new InvalidOperationException("folder denied");
        Wait(Invoke<Task>(window, "RefreshTeamChannelsAsync", oldTeam.Tag, true));
        Wait(Invoke<Task>(window, "RefreshOutlookChildrenAsync", oldFolder.Tag, true));
        TestAssert.True(Descendants<CheckBox>(oldTeam).Any(check => check.Tag is null) &&
            Descendants<TextBlock>(oldTeam).Any(text => text.Text.Contains("Showing cached values")) &&
            Descendants<Button>(oldTeam).Any(button => button.Content?.ToString() == "Retry") &&
            Descendants<TextBlock>(oldFolder).Any(text => text.Text.Contains("folder denied")) &&
            Invoke<List<TeamsChannelWatch>>(window, "GetSavedWatchlist").Count == 1,
            "Child refresh failure erased cached choices or failed to expose Retry.");

        var delayed = new TaskCompletionSource<IReadOnlyList<TeamsTeamInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        control.Teams = _ => delayed.Task;
        now += TimeSpan.FromMinutes(31);
        var background = Invoke<Task>(window, "LoadTeamsAsync");
        TestAssert.True(!background.IsCompleted && oldTeam.IsExpanded &&
            ReferenceEquals(oldTeam, Find<StackPanel>(window, "TeamsPanel").Children[0]) &&
            Find<TextBlock>(window, "ChannelStatus").Text.Contains("background") &&
            !Find<Button>(window, "RefreshTeamsButton").IsEnabled,
            "Background refresh blanked existing nodes or reset expansion.");
        var oldText = Find<TextBlock>(window, "ChannelStatus").Text;
        window.Close();
        Wait(background);
        delayed.SetResult([new("late-account-team", "Do not display")]);
        Pump();
        TestAssert.True(Find<TextBlock>(window, "ChannelStatus").Text == oldText &&
            cache.Read<TeamsTeamInfo>(new("teams")).Values!
                .All(team => team.TeamId != "late-account-team"),
            "Closing Settings allowed a late metadata result to publish.");

        control.Teams = _ => Task.FromResult<IReadOnlyList<TeamsTeamInfo>>([]);
        control.Folders = (_, _) => Task.FromResult<IReadOnlyList<OutlookFolderInfo>>([]);
        var empty = NewWindow(proxy, cache);
        Wait(Invoke<Task>(empty, "RefreshTeamsAsync", true));
        Wait(Invoke<Task>(empty, "RefreshOutlookFoldersAsync", true));
        TestAssert.True(Find<StackPanel>(empty, "TeamsPanel").Children.Count == 0 &&
            Find<StackPanel>(empty, "OutlookFoldersPanel").Children.Count == 0 &&
            Find<TextBlock>(empty, "ChannelStatus").Text.Contains("No joined teams were returned") &&
            Find<TextBlock>(empty, "OutlookFolderStatus").Text.Contains("Last refreshed") &&
            Find<Button>(empty, "RefreshOutlookFoldersButton").Content?.ToString() == "Refresh" &&
            Invoke<List<TeamsChannelWatch>>(empty, "GetSavedWatchlist").Count == 1 &&
            Invoke<List<string>>(empty, "GetSavedAdditionalOutlookFolders").Count == 1,
            "Successful empty refresh retained a stale error or removed saved selections.");
        empty.Close();
    }

    private static SettingsWindow NewWindow(IMessageProvider provider, SettingsCatalogCache cache)
        => new(SelectedSettings(), provider, "settings-tests@example.invalid", null, null, cache);

    private static void TestWholeTeamExclusionsPreserveFutureChannels()
    {
        foreach (var staleWithFailedRefresh in new[] { false, true })
        {
            var now = DateTimeOffset.UtcNow;
            var cache = new SettingsCatalogCache(() => now);
            var team = new TeamsTeamInfo("catalog-team", "Catalog team");
            var channelA = new TeamsChannelInfo(team.TeamId, team.TeamName, "A", "Channel A");
            var channelB = new TeamsChannelInfo(team.TeamId, team.TeamName, "B", "Channel B");
            Wait(cache.RefreshAsync(new SettingsCatalogKey("teams"),
                _ => Task.FromResult<IReadOnlyList<TeamsTeamInfo>>([team])));
            Wait(cache.RefreshAsync(new SettingsCatalogKey("channels", team.TeamId),
                _ => Task.FromResult<IReadOnlyList<TeamsChannelInfo>>([channelA])));
            if (staleWithFailedRefresh)
            {
                now += TimeSpan.FromMinutes(31);
            }
            var provider = DispatchProxy.Create<IMessageProvider, SettingsCatalogProviderProxy>();
            var control = (SettingsCatalogProviderProxy)provider;
            var window = new SettingsWindow(new PeggySettings
            {
                TeamsScope = TeamsMonitoringScope.SelectedTeamsAndChannels,
                TeamsTeamWatchlist =
                [
                    new() { TeamId = team.TeamId, DisplayName = team.TeamName }
                ]
            }, provider, "team-exclusions@example.invalid", null, null, cache);
            var node = Find<StackPanel>(window, "TeamsPanel").Children.OfType<Expander>().Single();
            var teamCheck = Descendants<CheckBox>(node).Single(check => check.Tag is TeamsTeamInfo);
            if (staleWithFailedRefresh)
            {
                control.Channels = (_, _, _) => throw new InvalidOperationException("catalog unavailable");
                Wait(Invoke<Task>(window, "RefreshTeamChannelsAsync", team, true));
                TestAssert.True(Descendants<TextBlock>(node)
                    .Any(text => text.Text.Contains("Showing cached values")),
                    "The exclusion regression did not exercise a failed stale-cache refresh.");
            }
            Descendants<CheckBox>(node).Single(check =>
                check.Content?.ToString() == channelA.ChannelName).IsChecked = false;
            TestAssert.True(
                Invoke<List<TeamsTeamWatch>>(window, "GetSavedTeamWatchlist") is
                    [{ TeamId: "catalog-team", ExcludedChannelIds: ["A"] }] &&
                teamCheck.IsChecked is null &&
                Invoke<bool>(window, "IsChannelSelected", team.TeamId, channelB.ChannelId) &&
                Invoke<bool>(window, "IsChannelSelected", team.TeamId, "future-channel"),
                "Excluding the last known channel removed the whole-team watch or its future channels.");

            control.Channels = (_, _, _) =>
                Task.FromResult<IReadOnlyList<TeamsChannelInfo>>([channelA, channelB]);
            Wait(Invoke<Task>(window, "RefreshTeamChannelsAsync", team, true));
            var checkB = Descendants<CheckBox>(node).Single(check =>
                check.Content?.ToString() == channelB.ChannelName);
            TestAssert.True(checkB.IsChecked == true && teamCheck.IsChecked is null,
                "A newly discovered channel was not included in the retained whole-team watch.");
            checkB.IsChecked = false;
            TestAssert.True(
                Invoke<List<TeamsTeamWatch>>(window, "GetSavedTeamWatchlist") is
                    [{ ExcludedChannelIds: ["A", "B"] }] &&
                teamCheck.IsChecked is null &&
                Invoke<bool>(window, "IsChannelSelected", team.TeamId, "future-channel"),
                "Excluding all freshly loaded channels canceled the future-channel subscription.");
            Invoke<object?>(window, "DeselectTeam", team.TeamId);
            TestAssert.True(
                Invoke<List<TeamsTeamWatch>>(window, "GetSavedTeamWatchlist").Count == 0 &&
                teamCheck.IsChecked == false &&
                !Invoke<bool>(window, "IsChannelSelected", team.TeamId, "future-channel"),
                "Explicit team deselection did not remove the subscription.");
            window.Close();
        }
    }

    private static T Find<T>(SettingsWindow window, string name) where T : class
        => (T)window.FindName(name);

    private static void Select(ComboBox combo, string tag)
        => combo.SelectedItem = combo.Items.Cast<ComboBoxItem>()
            .Single(item => item.Tag?.ToString() == tag);

    private static T Invoke<T>(object target, string name, params object[] args)
    {
        try
        {
            return (T)target.GetType().GetMethod(name,
                BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) { yield return match; }
            foreach (var nested in Descendants<T>(child)) { yield return nested; }
        }
    }

    private static void Wait(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Pump(); }
        if (!task.IsCompleted) { throw new TimeoutException("Settings metadata test timed out."); }
        task.GetAwaiter().GetResult();
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Thread.Sleep(1);
    }
}

internal class SettingsCatalogProviderProxy : DispatchProxy
{
    private readonly MockMessageProvider _fallback = new();
    public SettingsCatalogProviderProxy() { }
    public Func<CancellationToken, Task<IReadOnlyList<TeamsTeamInfo>>>? Teams { get; set; }
    public Func<string, string, CancellationToken, Task<IReadOnlyList<TeamsChannelInfo>>>? Channels { get; set; }
    public Func<OutlookFolderInfo?, CancellationToken, Task<IReadOnlyList<OutlookFolderInfo>>>? Folders { get; set; }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
        => method!.Name switch
        {
            nameof(IMessageProvider.ListJoinedTeamsAsync) when Teams is not null =>
                Teams((CancellationToken)args![0]!),
            nameof(IMessageProvider.ListTeamChannelsAsync) when Channels is not null =>
                Channels((string)args![0]!, (string)args[1]!, (CancellationToken)args[2]!),
            nameof(IMessageProvider.ListOutlookChildFoldersAsync) when Folders is not null =>
                Folders((OutlookFolderInfo?)args![0], (CancellationToken)args[1]!),
            _ => method.Invoke(_fallback, args)
        };
}
