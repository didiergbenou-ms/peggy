using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Peggy.Core;

namespace Peggy.Tray;

public partial class SettingsWindow : Window
{
    private readonly IMessageProvider _provider;
    private readonly Dictionary<string, TeamsChannelWatch> _selectedChannels;
    private readonly Dictionary<string, CheckBox> _channelChecks =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, StackPanel> _teamPanels =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<TeamsChannelInfo>>
        _loadedChannelsByTeam = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _teamNames =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadedTeamIds =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadingTeamIds =
        new(StringComparer.Ordinal);
    private readonly string _pinnedAccount;
    private readonly CancellationTokenSource _loadCancellation = new();
    private bool _updatingSelectionControls;
    private bool _closed;

    public SettingsWindow(
        PeggySettings settings,
        IMessageProvider provider,
        string? pinnedAccount = null)
    {
        InitializeComponent();
        _provider = provider;
        _pinnedAccount = pinnedAccount?.Trim() ?? string.Empty;
        AccountStatus.Text = _pinnedAccount.Length == 0
            ? "Microsoft 365 is not connected."
            : $"Connected as {_pinnedAccount}";
        ScheduleEnabledCheck.IsChecked =
            settings.MonitoringScheduleEnabled;
        WakeTimeText.Text = MonitoringSchedule.FormatMinute(
            settings.MonitoringWakeMinute);
        SleepTimeText.Text = MonitoringSchedule.FormatMinute(
            settings.MonitoringSleepMinute);
        UpdateScheduleControls();
        var configuredModel = Environment.GetEnvironmentVariable(
            "PEGGY_COPILOT_MODEL");
        ModelText.Text = string.IsNullOrWhiteSpace(configuredModel)
            ? settings.CopilotModel
            : configuredModel.Trim();
        if (!string.IsNullOrWhiteSpace(configuredModel))
        {
            ModelText.IsReadOnly = true;
            ModelText.ToolTip =
                "Controlled by the PEGGY_COPILOT_MODEL environment variable.";
        }
        FoldersText.Text = string.Join(
            Environment.NewLine,
            settings.AdditionalOutlookFolders);
        StartupCheck.IsChecked = settings.StartAtSignIn;
        PausedCheck.IsChecked = settings.MonitoringPaused;
        SpeechEnabledCheck.IsChecked = settings.SpeechEnabled;
        SpeechModelsText.Text = string.IsNullOrWhiteSpace(
            settings.SpeechModelDirectory)
            ? PeggyDefaults.SpeechModelDirectory
            : settings.SpeechModelDirectory;
        SelectComboItem(SpeechThreads, settings.SpeechThreads.ToString());
        SelectComboItem(SpeechVoice, settings.SpeechVoiceId.ToString());
        SelectComboItem(
            SpeechInputGain,
            settings.SpeechInputGain.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        _selectedChannels = settings.TeamsChannelWatchlist
            .GroupBy(
                item => ChannelKey(item.TeamId, item.ChannelId),
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => CloneWatch(group.First()),
                StringComparer.Ordinal);
        RenderSelectedChannels();
        Loaded += async (_, _) => await LoadTeamsAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _loadCancellation.Cancel();
            _loadCancellation.Dispose();
        };
    }

    public PeggySettings? SavedSettings { get; private set; }

    private async System.Threading.Tasks.Task LoadTeamsAsync()
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                _loadCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var teams = await _provider.ListJoinedTeamsAsync(timeout.Token);
            if (_closed)
            {
                return;
            }

            TeamsPanel.Children.Clear();
            _teamPanels.Clear();
            _loadedChannelsByTeam.Clear();
            _teamNames.Clear();
            _loadedTeamIds.Clear();
            _loadingTeamIds.Clear();
            _channelChecks.Clear();
            foreach (var team in teams)
            {
                _teamNames[team.TeamId] = team.TeamName;
                var channelPanel = new StackPanel
                {
                    Margin = new Thickness(12, 3, 0, 7)
                };
                channelPanel.Children.Add(new TextBlock
                {
                    Text = "Expand this team to load its channels.",
                    Foreground = System.Windows.Media.Brushes.DimGray
                });
                _teamPanels[team.TeamId] = channelPanel;
                var expander = new Expander
                {
                    Header = team.TeamName,
                    Tag = team,
                    Content = channelPanel,
                    Margin = new Thickness(0, 1, 0, 1)
                };
                expander.Expanded += async (_, _) =>
                    await LoadTeamChannelsAsync(team);
                TeamsPanel.Children.Add(expander);
            }
            RenderSelectedChannels();
            ChannelStatus.Text = teams.Count == 0
                ? "No joined Teams were returned."
                : $"{teams.Count} joined teams loaded. Expand only the teams " +
                  "whose channels you want to configure. Selected channels " +
                  "receive full activity monitoring.";
        }
        catch (OperationCanceledException) when (_closed)
        {
        }
        catch (OperationCanceledException)
        {
            ChannelStatus.Text =
                "Joined teams took too long to load. Close and reopen Settings " +
                "to retry.";
        }
        catch (Exception ex)
        {
            ChannelStatus.Text =
                $"Joined teams could not be loaded: {ex.Message}";
        }
    }

    private async System.Threading.Tasks.Task LoadTeamChannelsAsync(
        TeamsTeamInfo team)
    {
        if (_closed ||
            _loadedTeamIds.Contains(team.TeamId) ||
            !_loadingTeamIds.Add(team.TeamId) ||
            !_teamPanels.TryGetValue(team.TeamId, out var channelPanel))
        {
            return;
        }

        ShowTeamMessage(channelPanel, "Loading channels...", null);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                _loadCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var channels = await _provider.ListTeamChannelsAsync(
                team.TeamId,
                team.TeamName,
                timeout.Token);
            if (_closed)
            {
                return;
            }

            channelPanel.Children.Clear();
            _loadedChannelsByTeam[team.TeamId] = channels;
            if (channels.Count == 0)
            {
                channelPanel.Children.Add(new TextBlock
                {
                    Text = "No channels were returned for this team.",
                    Foreground = System.Windows.Media.Brushes.DimGray
                });
            }
            if (channels.Count > 0)
            {
                AddTeamSelectionControls(channelPanel, team);
            }
            foreach (var channel in channels)
            {
                AddChannelCheckBox(channelPanel, channel);
            }
            var availableKeys = channels
                .Select(channel => ChannelKey(
                    channel.TeamId,
                    channel.ChannelId))
                .ToHashSet(StringComparer.Ordinal);
            if (_selectedChannels.Values.Any(item =>
                    string.Equals(
                        item.TeamId,
                        team.TeamId,
                        StringComparison.Ordinal) &&
                    !availableKeys.Contains(
                        ChannelKey(item.TeamId, item.ChannelId))))
            {
                channelPanel.Children.Add(new TextBlock
                {
                    Text = "A saved channel from this team is not currently " +
                           "available. It remains selected above until removed.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = System.Windows.Media.Brushes.DimGray,
                    FontSize = 11,
                    Margin = new Thickness(0, 6, 0, 0)
                });
            }
            _loadedTeamIds.Add(team.TeamId);
        }
        catch (OperationCanceledException) when (_closed)
        {
        }
        catch (OperationCanceledException)
        {
            ShowTeamMessage(
                channelPanel,
                "Channels took too long to load.",
                team);
        }
        catch (Exception ex)
        {
            ShowTeamMessage(
                channelPanel,
                $"Channels could not be loaded: {ex.Message}",
                team);
        }
        finally
        {
            _loadingTeamIds.Remove(team.TeamId);
        }
    }

    private void AddChannelCheckBox(
        Panel channelPanel,
        TeamsChannelInfo channel)
    {
        var key = ChannelKey(channel.TeamId, channel.ChannelId);
        var checkBox = new CheckBox
        {
            Content = channel.ChannelName,
            IsChecked = _selectedChannels.ContainsKey(key),
            Margin = new Thickness(0, 2, 0, 2)
        };
        checkBox.Checked += (_, _) =>
        {
            if (_updatingSelectionControls)
            {
                return;
            }
            _selectedChannels[key] = new TeamsChannelWatch
            {
                TeamId = channel.TeamId,
                ChannelId = channel.ChannelId,
                DisplayName = $"{channel.TeamName} / {channel.ChannelName}"
            };
            RenderSelectedChannels();
        };
        checkBox.Unchecked += (_, _) =>
        {
            if (_updatingSelectionControls)
            {
                return;
            }
            _selectedChannels.Remove(key);
            RenderSelectedChannels();
        };
        _channelChecks[key] = checkBox;
        channelPanel.Children.Add(checkBox);
    }

    private void AddTeamSelectionControls(
        Panel channelPanel,
        TeamsTeamInfo team)
    {
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 5)
        };
        var selectAll = new Button
        {
            Content = "Select all channels",
            Tag = team,
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, 0, 6, 0)
        };
        selectAll.Click += SelectAllTeamChannels_Click;
        actions.Children.Add(selectAll);
        var clear = new Button
        {
            Content = "Deselect team",
            Tag = team.TeamId,
            Padding = new Thickness(7, 3, 7, 3)
        };
        clear.Click += DeselectTeam_Click;
        actions.Children.Add(clear);
        channelPanel.Children.Add(actions);
    }

    private void SelectAllTeamChannels_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TeamsTeamInfo team } ||
            !_loadedChannelsByTeam.TryGetValue(
                team.TeamId,
                out var channels))
        {
            return;
        }

        _updatingSelectionControls = true;
        try
        {
            foreach (var channel in channels)
            {
                var key = ChannelKey(channel.TeamId, channel.ChannelId);
                _selectedChannels[key] = new TeamsChannelWatch
                {
                    TeamId = channel.TeamId,
                    ChannelId = channel.ChannelId,
                    DisplayName =
                        $"{channel.TeamName} / {channel.ChannelName}"
                };
                if (_channelChecks.TryGetValue(key, out var checkBox))
                {
                    checkBox.IsChecked = true;
                }
            }
        }
        finally
        {
            _updatingSelectionControls = false;
        }
        RenderSelectedChannels();
    }

    private void DeselectTeam_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Button { Tag: string teamId })
        {
            DeselectTeam(teamId);
        }
    }

    private void DeselectTeam(string teamId)
    {
        var keys = _selectedChannels
            .Where(pair => string.Equals(
                pair.Value.TeamId,
                teamId,
                StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToList();
        _updatingSelectionControls = true;
        try
        {
            foreach (var key in keys)
            {
                _selectedChannels.Remove(key);
                if (_channelChecks.TryGetValue(key, out var checkBox))
                {
                    checkBox.IsChecked = false;
                }
            }
        }
        finally
        {
            _updatingSelectionControls = false;
        }
        RenderSelectedChannels();
    }

    private async void RetryTeamChannels_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Button { Tag: TeamsTeamInfo team })
        {
            await LoadTeamChannelsAsync(team);
        }
    }

    private void ShowTeamMessage(
        Panel panel,
        string message,
        TeamsTeamInfo? retryTeam)
    {
        panel.Children.Clear();
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DimGray
        });
        if (retryTeam is null)
        {
            return;
        }

        var retry = new Button
        {
            Content = "Retry",
            Tag = retryTeam,
            Width = 72,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 6, 0, 0)
        };
        retry.Click += RetryTeamChannels_Click;
        panel.Children.Add(retry);
    }

    private void RenderSelectedChannels()
    {
        SelectedChannelsPanel.Children.Clear();
        var selectedTeams = _selectedChannels.Values
            .Select(item => item.TeamId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        SelectedChannelStatus.Text =
            $"Selected monitoring: {selectedTeams} " +
            $"{(selectedTeams == 1 ? "team" : "teams")}, " +
            $"{_selectedChannels.Count} " +
            $"{(_selectedChannels.Count == 1 ? "channel" : "channels")}";
        if (_selectedChannels.Count == 0)
        {
            SelectedChannelsPanel.Children.Add(new TextBlock
            {
                Text = "None selected. Mentions and replies in participated " +
                       "threads are still monitored across Teams.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.DimGray,
                FontSize = 11
            });
            return;
        }

        foreach (var teamGroup in _selectedChannels
                     .GroupBy(
                         pair => pair.Value.TeamId,
                         StringComparer.Ordinal)
                     .OrderBy(
                         group => GetTeamName(group.First().Value),
                         StringComparer.OrdinalIgnoreCase))
        {
            var teamHeader = new DockPanel
            {
                Margin = new Thickness(0, 4, 0, 2)
            };
            var deselectTeam = new Button
            {
                Content = "Deselect team",
                Tag = teamGroup.Key,
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(8, 0, 0, 0)
            };
            deselectTeam.Click += DeselectTeam_Click;
            DockPanel.SetDock(deselectTeam, Dock.Right);
            teamHeader.Children.Add(deselectTeam);
            teamHeader.Children.Add(new TextBlock
            {
                Text = GetTeamName(teamGroup.First().Value),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });
            SelectedChannelsPanel.Children.Add(teamHeader);

            foreach (var pair in teamGroup.OrderBy(
                         item => item.Value.DisplayName,
                         StringComparer.OrdinalIgnoreCase))
            {
                var row = new DockPanel
                {
                    Margin = new Thickness(12, 2, 0, 2)
                };
                var remove = new Button
                {
                    Content = "Remove",
                    Tag = pair.Key,
                    MinWidth = 68,
                    Padding = new Thickness(7, 3, 7, 3),
                    Margin = new Thickness(8, 0, 0, 0)
                };
                remove.Click += RemoveSelectedChannel_Click;
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                row.Children.Add(new TextBlock
                {
                    Text = GetChannelName(pair.Value),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                });
                SelectedChannelsPanel.Children.Add(row);
            }
        }
    }

    private string GetTeamName(TeamsChannelWatch item)
    {
        if (_teamNames.TryGetValue(item.TeamId, out var teamName))
        {
            return teamName;
        }

        var separator = item.DisplayName.IndexOf(
            " / ",
            StringComparison.Ordinal);
        return separator > 0
            ? item.DisplayName[..separator]
            : item.DisplayName;
    }

    private static string GetChannelName(TeamsChannelWatch item)
    {
        var separator = item.DisplayName.IndexOf(
            " / ",
            StringComparison.Ordinal);
        return separator >= 0 &&
               separator + 3 < item.DisplayName.Length
            ? item.DisplayName[(separator + 3)..]
            : item.DisplayName;
    }

    private void RemoveSelectedChannel_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key })
        {
            return;
        }

        _selectedChannels.Remove(key);
        if (_channelChecks.TryGetValue(key, out var checkBox))
        {
            _updatingSelectionControls = true;
            try
            {
                checkBox.IsChecked = false;
            }
            finally
            {
                _updatingSelectionControls = false;
            }
        }
        RenderSelectedChannels();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var model = ModelText.Text.Trim();
        if (model.Length == 0)
        {
            MessageBox.Show(
                this,
                "Choose a Copilot model.",
                "Assistant settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (!TryParseTime(WakeTimeText.Text, out var wakeMinute) ||
            !TryParseTime(SleepTimeText.Text, out var sleepMinute))
        {
            MessageBox.Show(
                this,
                "Enter wake and sleep times in 24-hour HH:mm format.",
                "Assistant settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (wakeMinute == sleepMinute)
        {
            MessageBox.Show(
                this,
                "Wake and sleep times must be different.",
                "Assistant settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var folders = FoldersText.Text
            .Replace(';', '\n')
            .Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var watchlist = GetSavedWatchlist();
        SavedSettings = new PeggySettings
        {
            CopilotModel = model,
            AdditionalOutlookFolders = folders,
            TeamsChannelWatchlist = watchlist,
            StartAtSignIn = StartupCheck.IsChecked == true,
            MonitoringPaused = PausedCheck.IsChecked == true,
            MonitoringScheduleEnabled =
                ScheduleEnabledCheck.IsChecked == true,
            MonitoringWakeMinute = wakeMinute,
            MonitoringSleepMinute = sleepMinute,
            SpeechEnabled = SpeechEnabledCheck.IsChecked == true,
            SpeechModelDirectory = SpeechModelsText.Text.Trim(),
            SpeechThreads = GetSelectedInt(SpeechThreads, 8),
            SpeechVoiceId = GetSelectedInt(SpeechVoice, 0),
            SpeechSpeed = 1.15F,
            SpeechInputGain = GetSelectedInt(SpeechInputGain, 12),
            SpeechTuningVersion = 3
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private void SignInAgain_Click(object sender, RoutedEventArgs e)
    {
        var window = new WorkIqSignInWindow(_pinnedAccount)
        {
            Owner = this
        };
        if (window.ShowDialog() == true &&
            !string.IsNullOrWhiteSpace(window.AuthenticatedAccount))
        {
            AccountStatus.Text =
                $"Connected as {window.AuthenticatedAccount}";
        }
    }

    private void ScheduleEnabledCheck_Changed(
        object sender,
        RoutedEventArgs e)
        => UpdateScheduleControls();

    private void UpdateScheduleControls()
    {
        if (WakeTimeText is null || SleepTimeText is null)
        {
            return;
        }

        var enabled = ScheduleEnabledCheck.IsChecked == true;
        WakeTimeText.IsEnabled = enabled;
        SleepTimeText.IsEnabled = enabled;
    }

    private static bool TryParseTime(string value, out int minute)
    {
        if (TimeOnly.TryParseExact(
                value.Trim(),
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var time))
        {
            minute = (time.Hour * 60) + time.Minute;
            return true;
        }

        minute = 0;
        return false;
    }

    private List<TeamsChannelWatch> GetSavedWatchlist()
        => _selectedChannels.Values
            .OrderBy(
                item => item.DisplayName,
                StringComparer.OrdinalIgnoreCase)
            .Select(CloneWatch)
            .ToList();

    private static TeamsChannelWatch CloneWatch(TeamsChannelWatch item)
        => new()
        {
            TeamId = item.TeamId,
            ChannelId = item.ChannelId,
            DisplayName = item.DisplayName
        };

    private static string ChannelKey(string teamId, string channelId)
        => $"{teamId}\n{channelId}";

    private static void SelectComboItem(ComboBox comboBox, string value)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                string.Equals(
                    item.Tag?.ToString() ?? item.Content?.ToString(),
                    value,
                    StringComparison.Ordinal));
        comboBox.SelectedIndex = comboBox.SelectedIndex < 0
            ? 0
            : comboBox.SelectedIndex;
    }

    private static int GetSelectedInt(ComboBox comboBox, int fallback)
    {
        var item = comboBox.SelectedItem as ComboBoxItem;
        return int.TryParse(
            item?.Tag?.ToString() ?? item?.Content?.ToString(),
            out var value)
            ? value
            : fallback;
    }
}
