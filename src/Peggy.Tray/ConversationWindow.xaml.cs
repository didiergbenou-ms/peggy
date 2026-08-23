using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Peggy.Core;

namespace Peggy.Tray;

public sealed record TranscriptReviewResult(
    string? Text,
    bool RetryRequested);

public partial class ConversationWindow : Window
{
    private static readonly TimeSpan TranscriptAutoSubmitDelay =
        TimeSpan.FromSeconds(1);

    private string? _criticalAlertId;
    private TaskCompletionSource<TranscriptReviewResult>?
        _transcriptReview;
    private DispatcherTimer? _transcriptReviewTimer;
    private DateTimeOffset? _transcriptAutoSubmitUtc;
    private bool _suppressTranscriptTextChanges;
    private bool _ending;
    private string _assistantName = "Peggy";

    public ConversationWindow()
    {
        InitializeComponent();
        Closing += (_, args) =>
        {
            if (_ending)
            {
                return;
            }
            args.Cancel = true;
            Hide();
            ConversationClosed?.Invoke();
        };
    }

    public event Action? MicrophoneRequested;

    public event Action? ConversationClosed;

    public event Action? ApproveRequested;

    public event Action? CancelActionRequested;

    public event Action<string>? TypedConfirmationSubmitted;

    public event Action<string>? CriticalSwitchRequested;

    public void BeginConversation()
    {
        _ending = false;
        CancelTranscriptReview();
        ClearCritical();
        TranscriptPanel.Children.Clear();
        SetStatus("Ready", "Listen");
        Show();
        Activate();
    }

    public void EndConversation()
    {
        CancelTranscriptReview();
        ClearCritical();
        _ending = true;
        Hide();
        _ending = false;
    }

    public void AppendUser(string text)
        => AppendTranscript("You", text, Color.FromRgb(35, 92, 120));

    public void AppendAssistant(string text)
        => AppendTranscript(
            _assistantName,
            text,
            Color.FromRgb(113, 72, 44));

    public void SetListening()
        => SetStatus("Listening locally...", "Stop");

    public void ShowPartialTranscript(string transcript)
    {
        if (_transcriptReview is not null ||
            string.IsNullOrWhiteSpace(transcript))
        {
            return;
        }

        _suppressTranscriptTextChanges = true;
        TranscriptReviewText.Text = transcript;
        _suppressTranscriptTextChanges = false;
        TranscriptReviewText.IsReadOnly = true;
        TranscriptReviewCard.Visibility = Visibility.Visible;
        UseTranscriptButton.Visibility = Visibility.Collapsed;
        CancelTranscriptButton.Visibility = Visibility.Collapsed;
        TranscriptReviewCountdown.Text =
            "Listening locally. Nothing has been sent.";
    }

    public void SetThinking()
        => SetStatus("Thinking...", "Please wait");

    public void SetSpeaking()
        => SetStatus($"{_assistantName} is speaking", "Interrupt");

    public void SetReady()
        => SetStatus("Ready", "Listen");

    public async Task<TranscriptReviewResult> ReviewTranscriptAsync(
        string transcript,
        bool autoSubmit,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CancelTranscriptReview();
        var completion =
            new TaskCompletionSource<TranscriptReviewResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        _transcriptReview = completion;
        _suppressTranscriptTextChanges = true;
        TranscriptReviewText.Text = transcript;
        _suppressTranscriptTextChanges = false;
        TranscriptReviewText.IsReadOnly = false;
        TranscriptReviewCard.Visibility = Visibility.Visible;
        UseTranscriptButton.Visibility = Visibility.Visible;
        UseTranscriptButton.Content = autoSubmit
            ? "Send now"
            : "Use transcript";
        CancelTranscriptButton.Visibility = autoSubmit
            ? Visibility.Visible
            : Visibility.Collapsed;
        SetStatus(
            autoSubmit
                ? "Transcript ready"
                : "Review required before approval",
            "Please wait");
        TranscriptReviewText.Focus();
        TranscriptReviewText.SelectAll();
        if (autoSubmit)
        {
            StartTranscriptAutoSubmit(completion);
        }
        else
        {
            TranscriptReviewCountdown.Text =
                "Action approval transcripts require explicit review.";
        }

        using var registration = ct.Register(() =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(_transcriptReview, completion))
                {
                    HideTranscriptReview();
                    completion.TrySetCanceled(ct);
                }
            })));
        return await completion.Task;
    }

    private void StartTranscriptAutoSubmit(
        TaskCompletionSource<TranscriptReviewResult> completion)
    {
        StopTranscriptReviewTimer();
        _transcriptAutoSubmitUtc =
            DateTimeOffset.UtcNow.Add(TranscriptAutoSubmitDelay);
        _transcriptReviewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _transcriptReviewTimer.Tick += (_, _) =>
        {
            if (!ReferenceEquals(_transcriptReview, completion) ||
                _transcriptAutoSubmitUtc is null)
            {
                StopTranscriptReviewTimer();
                return;
            }

            var remaining =
                _transcriptAutoSubmitUtc.Value - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                TranscriptReviewCountdown.Text =
                    $"Sending in {remaining.TotalSeconds:0.0}s. " +
                    "Edit or cancel to pause.";
                return;
            }

            CompleteTranscriptReview(completion);
        };
        TranscriptReviewCountdown.Text =
            "Sending in 1.0s. Edit or cancel to pause.";
        _transcriptReviewTimer.Start();
    }

    public void ShowAction(WorkActionRequest request)
    {
        ActionDescription.Text = request.ExactDescription;
        NormalApprovalPanel.Visibility = Visibility.Visible;
        TypedApprovalPanel.Visibility = Visibility.Collapsed;
        TypedConfirmation.Clear();
        ActionCard.Visibility = Visibility.Visible;
    }

    public void RequireTypedConfirmation(WorkActionRequest request)
    {
        NormalApprovalPanel.Visibility = Visibility.Collapsed;
        TypedApprovalPanel.Visibility = Visibility.Visible;
        TypedInstruction.Text =
            "Type this exact phrase to complete the second confirmation: " +
            request.TypedConfirmationPhrase;
        TypedConfirmation.Clear();
        TypedConfirmation.Focus();
    }

    public void HideAction()
    {
        ActionCard.Visibility = Visibility.Collapsed;
        TypedConfirmation.Clear();
    }

    public void ShowCritical(AlertRecord alert, bool maskContent)
    {
        _criticalAlertId = alert.Id;
        CriticalText.Text = maskContent
            ? "A critical work item needs your attention."
            : alert.Summary;
        CriticalBanner.Visibility = Visibility.Visible;
    }

    public void ClearCritical()
    {
        _criticalAlertId = null;
        CriticalBanner.Visibility = Visibility.Collapsed;
    }

    private void AppendTranscript(
        string speaker,
        string text,
        Color color)
    {
        var border = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = speaker,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color),
            FontSize = 12
        });
        panel.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(45, 42, 39))
        });
        border.Child = panel;
        TranscriptPanel.Children.Add(border);
        Dispatcher.BeginInvoke(() =>
            TranscriptScroll.ScrollToEnd());
    }

    private void SetStatus(string status, string microphoneText)
    {
        StatusText.Text = status;
        MicrophoneButton.Content = microphoneText;
        MicrophoneButton.IsEnabled =
            !string.Equals(
                microphoneText,
                "Please wait",
                StringComparison.Ordinal);
    }

    private void UseTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (_transcriptReview is null)
        {
            return;
        }

        var completion = _transcriptReview;
        CompleteTranscriptReview(completion);
    }

    private void CompleteTranscriptReview(
        TaskCompletionSource<TranscriptReviewResult> completion)
    {
        var text = TranscriptReviewText.Text.Trim();
        if (text.Length == 0 ||
            !ReferenceEquals(_transcriptReview, completion))
        {
            PauseTranscriptAutoSubmit(
                "Transcript is empty. Edit or try again.");
            TranscriptReviewText.Focus();
            return;
        }

        HideTranscriptReview();
        completion.TrySetResult(new TranscriptReviewResult(
            text,
            RetryRequested: false));
    }

    private void CancelTranscript_Click(
        object sender,
        RoutedEventArgs e)
        => PauseTranscriptAutoSubmit(
            "Auto-send paused. Edit the transcript or send when ready.");

    private void TranscriptReviewText_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (!_suppressTranscriptTextChanges &&
            _transcriptReview is not null &&
            _transcriptReviewTimer is not null)
        {
            PauseTranscriptAutoSubmit(
                "Auto-send paused after your edit.");
        }
    }

    private void PauseTranscriptAutoSubmit(string status)
    {
        StopTranscriptReviewTimer();
        TranscriptReviewCountdown.Text = status;
        SetStatus($"Review what {_assistantName} heard", "Please wait");
        CancelTranscriptButton.Visibility = Visibility.Collapsed;
    }

    private void RetryTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (_transcriptReview is null)
        {
            return;
        }

        var completion = _transcriptReview;
        HideTranscriptReview();
        completion.TrySetResult(new TranscriptReviewResult(
            Text: null,
            RetryRequested: true));
    }

    private void CancelTranscriptReview()
    {
        var completion = _transcriptReview;
        HideTranscriptReview();
        completion?.TrySetCanceled();
    }

    private void HideTranscriptReview()
    {
        StopTranscriptReviewTimer();
        _transcriptReview = null;
        TranscriptReviewCard.Visibility = Visibility.Collapsed;
        TranscriptReviewCountdown.Text = string.Empty;
        CancelTranscriptButton.Visibility = Visibility.Collapsed;
        UseTranscriptButton.Visibility = Visibility.Visible;
        _suppressTranscriptTextChanges = true;
        TranscriptReviewText.Clear();
        _suppressTranscriptTextChanges = false;
    }

    private void StopTranscriptReviewTimer()
    {
        _transcriptReviewTimer?.Stop();
        _transcriptReviewTimer = null;
        _transcriptAutoSubmitUtc = null;
    }

    private void Microphone_Click(object sender, RoutedEventArgs e)
        => MicrophoneRequested?.Invoke();

    private void End_Click(object sender, RoutedEventArgs e)
    {
        EndConversation();
        ConversationClosed?.Invoke();
    }

    private void Approve_Click(object sender, RoutedEventArgs e)
        => ApproveRequested?.Invoke();

    private void CancelAction_Click(object sender, RoutedEventArgs e)
        => CancelActionRequested?.Invoke();

    private void TypedConfirm_Click(object sender, RoutedEventArgs e)
        => TypedConfirmationSubmitted?.Invoke(TypedConfirmation.Text);

    private void SwitchCritical_Click(object sender, RoutedEventArgs e)
    {
        if (_criticalAlertId is not null)
        {
            CriticalSwitchRequested?.Invoke(_criticalAlertId);
        }
        ClearCritical();
    }
}
