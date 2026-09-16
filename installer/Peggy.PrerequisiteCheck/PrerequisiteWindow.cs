using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Peggy.PrerequisiteCheck;

internal sealed class PrerequisiteWindow : Window
{
    private readonly Dictionary<string, (TextBlock State, TextBlock Detail, Button Action)> rows = [];
    private readonly Dictionary<string, PrerequisiteResult> results = [];
    private readonly CancellationTokenSource checks = new();
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar progress = new() { Height = 5, Margin = new Thickness(0, 12, 0, 12) };
    private readonly Button retry = new() { Content = "Retry checks" };
    private readonly Button proceed = new() { Content = "Continue to install Peggy", IsEnabled = false };
    private bool busy;
    private bool installing;
    internal bool Accepted { get; private set; }

    internal PrerequisiteWindow()
    {
        Title = "Peggy setup · prerequisites";
        Width = 830;
        Height = 770;
        MinWidth = 650;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI");
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/peggy-prerequisite-check;component/PeggyTheme.xaml", UriKind.Relative)
        });
        Background = (Brush)FindResource("PeggyPageBrush");
        Foreground = (Brush)FindResource("PeggyTextBrush");
        var panel = new StackPanel { Margin = new Thickness(28) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock
        {
            Text = "Let's get Peggy ready",
            FontSize = 27, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("PeggyBrandDarkBrush")
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Check the tools Peggy needs before installing the app. " +
                "Nothing is installed or updated until you choose an action.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12)
        });
        panel.Children.Add(summary);
        panel.Children.Add(progress);
        for (var i = 0; i < PrerequisiteEngine.Ids.Length; i++)
        {
            var id = PrerequisiteEngine.Ids[i];
            var card = new Grid { Margin = new Thickness(12) };
            card.ColumnDefinitions.Add(new ColumnDefinition());
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            var text = new StackPanel { Margin = new Thickness(0, 0, 15, 0) };
            text.Children.Add(new TextBlock
            {
                Text = PrerequisiteEngine.Names[i], FontSize = 16, FontWeight = FontWeights.SemiBold
            });
            var state = new TextBlock { Text = "Checking", Margin = new Thickness(0, 4, 0, 4) };
            var detail = new TextBlock
            {
                Text = "Waiting for check…", TextWrapping = TextWrapping.Wrap, FontSize = 12,
                Foreground = (Brush)FindResource("PeggyMutedTextBrush")
            };
            text.Children.Add(state);
            var details = new Expander { Header = "Details", Content = detail, IsExpanded = true };
            text.Children.Add(details);
            card.Children.Add(text);
            var actions = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var action = MakeButton("Install", "PeggyPrimaryButtonStyle");
            action.IsEnabled = false;
            action.Click += async (_, _) => await InstallAsync(id);
            actions.Children.Add(action);
            var help = MakeButton("Official download", "PeggySecondaryButtonStyle");
            help.Margin = new Thickness(0, 6, 0, 0);
            help.Click += (_, _) => OpenGuidance(id);
            actions.Children.Add(help);
            Grid.SetColumn(actions, 1);
            card.Children.Add(actions);
            panel.Children.Add(new Border
            {
                Background = (Brush)FindResource("PeggySurfaceBrush"),
                BorderBrush = (Brush)FindResource("PeggyBorderBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 0, 0, 9), Child = card
            });
            rows[id] = (state, detail, action);
        }
        panel.Children.Add(new TextBlock
        {
            Text = "WinGet and the vendor installer show their own terms and Windows UAC prompts. " +
                "Review them there. If WinGet is unavailable, use Official download, then Retry checks. " +
                "Account selection and WorkIQ consent happen on Peggy's first run.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 7, 0, 15)
        });
        var footer = new StackPanel { Orientation = Orientation.Horizontal };
        retry.Style = (Style)FindResource("PeggySecondaryButtonStyle");
        retry.Click += async (_, _) => await CheckAsync();
        proceed.Style = (Style)FindResource("PeggyPrimaryButtonStyle");
        proceed.Margin = new Thickness(10, 0, 0, 0);
        proceed.Click += (_, _) => { Accepted = true; Close(); };
        footer.Children.Add(retry);
        footer.Children.Add(proceed);
        var cancel = MakeButton("Cancel setup", "PeggySecondaryButtonStyle");
        cancel.Margin = new Thickness(10, 0, 0, 0);
        cancel.Click += (_, _) => Close();
        footer.Children.Add(cancel);
        panel.Children.Add(footer);
        Loaded += async (_, _) => await CheckAsync();
        Closing += (_, e) =>
        {
            if (installing)
            {
                e.Cancel = true;
                summary.Text = "Finish or cancel the visible dependency installer first. " +
                    "Peggy setup will wait safely, then you can close this window.";
                return;
            }
            checks.Cancel();
        };
    }

    private Button MakeButton(string text, string style) => new()
    {
        Content = text, Style = (Style)FindResource(style)
    };

    private async Task CheckAsync()
    {
        if (busy || checks.IsCancellationRequested) return;
        SetBusy(true);
        summary.Text = "Checking prerequisites for the current Windows user…";
        try
        {
            await Task.Run(async () =>
            {
                PrerequisiteEngine.RefreshEnvironment();
                await PrerequisiteEngine.CheckAllAsync(checks.Token,
                    result => Dispatcher.Invoke(() => ShowResult(result)));
            }, checks.Token);
            summary.Text = results.Values.All(result => result.State == PrerequisiteState.Ready)
                ? "All prerequisites are ready. Continue to install Peggy."
                : "Choose an action for missing or outdated tools, then retry. " +
                    "npm is included with Node.js; Repair runs the Node.js repair wizard.";
            if (!InteractiveIdentity.CanInstallDependencies)
            {
                summary.Text += " Dependency changes are disabled in this identity: close setup and " +
                    "run it normally from your desktop, not as administrator or a service.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { summary.Text = $"Checks failed: {ex.Message}. Retry when ready."; }
        finally { SetBusy(false); }
    }

    private void ShowResult(PrerequisiteResult result)
    {
        results[result.Id] = result;
        var row = rows[result.Id];
        row.State.Text = result.State.ToString();
        row.State.Foreground = (Brush)FindResource(result.State == PrerequisiteState.Ready
            ? "PeggyPositiveBrush" : result.State == PrerequisiteState.Checking
            ? "PeggyMutedTextBrush" : "PeggyWarningBrush");
        row.Detail.Text = result.Detail;
        if (result.Id == "copilot" && HasCopilotOverride)
        {
            row.Detail.Text += " PEGGY_COPILOT_PATH takes precedence. Update that executable " +
                "yourself or remove the override and restart setup; installing another copy cannot fix it.";
        }
        row.Action.Content = PrerequisiteInstall.ActionName(result);
    }

    private static bool HasCopilotOverride =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PEGGY_COPILOT_PATH"));

    private void SetBusy(bool value)
    {
        busy = value;
        progress.IsIndeterminate = value;
        retry.IsEnabled = !value;
        proceed.IsEnabled = !value && results.Count == rows.Count &&
            results.Values.All(result => result.State == PrerequisiteState.Ready);
        foreach (var (id, row) in rows)
        {
            row.Action.IsEnabled = !value && results.ContainsKey(id) &&
                InteractiveIdentity.CanInstallDependencies && !(id == "copilot" && HasCopilotOverride);
        }
    }

    private async Task InstallAsync(string id)
    {
        if (busy || !InteractiveIdentity.CanInstallDependencies) return;
        var winget = PrerequisiteEngine.ResolveCommand(["winget.exe"]);
        if (winget is null)
        {
            summary.Text = "WinGet (Windows App Installer) is unavailable for this Windows user. " +
                $"Use Official download for {results[id].Name}, install it yourself, then Retry checks. " +
                "For npm, choose Node.js LTS and include the npm package manager feature.";
            return;
        }
        var result = results[id];
        var info = PrerequisiteInstall.CreateStartInfo(winget, result, true, true, HasCopilotOverride);
        if (MessageBox.Show(this,
            $"{PrerequisiteInstall.ActionName(result)} {result.Name} using WinGet?\n\n" +
            string.Join(" ", info.ArgumentList) + "\n\n" +
            "This changes software on your computer. Review any terms and UAC request " +
            "in the visible terminal/vendor installer. Peggy will not accept terms for you.",
            "Review prerequisite action", MessageBoxButton.OKCancel, MessageBoxImage.Information)
            != MessageBoxResult.OK) return;
        SetBusy(true);
        installing = true;
        string completion;
        try
        {
            summary.Text = $"Waiting for {result.Name}. Complete or cancel its visible installer.";
            var exit = await PrerequisiteInstall.RunAsync(info, checks.Token);
            completion = $"WinGet finished with exit code {exit}. ";
            if (exit != 0)
            {
                completion += "The action failed or was cancelled. If repair is unavailable, " +
                    "use the official installer to modify/repair Node.js (including npm) or the selected tool. ";
            }
        }
        catch (Exception ex) { completion = $"Dependency action did not complete: {ex.Message}. "; }
        finally { installing = false; SetBusy(false); }
        await CheckAsync();
        summary.Text = completion + summary.Text;
    }

    private void OpenGuidance(string id)
    {
        try
        {
            if (!InteractiveIdentity.CanInstallDependencies)
            {
                summary.Text = $"Open {PrerequisiteInstall.OfficialUrl(id)} in your normal user's browser.";
                return;
            }
            Process.Start(new ProcessStartInfo(PrerequisiteInstall.OfficialUrl(id)) { UseShellExecute = true });
        }
        catch (Exception ex) { summary.Text = $"Could not open guidance: {ex.Message}"; }
    }
}
