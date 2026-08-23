using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Peggy.Core;

namespace Peggy.Tray;

public sealed class ConversationController : IAsyncDisposable
{
    private readonly ConversationWindow _window;
    private readonly MascotWindow _mascot;
    private readonly PeggyStateManager _state;
    private readonly AlertManager _alerts;
    private readonly Func<CopilotRunner> _getCopilot;
    private readonly Func<IMessageProvider> _getProvider;
    private readonly Func<string?, IReadOnlyCollection<Message>> _getFocusMessages;
    private readonly Func<bool> _shouldSuspend;
    private readonly Func<ISpeechProvider> _createSpeech;
    private readonly Action _restoreRestingState;
    private readonly CancellationToken _shutdown;
    private CancellationTokenSource? _conversationCts;
    private CancellationTokenSource? _activeOperationCts;
    private ISpeechProvider? _speech;
    private WorkActionRequest? _pendingAction;
    private string? _activeAlertId;
    private bool _voiceApprovalEligible;
    private bool _turnInProgress;
    private bool _userInterruptedTurn;
    private bool _listenAfterInterrupt;
    private ApprovalMethod? _deferredApproval;
    private string? _deferredActionId;
    private Task _activeTurn = Task.CompletedTask;
    private bool _disposed;

    public ConversationController(
        ConversationWindow window,
        MascotWindow mascot,
        PeggyStateManager state,
        AlertManager alerts,
        Func<CopilotRunner> getCopilot,
        Func<IMessageProvider> getProvider,
        Func<string?, IReadOnlyCollection<Message>> getFocusMessages,
        Func<bool> shouldSuspend,
        CancellationToken shutdown,
        Func<ISpeechProvider>? createSpeech = null,
        Action? restoreRestingState = null)
    {
        _window = window;
        _mascot = mascot;
        _state = state;
        _alerts = alerts;
        _getCopilot = getCopilot;
        _getProvider = getProvider;
        _getFocusMessages = getFocusMessages;
        _shouldSuspend = shouldSuspend;
        _createSpeech = createSpeech ??
            (() => new LocalSpeechService(_state.State.Settings));
        _restoreRestingState = restoreRestingState ??
            (() => _mascot.SetState(PeggyState.Idle));
        _shutdown = shutdown;

        _window.MicrophoneRequested += OnMicrophoneRequested;
        _window.ConversationClosed += EndConversation;
        _window.ApproveRequested += OnClickApprove;
        _window.CancelActionRequested += OnCancelAction;
        _window.TypedConfirmationSubmitted += OnTypedConfirmation;
        _window.CriticalSwitchRequested += OnCriticalSwitchRequested;
    }

    public bool IsActive => _conversationCts is not null;

    public async Task StartAsync(string? alertId = null)
    {
        ThrowIfDisposed();
        EndConversation();
        await WaitForActiveTurnAsync();
        if (_shouldSuspend())
        {
            throw new InvalidOperationException(
                "Voice is unavailable while the Windows session is locked " +
                "or notifications are being suppressed.");
        }
        if (!_state.State.Settings.SpeechEnabled)
        {
            throw new InvalidOperationException(
                "Local voice is disabled in assistant settings.");
        }
        _speech ??= _createSpeech();
        _conversationCts = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdown);
        if (_shouldSuspend())
        {
            EndConversation();
            throw new InvalidOperationException(
                "Voice is unavailable while the Windows session is locked " +
                "or notifications are being suppressed.");
        }
        _activeAlertId = alertId;
        _pendingAction = null;
        _voiceApprovalEligible = false;
        _deferredApproval = null;
        _deferredActionId = null;
        _window.BeginConversation();
        _window.HideAction();
        _window.SetThinking();
        _mascot.SetState(PeggyState.Thinking);
        try
        {
            await _speech.WarmUpAsync(_conversationCts.Token);
        }
        catch
        {
            EndConversation();
            throw;
        }

        string? greeting = null;
        if (alertId is not null)
        {
            var alert = _state.State.Alerts.FirstOrDefault(item =>
                string.Equals(item.Id, alertId, StringComparison.Ordinal));
            if (alert is not null)
            {
                greeting = $"{alert.Summary} {alert.Reason}";
            }
        }
        if (greeting is not null)
        {
            _window.AppendAssistant(greeting);
            _activeTurn = SpeakThenListenAsync(greeting);
            await _activeTurn;
            return;
        }

        _window.SetListening();
        _mascot.SetState(PeggyState.Listening);
        QueueListeningTurn();
    }

    public void ShowCritical(AlertRecord alert, bool maskContent)
    {
        if (IsActive)
        {
            _window.ShowCritical(alert, maskContent);
        }
    }

    public void EndConversation()
    {
        _conversationCts?.Cancel();
        _activeOperationCts?.Cancel();
        _conversationCts?.Dispose();
        _conversationCts = null;
        var speech = _speech;
        _speech = null;
        speech?.CancelSpeaking();
        speech?.CancelListening();
        speech?.Dispose();
        _pendingAction = null;
        _voiceApprovalEligible = false;
        _deferredApproval = null;
        _deferredActionId = null;
        _activeAlertId = null;
        _listenAfterInterrupt = false;
        _window.HideAction();
        _window.EndConversation();
        _restoreRestingState();
    }

    public void UnloadSpeech()
        => EndConversation();

    public async Task StopAsync()
    {
        EndConversation();
        await WaitForActiveTurnAsync();
    }

    private async void OnMicrophoneRequested()
    {
        if (!IsActive)
        {
            return;
        }

        if (_speech?.IsSpeaking == true)
        {
            _userInterruptedTurn = true;
            _listenAfterInterrupt = true;
            _activeOperationCts?.Cancel();
            _speech.CancelSpeaking();
            return;
        }
        if (_speech?.IsListening == true)
        {
            _userInterruptedTurn = true;
            _speech.CancelListening();
            return;
        }
        if (_turnInProgress)
        {
            _userInterruptedTurn = true;
            _listenAfterInterrupt = true;
            _activeOperationCts?.Cancel();
            _speech?.CancelSpeaking();
            _speech?.CancelListening();
            return;
        }

        await RunListeningTurnAsync();
    }

    private async Task ListenOnceAsync()
    {
        if (_conversationCts is null || _turnInProgress)
        {
            return;
        }
        if (_shouldSuspend())
        {
            EndConversation();
            return;
        }

        _turnInProgress = true;
        _userInterruptedTurn = false;
        using var operationCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                _conversationCts.Token);
        _activeOperationCts = operationCts;
        var ct = operationCts.Token;
        try
        {
            _window.SetListening();
            _mascot.SetState(PeggyState.Listening);
            string? recognized;
            while (true)
            {
                recognized = await _speech!.RecognizeOnceAsync(
                    TimeSpan.FromSeconds(30),
                    partial => _ = _window.Dispatcher.BeginInvoke(
                        new Action(() =>
                            _window.ShowPartialTranscript(partial))),
                    ct);
                if (string.IsNullOrWhiteSpace(recognized))
                {
                    EndConversation();
                    return;
                }

                var review = await _window.ReviewTranscriptAsync(
                    recognized,
                    autoSubmit: ShouldAutoSubmitTranscript(),
                    ct: ct);
                if (!review.RetryRequested)
                {
                    recognized = review.Text
                        ?? throw new InvalidDataException(
                            "The accepted speech transcript was empty.");
                    break;
                }

                _window.SetListening();
                _mascot.SetState(PeggyState.Listening);
            }

            _window.AppendUser(recognized);
            if (IsGoodbye(recognized))
            {
                const string goodbye = "Goodbye.";
                _window.AppendAssistant(goodbye);
                await SpeakOnlyAsync(goodbye, ct);
                EndConversation();
                return;
            }

            if (_pendingAction is not null && _voiceApprovalEligible)
            {
                _voiceApprovalEligible = false;
                await HandleImmediateApprovalTurnAsync(recognized, ct);
                return;
            }

            await ProcessConversationTurnAsync(recognized, ct);
        }
        catch (OperationCanceledException) when (
            _userInterruptedTurn ||
            _conversationCts is null ||
            _conversationCts.IsCancellationRequested ||
            _shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var text = $"I could not complete that voice turn: {ex.Message}";
            _window.AppendAssistant(text);
            await TrySpeakAsync(text, ct);
        }
        finally
        {
            _turnInProgress = false;
            if (ReferenceEquals(
                    _activeOperationCts,
                    operationCts))
            {
                _activeOperationCts = null;
            }
            var deferredApproval = TakeDeferredApproval();
            var restartListening = _listenAfterInterrupt;
            _listenAfterInterrupt = false;
            _userInterruptedTurn = false;
            if (deferredApproval is not null)
            {
                _turnInProgress = true;
                try
                {
                    await ExecutePendingActionAsync(
                        deferredApproval.Value,
                        _conversationCts!.Token);
                }
                finally
                {
                    _turnInProgress = false;
                }
            }

            if (IsActive && _pendingAction is null)
            {
                _window.SetReady();
            }
            if (restartListening && IsActive)
            {
                QueueListeningTurn();
            }
        }
    }

    private bool ShouldAutoSubmitTranscript()
        => _pendingAction is null || !_voiceApprovalEligible;

    private async Task ProcessConversationTurnAsync(
        string recognized,
        CancellationToken ct)
    {
        _window.SetThinking();
        _mascot.SetState(PeggyState.Thinking);
        var activeAlerts = _alerts.GetActiveAlerts();
        var speechQueue = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        var sentences = new StreamingSentenceBuffer(sentence =>
            speechQueue.Writer.TryWrite(sentence));
        var streamedSpeech = ConsumeStreamedSpeechAsync(
            speechQueue.Reader,
            ct);
        CopilotConversationRunResult result;
        try
        {
            result = await _getCopilot().RunConversationAsync(
                recognized,
                activeAlerts,
                _getFocusMessages(_activeAlertId),
                onTextDelta: sentences.Append,
                ct: ct);
            sentences.Complete(result.Text);
            speechQueue.Writer.TryComplete();
            _window.AppendAssistant(result.Text);
            _state.RecordCredit(
                "conversation",
                result.Usage.Model,
                result.Usage.OperationCredits,
                result.Usage.SessionCredits,
                DateTimeOffset.UtcNow);
            await _state.SaveAsync(ct);
            await streamedSpeech;
        }
        catch (Exception ex)
        {
            speechQueue.Writer.TryComplete(ex);
            try
            {
                await streamedSpeech;
            }
            catch
            {
            }
            throw;
        }
        _deferredApproval = null;
        _deferredActionId = null;
        _pendingAction = result.ProposedAction;
        if (_pendingAction is not null)
        {
            _window.ShowAction(_pendingAction);
            _voiceApprovalEligible = true;
            var approval =
                $"{_pendingAction.ExactDescription} Do you approve?";
            _window.AppendAssistant(approval);
            await SpeakThenListenAsync(approval);
            return;
        }

        QueueListeningTurn();
    }

    private async Task ConsumeStreamedSpeechAsync(
        ChannelReader<string> sentences,
        CancellationToken ct)
    {
        await foreach (var sentence in sentences.ReadAllAsync(ct))
        {
            await SpeakOnlyAsync(sentence, ct);
        }
    }

    private async Task HandleImmediateApprovalTurnAsync(
        string recognized,
        CancellationToken ct)
    {
        if (_pendingAction is null)
        {
            return;
        }

        if (IsNaturalApproval(recognized))
        {
            if (_pendingAction.IsDestructive)
            {
                _window.RequireTypedConfirmation(_pendingAction);
                const string typed =
                    "This destructive action needs the separate typed confirmation shown on screen.";
                _window.AppendAssistant(typed);
                await SpeakOnlyAsync(typed, ct);
                _window.SetReady();
                return;
            }

            await ExecutePendingActionAsync(ApprovalMethod.Voice, ct);
            return;
        }

        if (IsNaturalRejection(recognized))
        {
            OnCancelAction();
            const string cancelled = "I cancelled the proposed action.";
            _window.AppendAssistant(cancelled);
            await SpeakThenListenAsync(cancelled);
            return;
        }

        var retry =
            $"I did not hear a clear approval. {_pendingAction.ExactDescription} " +
            "Do you approve?";
        _window.AppendAssistant(retry);
        _voiceApprovalEligible = true;
        await SpeakThenListenAsync(retry);
    }

    private async void OnClickApprove()
    {
        if (_pendingAction is null ||
            _conversationCts is null)
        {
            return;
        }

        _voiceApprovalEligible = false;
        if (_pendingAction.IsDestructive)
        {
            InterruptCurrentTurn();
            _window.RequireTypedConfirmation(_pendingAction);
            _window.SetReady();
            return;
        }

        if (_turnInProgress)
        {
            _deferredApproval = ApprovalMethod.Click;
            _deferredActionId = _pendingAction.Id;
            InterruptCurrentTurn();
            return;
        }

        _turnInProgress = true;
        try
        {
            _activeTurn = ExecutePendingActionAsync(
                ApprovalMethod.Click,
                _conversationCts.Token);
            await _activeTurn;
        }
        finally
        {
            _turnInProgress = false;
        }
    }

    private async void OnTypedConfirmation(string phrase)
    {
        if (_pendingAction is null ||
            !_pendingAction.IsDestructive ||
            _conversationCts is null)
        {
            return;
        }

        if (!string.Equals(
                phrase.Trim(),
                _pendingAction.TypedConfirmationPhrase,
                StringComparison.Ordinal))
        {
            _window.AppendAssistant(
                "The typed confirmation did not exactly match. Nothing was changed.");
            return;
        }

        if (_turnInProgress)
        {
            _deferredApproval = ApprovalMethod.TypedConfirmation;
            _deferredActionId = _pendingAction.Id;
            InterruptCurrentTurn();
            return;
        }

        _turnInProgress = true;
        try
        {
            _activeTurn = ExecutePendingActionAsync(
                ApprovalMethod.TypedConfirmation,
                _conversationCts.Token);
            await _activeTurn;
        }
        finally
        {
            _turnInProgress = false;
        }
    }

    private async Task ExecutePendingActionAsync(
        ApprovalMethod method,
        CancellationToken ct)
    {
        var action = _pendingAction
            ?? throw new InvalidOperationException(
                "There is no pending assistant action.");
        _pendingAction = null;
        _voiceApprovalEligible = false;
        _window.HideAction();
        _window.SetThinking();
        _mascot.SetState(PeggyState.Thinking);
        string? response = null;
        try
        {
            var result = await _getProvider().ExecuteActionAsync(action, ct);
            var verified = result.Success && result.Verified;
            var auditSaved = await RecordAuditAsync(
                action.Kind,
                GetTargetIds(action),
                method,
                result.Detail,
                verified);
            response = verified
                ? "The action completed and I verified the result."
                : "The action did not pass verification, so I will not claim it succeeded.";
            if (!auditSaved)
            {
                response += " I could not persist the local audit entry, but I will retry when the assistant exits.";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await RecordAuditAsync(
                action.Kind,
                GetTargetIds(action),
                method,
                "cancelled_before_verification",
                verified: false);
            response =
                "The action was interrupted before I could verify the result. " +
                "I will not retry it automatically or report success.";
        }
        catch (Exception ex)
        {
            var auditSaved = await RecordAuditAsync(
                action.Kind,
                GetTargetIds(action),
                method,
                ex.GetType().Name,
                verified: false);
            response =
                "The action failed or could not be verified, so I will not retry it " +
                $"automatically or report success: {ex.Message}";
            if (!auditSaved)
            {
                response += " The local audit entry could not be persisted yet.";
            }
        }

        if (IsActive && response is not null)
        {
            _window.AppendAssistant(response);
            await TrySpeakThenListenAsync(response);
        }
    }

    private async Task<bool> RecordAuditAsync(
        WorkActionKind action,
        IEnumerable<string> targetIds,
        ApprovalMethod method,
        string result,
        bool verified)
    {
        _state.RecordAudit(
            action,
            targetIds,
            method,
            result,
            verified,
            DateTimeOffset.UtcNow);
        try
        {
            await _state.SaveAsync();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void OnCancelAction()
    {
        _deferredApproval = null;
        _deferredActionId = null;
        if (_turnInProgress)
        {
            InterruptCurrentTurn();
        }
        _pendingAction = null;
        _voiceApprovalEligible = false;
        _window.HideAction();
        _window.SetReady();
    }

    private async Task SwitchToAlertAsync(string alertId)
    {
        ClearPendingAction();
        if (_turnInProgress || !_activeTurn.IsCompleted)
        {
            InterruptCurrentTurn();
            await WaitForActiveTurnAsync();
            _userInterruptedTurn = false;
            ClearPendingAction();
        }
        _activeAlertId = alertId;
        var alert = _state.State.Alerts.FirstOrDefault(item =>
            string.Equals(item.Id, alertId, StringComparison.Ordinal));
        if (alert is null || _conversationCts is null)
        {
            return;
        }

        var text = $"{alert.Summary} {alert.Reason}";
        _window.AppendAssistant(text);
        await SpeakThenListenAsync(text);
    }

    private async void OnCriticalSwitchRequested(string alertId)
    {
        try
        {
            await SwitchToAlertAsync(alertId);
        }
        catch (OperationCanceledException) when (!IsActive)
        {
        }
        catch (Exception ex)
        {
            if (IsActive)
            {
                _window.AppendAssistant(
                    $"I could not switch to that alert: {ex.Message}");
                _window.SetReady();
            }
        }
    }

    private async Task SpeakThenListenAsync(string text)
    {
        if (_conversationCts is null)
        {
            return;
        }
        if (_shouldSuspend())
        {
            EndConversation();
            return;
        }

        await SpeakOnlyAsync(text, _conversationCts.Token);
        QueueListeningTurn();
    }

    private void QueueListeningTurn()
    {
        if (_conversationCts is not null &&
            (_pendingAction is null ||
             _voiceApprovalEligible))
        {
            _ = _window.Dispatcher.BeginInvoke(
                new Action(async () => await RunListeningTurnAsync()));
        }
    }

    private async Task SpeakOnlyAsync(string text, CancellationToken ct)
    {
        _window.SetSpeaking();
        _mascot.SetState(PeggyState.Idle);
        await _speech!.SpeakAsync(text, ct);
    }

    private async Task TrySpeakAsync(string text, CancellationToken ct)
    {
        try
        {
            await SpeakOnlyAsync(text, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (IsActive)
            {
                _window.SetReady();
            }
        }
    }

    private async Task TrySpeakThenListenAsync(string text)
    {
        try
        {
            await SpeakThenListenAsync(text);
        }
        catch (OperationCanceledException) when (!IsActive)
        {
        }
        catch (Exception ex)
        {
            if (IsActive)
            {
                _window.AppendAssistant(
                    $"I could not use local speech: {ex.Message}");
                _window.SetReady();
            }
        }
    }

    private static IReadOnlyList<string> GetTargetIds(WorkActionRequest action)
        => new[]
        {
            action.TargetId,
            action.RelatedMessageId
        }
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .ToList();

    private static bool IsGoodbye(string value)
        => Normalize(value) is "goodbye" or "bye" or "good bye";

    private static bool IsNaturalApproval(string value)
    {
        var normalized = Normalize(value);
        return normalized is
            "yes" or
            "yes please" or
            "approve" or
            "approved" or
            "do it" or
            "go ahead" or
            "please do" or
            "that is correct" or
            "thats correct";
    }

    private static bool IsNaturalRejection(string value)
    {
        var normalized = Normalize(value);
        return normalized is
            "no" or
            "cancel" or
            "do not" or
            "dont" or
            "never mind" or
            "nevermind";
    }

    private static string Normalize(string value)
        => string.Join(
            " ",
            new string(value
                .Trim()
                .ToLowerInvariant()
                .Where(character =>
                    char.IsLetterOrDigit(character) ||
                    char.IsWhiteSpace(character))
                .ToArray())
            .Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries));

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        EndConversation();
        await WaitForActiveTurnAsync();
        try
        {
            _speech?.Dispose();
            _speech = null;
        }
        finally
        {
            _disposed = true;
        }
    }

    private void InterruptCurrentTurn()
    {
        _userInterruptedTurn = true;
        _activeOperationCts?.Cancel();
        _speech?.CancelSpeaking();
        _speech?.CancelListening();
    }

    private void ClearPendingAction()
    {
        _deferredApproval = null;
        _deferredActionId = null;
        _pendingAction = null;
        _voiceApprovalEligible = false;
        _window.HideAction();
    }

    private ApprovalMethod? TakeDeferredApproval()
    {
        var approval = IsActive &&
                       _pendingAction is not null &&
                       string.Equals(
                           _pendingAction.Id,
                           _deferredActionId,
                           StringComparison.Ordinal)
            ? _deferredApproval
            : null;
        _deferredApproval = null;
        _deferredActionId = null;
        return approval;
    }

    private async Task RunListeningTurnAsync()
    {
        var previous = _activeTurn;
        if (!previous.IsCompleted)
        {
            await previous;
        }
        if (!IsActive || _turnInProgress)
        {
            return;
        }

        _activeTurn = ListenOnceAsync();
        await _activeTurn;
    }

    private async Task WaitForActiveTurnAsync()
    {
        try
        {
            await _activeTurn;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // The user can recover from a failed speech turn by starting a
            // new conversation, and shutdown must never remain wedged.
        }
        finally
        {
            _activeTurn = Task.CompletedTask;
        }
    }
}
