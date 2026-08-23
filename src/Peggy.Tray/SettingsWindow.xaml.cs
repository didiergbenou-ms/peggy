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
    private readonly HashSet<string> _selectedChannels;
    private readonly HashSet<string> _unavailableChannelKeys =
        new(StringComparer.Ordinal);
    private readonly List<TeamsChannelWatch> _originalWatchlist;
    private readonly List<(CheckBox CheckBox, TeamsChannelInfo Channel)> _channelChecks = [];
    private readonly CancellationTokenSource _loadCancellation = new();
    private bool _channelsLoaded;
    private bool _closed;

    public SettingsWindow(
        PeggySettings settings,
        IMessageProvider provider)
    {
        InitializeComponent();
        _provider = provider;
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
        _originalWatchlist = settings.TeamsChannelWatchlist
            .Select(CloneWatch)
            .ToList();
        _selectedChannels = _originalWatchlist
            .Select(item => ChannelKey(item.TeamId, item.ChannelId))
            .ToHashSet(StringComparer.Ordinal);
        Loaded += async (_, _) => await LoadChannelsAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _loadCancellation.Cancel();
            _loadCancellation.Dispose();
        };
    }

    public PeggySettings? SavedSettings { get; private set; }

    private async System.Threading.Tasks.Task LoadChannelsAsync()
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                _loadCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var channels = await _provider.ListChannelsAsync(timeout.Token);
            if (_closed)
            {
                return;
            }

            ChannelsPanel.Children.Clear();
            _channelChecks.Clear();
            foreach (var teamGroup in channels.GroupBy(
                         channel => channel.TeamName,
                         StringComparer.OrdinalIgnoreCase))
            {
                ChannelsPanel.Children.Add(new TextBlock
                {
                    Text = teamGroup.Key,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 8, 0, 3)
                });
                foreach (var channel in teamGroup)
                {
                    var checkBox = new CheckBox
                    {
                        Content = channel.ChannelName,
                        IsChecked = _selectedChannels.Contains(
                            ChannelKey(channel.TeamId, channel.ChannelId)),
                        Margin = new Thickness(12, 2, 0, 2)
                    };
                    _channelChecks.Add((checkBox, channel));
                    ChannelsPanel.Children.Add(checkBox);
                }
            }
            var availableKeys = channels
                .Select(channel => ChannelKey(
                    channel.TeamId,
                    channel.ChannelId))
                .ToHashSet(StringComparer.Ordinal);
            var unavailable = _originalWatchlist
                .Where(item => !availableKeys.Contains(
                    ChannelKey(item.TeamId, item.ChannelId)))
                .ToList();
            if (unavailable.Count > 0)
            {
                ChannelsPanel.Children.Add(new TextBlock
                {
                    Text = "Unavailable saved channels",
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 12, 0, 3)
                });
                foreach (var watched in unavailable)
                {
                    var channel = new TeamsChannelInfo(
                        watched.TeamId,
                        "Unavailable saved channels",
                        watched.ChannelId,
                        watched.DisplayName);
                    var key = ChannelKey(
                        channel.TeamId,
                        channel.ChannelId);
                    _unavailableChannelKeys.Add(key);
                    var checkBox = new CheckBox
                    {
                        Content = watched.DisplayName,
                        IsChecked = true,
                        ToolTip = "This saved channel is not in the current " +
                                  "catalog. Clear it to remove it.",
                        Margin = new Thickness(12, 2, 0, 2)
                    };
                    _channelChecks.Add((checkBox, channel));
                    ChannelsPanel.Children.Add(checkBox);
                }
            }
            _channelsLoaded = true;
            ChannelStatus.Text = channels.Count == 0
                ? "No joined channels were returned."
                : "Only selected channels have their full activity evaluated. " +
                  "Mentions and participated-thread replies remain monitored everywhere.";
        }
        catch (OperationCanceledException) when (_closed)
        {
        }
        catch (OperationCanceledException)
        {
            ChannelStatus.Text =
                "Channels took too long to load. Close and reopen Settings to retry.";
        }
        catch (Exception ex)
        {
            ChannelStatus.Text =
                $"Channels could not be loaded: {ex.Message}";
        }
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
    {
        if (!_channelsLoaded)
        {
            return _originalWatchlist
                .Select(CloneWatch)
                .ToList();
        }

        var representedKeys = _channelChecks
            .Select(item => ChannelKey(
                item.Channel.TeamId,
                item.Channel.ChannelId))
            .ToHashSet(StringComparer.Ordinal);
        return _channelChecks
            .Where(item => item.CheckBox.IsChecked == true)
            .Select(item => new TeamsChannelWatch
            {
                TeamId = item.Channel.TeamId,
                ChannelId = item.Channel.ChannelId,
                DisplayName = GetWatchDisplayName(item.Channel)
            })
            .Concat(_originalWatchlist
                .Where(item => !representedKeys.Contains(
                    ChannelKey(item.TeamId, item.ChannelId)))
                .Select(CloneWatch))
            .GroupBy(
                item => ChannelKey(item.TeamId, item.ChannelId),
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private string GetWatchDisplayName(TeamsChannelInfo channel)
    {
        var key = ChannelKey(channel.TeamId, channel.ChannelId);
        if (_unavailableChannelKeys.Contains(key))
        {
            return _originalWatchlist.First(item =>
                string.Equals(
                    ChannelKey(item.TeamId, item.ChannelId),
                    key,
                    StringComparison.Ordinal)).DisplayName;
        }

        return $"{channel.TeamName} / {channel.ChannelName}";
    }

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
