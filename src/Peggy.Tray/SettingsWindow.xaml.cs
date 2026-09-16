using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Peggy.Core;

namespace Peggy.Tray;

public partial class SettingsWindow : Window
{
    private sealed record ModelSettingOption(
        string Value,
        string DisplayName,
        bool IsAvailable = true);

    private readonly IMessageProvider _provider;
    private readonly Dictionary<string, TeamsChannelWatch> _selectedChannels;
    private readonly Dictionary<string, TeamsTeamWatch> _selectedTeams;
    private readonly Dictionary<string, CheckBox> _channelChecks =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> _teamChecks =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, StackPanel> _teamPanels =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> _outlookFolderChecks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StackPanel> _outlookFolderPanels =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<TeamsChannelInfo>>
        _loadedChannelsByTeam = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _teamNames =
        new(StringComparer.Ordinal);
    private readonly Func<
        CancellationToken,
        Task<IReadOnlyList<CopilotModelOption>>>? _loadCopilotModels;
    private readonly SpeechModelSetupService _speechModels;
    private readonly bool _speechModelDirectoryOverridden;
    private readonly HashSet<string> _loadedTeamIds =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadingTeamIds =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadedOutlookFolderIds =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadingOutlookFolderIds =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _selectedOutlookFolders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _pinnedAccount;
    private readonly string _initialModel;
    private readonly string _initialReasoningEffort;
    private readonly string _initialContextTier;
    private readonly bool _modelOverridden;
    private readonly CancellationTokenSource _loadCancellation = new();
    private bool _modelCatalogLoaded;
    private bool _loadingModels;
    private bool _loadingOutlookRoot;
    private bool _installingSpeechModels;
    private bool _outlookRootLoaded;
    private bool _updatingModelControls;
    private bool _updatingOutlookFolderControls;
    private bool _updatingSelectionControls;
    private bool _closed;

    public SettingsWindow(
        PeggySettings settings,
        IMessageProvider provider,
        string? pinnedAccount = null,
        Func<
            CancellationToken,
            Task<IReadOnlyList<CopilotModelOption>>>? loadCopilotModels = null)
    {
        InitializeComponent();
        _provider = provider;
        _loadCopilotModels = loadCopilotModels;
        _speechModels = new SpeechModelSetupService();
        _speechModelDirectoryOverridden = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("PEGGY_SPEECH_MODELS"));
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
        _modelOverridden = !string.IsNullOrWhiteSpace(configuredModel);
        _initialModel = string.IsNullOrWhiteSpace(configuredModel)
            ? (string.IsNullOrWhiteSpace(settings.CopilotModel)
                ? PeggyDefaults.CopilotModel
                : settings.CopilotModel.Trim())
            : configuredModel.Trim();
        _initialReasoningEffort =
            settings.CopilotReasoningEffort?.Trim() ?? string.Empty;
        _initialContextTier = string.Equals(
                settings.CopilotContextTier,
                PeggyDefaults.CopilotContextTierLong,
                StringComparison.OrdinalIgnoreCase)
            ? PeggyDefaults.CopilotContextTierLong
            : PeggyDefaults.CopilotContextTierDefault;
        _updatingModelControls = true;
        try
        {
            ModelCombo.ItemsSource = new List<CopilotModelOption>
            {
                new CopilotModelOption(
                    _initialModel,
                    _initialModel,
                    SupportedReasoningEfforts:
                        _initialReasoningEffort.Length == 0
                            ? []
                            : [_initialReasoningEffort],
                    SupportsLongContext:
                        _initialContextTier ==
                        PeggyDefaults.CopilotContextTierLong)
            };
            ModelCombo.SelectedValue = _initialModel;
            UpdateModelDependentControls(
                _initialReasoningEffort,
                _initialContextTier,
                preserveUnavailable: true);
        }
        finally
        {
            _updatingModelControls = false;
        }
        if (_modelOverridden)
        {
            ModelCombo.IsEnabled = false;
            ModelCombo.ToolTip =
                "Controlled by the PEGGY_COPILOT_MODEL environment variable.";
            ModelStatusText.Text = _loadCopilotModels is null
                ? "Model controlled by PEGGY_COPILOT_MODEL. Live model " +
                  "discovery is unavailable."
                : "Model controlled by PEGGY_COPILOT_MODEL. Loading its " +
                  "GitHub Copilot capabilities...";
        }
        else if (_loadCopilotModels is null)
        {
            RefreshModelsButton.IsEnabled = false;
            ModelStatusText.Text =
                "Using the saved model. Live model discovery is unavailable.";
        }
        else
        {
            ModelStatusText.Text =
                "Loading models from GitHub Copilot...";
        }
        if (settings.IncludeActiveCasesFolder)
        {
            AddOutlookFolderSelection(PeggyDefaults.ActiveCasesFolder);
        }
        foreach (var folder in settings.AdditionalOutlookFolders
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(NormalizeSavedOutlookFolder)
                     .OrderBy(
                         path => path.Count(character => character == '/')))
        {
            AddOutlookFolderSelection(folder);
        }
        _updatingOutlookFolderControls = true;
        try
        {
            AllInboxFoldersCheck.IsChecked =
                _selectedOutlookFolders.Contains(OutlookFolderPath.Inbox);
        }
        finally
        {
            _updatingOutlookFolderControls = false;
        }
        RenderSelectedOutlookFolders();
        StartupCheck.IsChecked = settings.StartAtSignIn;
        PausedCheck.IsChecked = settings.MonitoringPaused;
        SpeechEnabledCheck.IsChecked = settings.SpeechEnabled;
        var environmentSpeechModels = Environment.GetEnvironmentVariable(
            "PEGGY_SPEECH_MODELS");
        SpeechModelsText.Text = _speechModelDirectoryOverridden
            ? environmentSpeechModels?.Trim() ?? string.Empty
            : string.IsNullOrWhiteSpace(settings.SpeechModelDirectory)
                ? PeggyDefaults.SpeechModelDirectory
                : settings.SpeechModelDirectory;
        if (_speechModelDirectoryOverridden)
        {
            SpeechModelsText.IsReadOnly = true;
            SpeechModelsText.ToolTip =
                "Controlled by the PEGGY_SPEECH_MODELS environment variable.";
        }
        RefreshSpeechModelsStatus();
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
        _selectedTeams = settings.TeamsTeamWatchlist
            .GroupBy(item => item.TeamId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => CloneTeamWatch(group.First()),
                StringComparer.Ordinal);
        SelectComboItem(TeamsScopeCombo, settings.TeamsScope.ToString());
        MonitorTeamsChatsCheck.IsChecked =
            settings.MonitorTeamsChatsInSelectedScope;
        UpdateTeamsScopeControls();
        RenderSelectedChannels();
        Loaded += (_, _) =>
            PeggyWindowLayout.ConstrainToWorkArea(this);
        Loaded += async (_, _) => await LoadTeamsAsync();
        Loaded += async (_, _) => await LoadCopilotModelsAsync();
        Loaded += async (_, _) => await LoadOutlookFoldersAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _loadCancellation.Cancel();
            _loadCancellation.Dispose();
        };
    }

    public PeggySettings? SavedSettings { get; private set; }

    public bool AccountConnectionRequested { get; private set; }

    private async void InstallSpeechModels_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_installingSpeechModels)
        {
            return;
        }

        _installingSpeechModels = true;
        SpeechModelsText.IsEnabled = false;
        InstallSpeechModelsButton.IsEnabled = false;
        InstallSpeechModelsButton.Content = "Installing...";
        SpeechModelsStatus.Text =
            $"Downloading and verifying {SpeechModelSetupService
                .DownloadSizeDescription} of English voice models...";
        if (!TryGetSpeechModelDirectory(out var directory))
        {
            _installingSpeechModels = false;
            RefreshSpeechModelsStatus();
            return;
        }
        var repair = _speechModels.AreModelsInstalled(directory);
        try
        {
            await _speechModels.InstallAsync(
                directory,
                force: repair,
                ct: _loadCancellation.Token);
            SpeechEnabledCheck.IsChecked = true;
            RefreshSpeechModelsStatus();
        }
        catch (OperationCanceledException) when (
            _loadCancellation.IsCancellationRequested)
        {
        }
        catch (SpeechModelSetupException ex)
        {
            SpeechModelsStatus.Text = ex.Message;
            MessageBox.Show(
                this,
                ex.Message,
                "Peggy voice models",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _installingSpeechModels = false;
            if (!_closed)
            {
                SpeechModelsText.IsEnabled = true;
                RefreshSpeechModelsStatus();
            }
        }
    }

    private void SpeechModelsText_Changed(
        object sender,
        TextChangedEventArgs e)
    {
        if (SpeechModelsStatus is not null &&
            InstallSpeechModelsButton is not null &&
            !_installingSpeechModels)
        {
            RefreshSpeechModelsStatus();
        }
    }

    private bool TryGetSpeechModelDirectory(out string directory)
        => PeggyDefaults.TryResolveSpeechModelDirectory(
            SpeechModelsText.Text,
            out directory);

    private void RefreshSpeechModelsStatus()
    {
        if (!TryGetSpeechModelDirectory(out var directory))
        {
            SpeechModelsStatus.Text =
                "Enter a valid speech model directory.";
            InstallSpeechModelsButton.Content = "Install voice models";
            InstallSpeechModelsButton.IsEnabled = false;
            return;
        }
        var installed = _speechModels.AreModelsInstalled(directory);
        SpeechModelsStatus.Text = installed
            ? "English voice models are ready at this location."
            : $"Voice models are not installed here. Installing downloads " +
              $"{SpeechModelSetupService.DownloadSizeDescription}.";
        InstallSpeechModelsButton.Content = installed
            ? "Repair voice models"
            : "Install voice models";
        InstallSpeechModelsButton.IsEnabled = !_installingSpeechModels;
    }

    private async void RefreshModels_Click(
        object sender,
        RoutedEventArgs e)
        => await LoadCopilotModelsAsync();

    private async Task LoadCopilotModelsAsync()
    {
        if (_closed ||
            _loadCopilotModels is null ||
            _loadingModels)
        {
            return;
        }

        _loadingModels = true;
        ModelCombo.IsEnabled = false;
        RefreshModelsButton.IsEnabled = false;
        ModelStatusText.Text = "Loading models from GitHub Copilot...";
        var selectedModel = GetSelectedModel();
        var selectedReasoningEffort = GetSelectedReasoningEffort();
        var selectedContextTier = GetSelectedContextTier();
        if (selectedModel.Length == 0)
        {
            selectedModel = _initialModel;
        }

        try
        {
            var models = await _loadCopilotModels(
                _loadCancellation.Token);
            if (_closed)
            {
                return;
            }
            var availableModels = models
                .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(
                    model => model.Name,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(
                    model => model.Id,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (availableModels.Count == 0)
            {
                throw new InvalidOperationException(
                    "GitHub Copilot returned no available models.");
            }

            var options = new List<CopilotModelOption>(availableModels);
            if (!options.Any(model => string.Equals(
                    model.Id,
                    selectedModel,
                    StringComparison.OrdinalIgnoreCase)))
            {
                options.Insert(
                    0,
                    new CopilotModelOption(
                        selectedModel,
                        selectedModel,
                        IsAvailable: false));
            }
            _updatingModelControls = true;
            try
            {
                ModelCombo.ItemsSource = options;
                ModelCombo.SelectedValue = selectedModel;
                UpdateModelDependentControls(
                    selectedReasoningEffort,
                    selectedContextTier,
                    preserveUnavailable: true);
            }
            finally
            {
                _updatingModelControls = false;
            }
            _modelCatalogLoaded = true;
            ModelStatusText.Text = _modelOverridden
                ? $"{availableModels.Count} models available. Model is " +
                  "controlled by PEGGY_COPILOT_MODEL. " +
                  $"Refreshed at {DateTime.Now:t}."
                : $"{availableModels.Count} models available from GitHub " +
                  $"Copilot. Refreshed at {DateTime.Now:t}.";
        }
        catch (OperationCanceledException) when (
            _loadCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ModelStatusText.Text =
                $"Could not refresh GitHub Copilot models: {ex.Message}";
        }
        finally
        {
            _loadingModels = false;
            if (!_closed)
            {
                ModelCombo.IsEnabled = !_modelOverridden;
                RefreshModelsButton.IsEnabled = true;
            }
        }
    }

    private void ModelCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_updatingModelControls)
        {
            return;
        }
        UpdateModelDependentControls(
            GetSelectedReasoningEffort(),
            GetSelectedContextTier(),
            preserveUnavailable: false);
    }

    private void UpdateModelDependentControls(
        string reasoningEffort,
        string contextTier,
        bool preserveUnavailable)
    {
        var model = ModelCombo.SelectedItem as CopilotModelOption;
        if (model is null)
        {
            return;
        }

        var reasoningOptions = new List<ModelSettingOption>
        {
            new(
                string.Empty,
                string.IsNullOrWhiteSpace(model.DefaultReasoningEffort)
                    ? "Model default"
                    : $"Model default ({model.DefaultReasoningEffort})")
        };
        foreach (var effort in model.SupportedReasoningEfforts ?? [])
        {
            if (!string.IsNullOrWhiteSpace(effort) &&
                !reasoningOptions.Any(option => string.Equals(
                    option.Value,
                    effort,
                    StringComparison.OrdinalIgnoreCase)))
            {
                reasoningOptions.Add(new ModelSettingOption(
                    effort.Trim(),
                    FormatSettingName(effort)));
            }
        }
        if (reasoningEffort.Length > 0 &&
            !reasoningOptions.Any(option => string.Equals(
                option.Value,
                reasoningEffort,
                StringComparison.OrdinalIgnoreCase)))
        {
            if (preserveUnavailable)
            {
                reasoningOptions.Insert(
                    1,
                    new ModelSettingOption(
                        reasoningEffort,
                        $"{FormatSettingName(reasoningEffort)} - currently " +
                        "unavailable",
                        IsAvailable: false));
            }
            else
            {
                reasoningEffort = string.Empty;
            }
        }

        var contextOptions = new List<ModelSettingOption>
        {
            new(
                PeggyDefaults.CopilotContextTierDefault,
                "Default")
        };
        if (model.SupportsLongContext)
        {
            contextOptions.Add(new ModelSettingOption(
                PeggyDefaults.CopilotContextTierLong,
                "Long context"));
        }
        if (string.Equals(
                contextTier,
                PeggyDefaults.CopilotContextTierLong,
                StringComparison.OrdinalIgnoreCase) &&
            !model.SupportsLongContext)
        {
            if (preserveUnavailable)
            {
                contextOptions.Add(new ModelSettingOption(
                    PeggyDefaults.CopilotContextTierLong,
                    "Long context - currently unavailable",
                    IsAvailable: false));
            }
            else
            {
                contextTier = PeggyDefaults.CopilotContextTierDefault;
            }
        }

        _updatingModelControls = true;
        try
        {
            ReasoningEffortCombo.ItemsSource = reasoningOptions;
            ReasoningEffortCombo.SelectedValue = reasoningEffort;
            if (ReasoningEffortCombo.SelectedIndex < 0)
            {
                ReasoningEffortCombo.SelectedIndex = 0;
            }
            ContextTierCombo.ItemsSource = contextOptions;
            ContextTierCombo.SelectedValue = contextTier;
            if (ContextTierCombo.SelectedIndex < 0)
            {
                ContextTierCombo.SelectedIndex = 0;
            }
        }
        finally
        {
            _updatingModelControls = false;
        }

        ReasoningEffortCombo.IsEnabled =
            model.IsAvailable && reasoningOptions.Count > 1;
        ContextTierCombo.IsEnabled =
            model.IsAvailable && contextOptions.Count > 1;
        var contextDescription = model.MaxContextWindowTokens > 0
            ? "Model limit reported by Copilot: " +
              $"{model.MaxContextWindowTokens:N0} tokens."
            : "The model limit was not reported.";
        ModelCapabilitiesText.Text =
            $"{contextDescription} " +
            (model.SupportsLongContext
                ? "Long context is available for this model."
                : "Long context is not available for this model.");
    }

    private static string FormatSettingName(string value)
        => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
            value.Replace('_', ' ').Trim().ToLowerInvariant());

    private string GetSelectedModel()
        => ModelCombo.SelectedValue as string ??
           (ModelCombo.SelectedItem as CopilotModelOption)?.Id ??
           string.Empty;

    private string GetSelectedReasoningEffort()
        => ReasoningEffortCombo.SelectedValue as string ??
           (ReasoningEffortCombo.SelectedItem as ModelSettingOption)?.Value ??
           string.Empty;

    private string GetSelectedContextTier()
        => ContextTierCombo.SelectedValue as string ??
           (ContextTierCombo.SelectedItem as ModelSettingOption)?.Value ??
           PeggyDefaults.CopilotContextTierDefault;

    private void AllInboxFoldersCheck_Changed(
        object sender,
        RoutedEventArgs e)
    {
        if (_updatingOutlookFolderControls)
        {
            return;
        }
        if (AllInboxFoldersCheck.IsChecked == true)
        {
            AddOutlookFolderSelection(OutlookFolderPath.Inbox);
        }
        else
        {
            RemoveOutlookFolderSelection(OutlookFolderPath.Inbox);
        }
        UpdateOutlookFolderChecks();
        RenderSelectedOutlookFolders();
    }

    private async Task LoadOutlookFoldersAsync()
    {
        if (_closed || _outlookRootLoaded || _loadingOutlookRoot)
        {
            return;
        }
        _loadingOutlookRoot = true;
        OutlookFolderStatus.Text = "Loading Outlook folders...";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                _loadCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var folders = await _provider.ListOutlookChildFoldersAsync(
                null,
                timeout.Token);
            if (_closed)
            {
                return;
            }

            OutlookFoldersPanel.Children.Clear();
            _outlookFolderChecks.Clear();
            _outlookFolderPanels.Clear();
            _loadedOutlookFolderIds.Clear();
            _loadingOutlookFolderIds.Clear();
            foreach (var folder in folders)
            {
                AddOutlookFolderNode(OutlookFoldersPanel, folder);
            }
            _outlookRootLoaded = true;
            OutlookFolderStatus.Text = folders.Count == 0
                ? "No Inbox subfolders were returned."
                : $"{folders.Count} top-level Inbox folders loaded. Expand " +
                  "only the folders whose children you want to browse.";
            UpdateOutlookFolderChecks();
            RenderSelectedOutlookFolders();
        }
        catch (OperationCanceledException) when (_closed)
        {
        }
        catch (OperationCanceledException)
        {
            OutlookFolderStatus.Text =
                "Outlook folders took too long to load. Close and reopen " +
                "Settings to retry.";
        }
        catch (Exception ex)
        {
            OutlookFolderStatus.Text =
                $"Outlook folders could not be loaded: {ex.Message}";
        }
        finally
        {
            _loadingOutlookRoot = false;
        }
    }

    private void AddOutlookFolderNode(
        Panel destination,
        OutlookFolderInfo folder)
    {
        var checkBox = new CheckBox
        {
            Content = folder.FolderName,
            Tag = folder,
            IsChecked = IsOutlookFolderIncluded(folder.FolderPath),
            FontWeight = folder.HasChildren
                ? FontWeights.SemiBold
                : FontWeights.Normal,
            ToolTip = folder.IsSelectable
                ? folder.FolderPath
                : "This folder name cannot be represented safely as a " +
                  "Peggy folder path."
        };
        checkBox.Checked += OutlookFolderCheck_Changed;
        checkBox.Unchecked += OutlookFolderCheck_Changed;
        _outlookFolderChecks[folder.FolderPath] = checkBox;

        if (!folder.HasChildren)
        {
            checkBox.Margin = new Thickness(0, 2, 0, 2);
            destination.Children.Add(checkBox);
            return;
        }

        var childPanel = new StackPanel
        {
            Margin = new Thickness(12, 3, 0, 7)
        };
        childPanel.Children.Add(new TextBlock
        {
            Text = "Expand this folder to load its children.",
            Foreground = System.Windows.Media.Brushes.DimGray
        });
        _outlookFolderPanels[folder.FolderId] = childPanel;
        var expander = new Expander
        {
            Header = checkBox,
            Tag = folder,
            Content = childPanel,
            Margin = new Thickness(0, 1, 0, 1)
        };
        expander.Expanded += async (_, _) =>
            await LoadOutlookChildFoldersAsync(folder);
        destination.Children.Add(expander);
    }

    private async Task LoadOutlookChildFoldersAsync(
        OutlookFolderInfo parent)
    {
        if (_closed ||
            _loadedOutlookFolderIds.Contains(parent.FolderId) ||
            !_loadingOutlookFolderIds.Add(parent.FolderId) ||
            !_outlookFolderPanels.TryGetValue(
                parent.FolderId,
                out var childPanel))
        {
            return;
        }

        ShowOutlookFolderMessage(
            childPanel,
            "Loading folders...",
            retryParent: null);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                _loadCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var folders = await _provider.ListOutlookChildFoldersAsync(
                parent,
                timeout.Token);
            if (_closed)
            {
                return;
            }

            childPanel.Children.Clear();
            if (folders.Count == 0)
            {
                childPanel.Children.Add(new TextBlock
                {
                    Text = "No child folders were returned.",
                    Foreground = System.Windows.Media.Brushes.DimGray
                });
            }
            foreach (var folder in folders)
            {
                AddOutlookFolderNode(childPanel, folder);
            }
            _loadedOutlookFolderIds.Add(parent.FolderId);
            UpdateOutlookFolderChecks();
        }
        catch (OperationCanceledException) when (_closed)
        {
        }
        catch (OperationCanceledException)
        {
            ShowOutlookFolderMessage(
                childPanel,
                "Child folders took too long to load.",
                parent);
        }
        catch (Exception ex)
        {
            ShowOutlookFolderMessage(
                childPanel,
                $"Child folders could not be loaded: {ex.Message}",
                parent);
        }
        finally
        {
            _loadingOutlookFolderIds.Remove(parent.FolderId);
        }
    }

    private void OutlookFolderCheck_Changed(
        object sender,
        RoutedEventArgs e)
    {
        if (_updatingOutlookFolderControls ||
            sender is not CheckBox
            {
                Tag: OutlookFolderInfo folder
            } checkBox)
        {
            return;
        }

        if (checkBox.IsChecked == true)
        {
            AddOutlookFolderSelection(folder.FolderPath);
        }
        else
        {
            RemoveOutlookFolderSelection(folder.FolderPath);
        }
        UpdateOutlookFolderChecks();
        RenderSelectedOutlookFolders();
    }

    private void AddOutlookFolderSelection(string path)
    {
        var normalized = NormalizeSavedOutlookFolder(path);
        if (_selectedOutlookFolders.Any(selected =>
                OutlookFolderPath.IsSameOrDescendant(
                    normalized,
                    selected)))
        {
            return;
        }
        _selectedOutlookFolders.RemoveWhere(selected =>
            OutlookFolderPath.IsSameOrDescendant(
                selected,
                normalized));
        _selectedOutlookFolders.Add(normalized);
    }

    private void RemoveOutlookFolderSelection(string path)
        => _selectedOutlookFolders.Remove(
            NormalizeSavedOutlookFolder(path));

    private bool IsOutlookFolderIncluded(string path)
    {
        var normalized = NormalizeSavedOutlookFolder(path);
        return _selectedOutlookFolders.Any(selected =>
            OutlookFolderPath.IsSameOrDescendant(
                normalized,
                selected));
    }

    private bool HasSelectedOutlookFolderAncestor(string path)
    {
        var normalized = NormalizeSavedOutlookFolder(path);
        return _selectedOutlookFolders.Any(selected =>
            !string.Equals(
                normalized,
                selected,
                StringComparison.OrdinalIgnoreCase) &&
            OutlookFolderPath.IsSameOrDescendant(
                normalized,
                selected));
    }

    private void UpdateOutlookFolderChecks()
    {
        var wasUpdating = _updatingOutlookFolderControls;
        _updatingOutlookFolderControls = true;
        try
        {
            AllInboxFoldersCheck.IsChecked =
                _selectedOutlookFolders.Contains(OutlookFolderPath.Inbox);
            foreach (var pair in _outlookFolderChecks)
            {
                var folder = (OutlookFolderInfo)pair.Value.Tag;
                pair.Value.IsChecked =
                    IsOutlookFolderIncluded(folder.FolderPath);
                pair.Value.IsEnabled =
                    folder.IsSelectable &&
                    !HasSelectedOutlookFolderAncestor(folder.FolderPath);
            }
        }
        finally
        {
            _updatingOutlookFolderControls = wasUpdating;
        }
    }

    private void RenderSelectedOutlookFolders()
    {
        SelectedOutlookFoldersPanel.Children.Clear();
        var selected = _selectedOutlookFolders
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SelectedOutlookFolderStatus.Text =
            $"{selected.Count} additional folder " +
            $"{(selected.Count == 1 ? "root" : "roots")} selected";
        if (selected.Count == 0)
        {
            SelectedOutlookFoldersPanel.Children.Add(new TextBlock
            {
                Text = "No additional folders selected. Only direct Inbox " +
                       "messages will be monitored.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.DimGray,
                FontSize = 11
            });
            return;
        }

        foreach (var path in selected)
        {
            var row = new DockPanel
            {
                Margin = new Thickness(0, 2, 0, 2)
            };
            var remove = new Button
            {
                Content = "Remove",
                Tag = path,
                MinWidth = 68,
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(8, 0, 0, 0)
            };
            remove.Click += RemoveSelectedOutlookFolder_Click;
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(new TextBlock
            {
                Text = path == OutlookFolderPath.Inbox
                    ? "Inbox (all subfolders)"
                    : path,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });
            SelectedOutlookFoldersPanel.Children.Add(row);
        }
    }

    private void RemoveSelectedOutlookFolder_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path })
        {
            return;
        }
        RemoveOutlookFolderSelection(path);
        UpdateOutlookFolderChecks();
        RenderSelectedOutlookFolders();
    }

    private async void RetryOutlookFolder_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Button { Tag: OutlookFolderInfo parent })
        {
            await LoadOutlookChildFoldersAsync(parent);
        }
    }

    private void ShowOutlookFolderMessage(
        Panel panel,
        string message,
        OutlookFolderInfo? retryParent)
    {
        panel.Children.Clear();
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DimGray
        });
        if (retryParent is null)
        {
            return;
        }
        var retry = new Button
        {
            Content = "Retry",
            Tag = retryParent,
            Width = 72,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 6, 0, 0)
        };
        retry.Click += RetryOutlookFolder_Click;
        panel.Children.Add(retry);
    }

    private static string NormalizeSavedOutlookFolder(string path)
    {
        try
        {
            return OutlookFolderPath.Normalize(path);
        }
        catch (ArgumentException)
        {
            return path.Trim();
        }
    }

    private List<string> GetSavedAdditionalOutlookFolders()
        => _selectedOutlookFolders
            .Where(path => !string.Equals(
                path,
                PeggyDefaults.ActiveCasesFolder,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void TeamsScope_Changed(object sender, RoutedEventArgs e)
        => UpdateTeamsScopeControls();

    private void UpdateTeamsScopeControls()
    {
        if (TeamsScopeCombo is null ||
            MonitorTeamsChatsCheck is null ||
            TeamsSelectionPanel is null)
        {
            return;
        }

        var scope = GetSelectedTeamsScope();
        MonitorTeamsChatsCheck.IsEnabled =
            scope == TeamsMonitoringScope.SelectedTeamsAndChannels;
        TeamsSelectionPanel.IsEnabled =
            scope != TeamsMonitoringScope.Off;
        TeamsScopeDescription.Text = scope switch
        {
            TeamsMonitoringScope.AllActivity =>
                "Monitor new activity across Teams. Selected sources below " +
                "also receive full channel delta monitoring.",
            TeamsMonitoringScope.SelectedTeamsAndChannels =>
                "Monitor only the accessible teams and channels selected " +
                "below. New channels are included for a fully selected team.",
            _ => "Do not retrieve or classify Microsoft Teams messages."
        };
        UpdateTeamsScopeWarning();
    }

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
            _teamChecks.Clear();
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
                var teamCheck = new CheckBox
                {
                    Content = team.TeamName,
                    Tag = team,
                    IsThreeState = true,
                    IsChecked = GetTeamSelectionState(team.TeamId),
                    FontWeight = FontWeights.SemiBold
                };
                teamCheck.Checked += (_, _) =>
                    ApplyTeamCheckSelection(team, teamCheck);
                teamCheck.Unchecked += (_, _) =>
                    ApplyTeamCheckSelection(team, teamCheck);
                teamCheck.Indeterminate += (_, _) =>
                    ApplyTeamCheckSelection(team, teamCheck);
                _teamChecks[team.TeamId] = teamCheck;
                var expander = new Expander
                {
                    Header = teamCheck,
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
                  "whose channels you want to configure.";
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
            IsChecked = IsChannelSelected(
                channel.TeamId,
                channel.ChannelId),
            Margin = new Thickness(0, 2, 0, 2)
        };
        checkBox.Checked += (_, _) =>
        {
            if (_updatingSelectionControls)
            {
                return;
            }
            SelectChannel(channel);
            UpdateTeamCheckState(channel.TeamId);
            RenderSelectedChannels();
        };
        checkBox.Unchecked += (_, _) =>
        {
            if (_updatingSelectionControls)
            {
                return;
            }
            DeselectChannel(channel);
            UpdateTeamCheckState(channel.TeamId);
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
            Content = "Select entire team",
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
        if (sender is not Button { Tag: TeamsTeamInfo team })
        {
            return;
        }

        SelectTeam(team);
    }

    private void SelectTeam(TeamsTeamInfo team)
    {
        _selectedTeams[team.TeamId] = new TeamsTeamWatch
        {
            TeamId = team.TeamId,
            DisplayName = team.TeamName
        };
        RemoveExplicitTeamChannels(team.TeamId);
        _updatingSelectionControls = true;
        try
        {
            if (_loadedChannelsByTeam.TryGetValue(
                    team.TeamId,
                    out var channels))
            {
                foreach (var channel in channels)
                {
                    var key = ChannelKey(
                        channel.TeamId,
                        channel.ChannelId);
                    if (_channelChecks.TryGetValue(key, out var checkBox))
                    {
                        checkBox.IsChecked = true;
                    }
                }
            }
            UpdateTeamCheckState(team.TeamId);
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
        _selectedTeams.Remove(teamId);
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
            }
            foreach (var pair in _channelChecks.Where(pair =>
                         pair.Key.StartsWith(
                             teamId + "\n",
                             StringComparison.Ordinal)))
            {
                pair.Value.IsChecked = false;
            }
            UpdateTeamCheckState(teamId);
        }
        finally
        {
            _updatingSelectionControls = false;
        }
        RenderSelectedChannels();
    }

    private void ApplyTeamCheckSelection(
        TeamsTeamInfo team,
        CheckBox checkBox)
    {
        if (_updatingSelectionControls)
        {
            return;
        }

        if (checkBox.IsChecked == true)
        {
            SelectTeam(team);
        }
        else
        {
            DeselectTeam(team.TeamId);
        }
    }

    private void SelectChannel(TeamsChannelInfo channel)
    {
        if (_selectedTeams.TryGetValue(channel.TeamId, out var team))
        {
            team.ExcludedChannelIds.RemoveAll(id => string.Equals(
                id,
                channel.ChannelId,
                StringComparison.Ordinal));
            return;
        }

        var key = ChannelKey(channel.TeamId, channel.ChannelId);
        _selectedChannels[key] = new TeamsChannelWatch
        {
            TeamId = channel.TeamId,
            ChannelId = channel.ChannelId,
            DisplayName = $"{channel.TeamName} / {channel.ChannelName}"
        };
    }

    private void DeselectChannel(TeamsChannelInfo channel)
    {
        var key = ChannelKey(channel.TeamId, channel.ChannelId);
        _selectedChannels.Remove(key);
        if (!_selectedTeams.TryGetValue(channel.TeamId, out var team))
        {
            return;
        }

        if (!team.ExcludedChannelIds.Contains(
                channel.ChannelId,
                StringComparer.Ordinal))
        {
            team.ExcludedChannelIds.Add(channel.ChannelId);
        }
        if (_loadedChannelsByTeam.TryGetValue(
                channel.TeamId,
                out var channels) &&
            channels.All(item => !IsChannelSelected(
                item.TeamId,
                item.ChannelId)))
        {
            _selectedTeams.Remove(channel.TeamId);
        }
    }

    private bool IsChannelSelected(string teamId, string channelId)
    {
        if (_selectedTeams.TryGetValue(teamId, out var team))
        {
            return !team.ExcludedChannelIds.Contains(
                channelId,
                StringComparer.Ordinal);
        }

        return _selectedChannels.ContainsKey(ChannelKey(teamId, channelId));
    }

    private bool? GetTeamSelectionState(string teamId)
    {
        if (_selectedTeams.TryGetValue(teamId, out var team))
        {
            return team.ExcludedChannelIds.Count == 0 ? true : null;
        }

        return _selectedChannels.Values.Any(item => string.Equals(
            item.TeamId,
            teamId,
            StringComparison.Ordinal))
            ? null
            : false;
    }

    private void UpdateTeamCheckState(string teamId)
    {
        if (!_teamChecks.TryGetValue(teamId, out var checkBox))
        {
            return;
        }

        var wasUpdating = _updatingSelectionControls;
        _updatingSelectionControls = true;
        try
        {
            checkBox.IsChecked = GetTeamSelectionState(teamId);
        }
        finally
        {
            _updatingSelectionControls = wasUpdating;
        }
    }

    private void RemoveExplicitTeamChannels(string teamId)
    {
        foreach (var key in _selectedChannels
                     .Where(pair => string.Equals(
                         pair.Value.TeamId,
                         teamId,
                         StringComparison.Ordinal))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _selectedChannels.Remove(key);
        }
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
        var selectedTeamIds = _selectedTeams.Keys
            .Concat(_selectedChannels.Values.Select(item => item.TeamId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        SelectedChannelStatus.Text =
            $"Selected sources: {selectedTeamIds.Count} " +
            $"{(selectedTeamIds.Count == 1 ? "team" : "teams")}, " +
            $"{_selectedTeams.Count} entire, " +
            $"{_selectedChannels.Count} individual " +
            $"{(_selectedChannels.Count == 1 ? "channel" : "channels")}";
        UpdateTeamsScopeWarning();
        if (selectedTeamIds.Count == 0)
        {
            SelectedChannelsPanel.Children.Add(new TextBlock
            {
                Text = GetSelectedTeamsScope() ==
                    TeamsMonitoringScope.AllActivity
                    ? "None selected for full channel delta monitoring."
                    : "No teams or channels selected.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.DimGray,
                FontSize = 11
            });
            return;
        }

        foreach (var teamId in selectedTeamIds.OrderBy(
                     GetTeamName,
                     StringComparer.OrdinalIgnoreCase))
        {
            _selectedTeams.TryGetValue(teamId, out var wholeTeam);
            var selectedChannels = _selectedChannels
                .Where(pair => string.Equals(
                    pair.Value.TeamId,
                    teamId,
                    StringComparison.Ordinal))
                .OrderBy(
                    pair => pair.Value.DisplayName,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
            var teamHeader = new DockPanel
            {
                Margin = new Thickness(0, 4, 0, 2)
            };
            var deselectTeam = new Button
            {
                Content = "Deselect team",
                Tag = teamId,
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(8, 0, 0, 0)
            };
            deselectTeam.Click += DeselectTeam_Click;
            DockPanel.SetDock(deselectTeam, Dock.Right);
            teamHeader.Children.Add(deselectTeam);
            teamHeader.Children.Add(new TextBlock
            {
                Text = GetTeamName(teamId),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });
            SelectedChannelsPanel.Children.Add(teamHeader);

            if (wholeTeam is not null)
            {
                SelectedChannelsPanel.Children.Add(new TextBlock
                {
                    Text = wholeTeam.ExcludedChannelIds.Count == 0
                        ? "All accessible channels, including new channels"
                        : "All accessible channels except " +
                          $"{wholeTeam.ExcludedChannelIds.Count}",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = System.Windows.Media.Brushes.DimGray,
                    FontSize = 11,
                    Margin = new Thickness(12, 2, 0, 2)
                });
            }
            foreach (var pair in selectedChannels)
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

    private string GetTeamName(string teamId)
    {
        if (_teamNames.TryGetValue(teamId, out var teamName))
        {
            return teamName;
        }
        if (_selectedTeams.TryGetValue(teamId, out var selectedTeam) &&
            !string.IsNullOrWhiteSpace(selectedTeam.DisplayName))
        {
            return selectedTeam.DisplayName;
        }

        var selectedChannel = _selectedChannels.Values.FirstOrDefault(item =>
            string.Equals(
                item.TeamId,
                teamId,
                StringComparison.Ordinal));
        return selectedChannel is null
            ? teamId
            : GetTeamName(selectedChannel);
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
        var separator = key.IndexOf('\n');
        if (separator > 0)
        {
            UpdateTeamCheckState(key[..separator]);
        }
        RenderSelectedChannels();
    }

    private void UpdateTeamsScopeWarning()
    {
        if (TeamsScopeWarning is null)
        {
            return;
        }

        var scope = GetSelectedTeamsScope();
        if (scope != TeamsMonitoringScope.SelectedTeamsAndChannels)
        {
            TeamsScopeWarning.Visibility = Visibility.Collapsed;
            return;
        }

        var hasSelectedSources =
            _selectedTeams.Count > 0 ||
            _selectedChannels.Count > 0 ||
            MonitorTeamsChatsCheck.IsChecked == true;
        TeamsScopeWarningText.Text = hasSelectedSources
            ? "Only these sources will generate Teams alerts. Unselected " +
              "teams and channels are not retrieved."
            : "No Teams sources are selected, so Peggy will not generate " +
              "Teams alerts.";
        TeamsScopeWarning.Visibility = Visibility.Visible;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_installingSpeechModels)
        {
            MessageBox.Show(
                this,
                "Wait for voice model installation to finish or close Settings " +
                "to cancel it.",
                "Assistant settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var model = GetSelectedModel().Trim();
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
        if (_modelCatalogLoaded &&
            !_modelOverridden &&
            ModelCombo.SelectedItem is CopilotModelOption
            {
                IsAvailable: false
            })
        {
            MessageBox.Show(
                this,
                "Choose a model currently available in GitHub Copilot.",
                "Assistant settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (_modelCatalogLoaded &&
            ModelCombo.SelectedItem is CopilotModelOption
            {
                IsAvailable: true
            } &&
            ReasoningEffortCombo.SelectedItem is ModelSettingOption
            {
                IsAvailable: false
            })
        {
            MessageBox.Show(
                this,
                "Choose a reasoning effort supported by the selected model.",
                "Assistant settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (_modelCatalogLoaded &&
            ModelCombo.SelectedItem is CopilotModelOption
            {
                IsAvailable: true
            } &&
            ContextTierCombo.SelectedItem is ModelSettingOption
            {
                IsAvailable: false
            })
        {
            MessageBox.Show(
                this,
                "Choose a context tier supported by the selected model.",
                "Assistant settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        var reasoningEffort = GetSelectedReasoningEffort().Trim();
        var contextTier = GetSelectedContextTier();
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

        var folders = GetSavedAdditionalOutlookFolders();
        var includeActiveCasesFolder =
            _selectedOutlookFolders.Contains(
                PeggyDefaults.ActiveCasesFolder);
        var watchlist = GetSavedWatchlist();
        var teamWatchlist = GetSavedTeamWatchlist();
        SavedSettings = new PeggySettings
        {
            CopilotModel = model,
            CopilotReasoningEffort = reasoningEffort,
            CopilotContextTier = contextTier,
            IncludeActiveCasesFolder = includeActiveCasesFolder,
            AdditionalOutlookFolders = folders,
            TeamsScope = GetSelectedTeamsScope(),
            MonitorTeamsChatsInSelectedScope =
                MonitorTeamsChatsCheck.IsChecked == true,
            TeamsTeamWatchlist = teamWatchlist,
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
        if (MessageBox.Show(
                this,
                "Changing or reconnecting the Microsoft 365 account closes " +
                "Settings. Unsaved changes on this page will be discarded. " +
                "Continue?",
                "Microsoft 365 account",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }
        AccountConnectionRequested = true;
        DialogResult = false;
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

    private List<TeamsTeamWatch> GetSavedTeamWatchlist()
        => _selectedTeams.Values
            .OrderBy(
                item => item.DisplayName,
                StringComparer.OrdinalIgnoreCase)
            .Select(CloneTeamWatch)
            .ToList();

    private static TeamsChannelWatch CloneWatch(TeamsChannelWatch item)
        => new()
        {
            TeamId = item.TeamId,
            ChannelId = item.ChannelId,
            DisplayName = item.DisplayName
        };

    private static TeamsTeamWatch CloneTeamWatch(TeamsTeamWatch item)
        => new()
        {
            TeamId = item.TeamId,
            DisplayName = item.DisplayName,
            ExcludedChannelIds = item.ExcludedChannelIds
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList()
        };

    private TeamsMonitoringScope GetSelectedTeamsScope()
    {
        var item = TeamsScopeCombo.SelectedItem as ComboBoxItem;
        return Enum.TryParse<TeamsMonitoringScope>(
            item?.Tag?.ToString(),
            out var scope)
            ? scope
            : TeamsMonitoringScope.AllActivity;
    }

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
