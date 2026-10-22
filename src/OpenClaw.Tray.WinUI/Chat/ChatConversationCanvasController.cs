using System.Net.WebSockets;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

internal sealed record ChatConversationCanvasSelection(
    ChatConversationIdentity Owner, ConversationCanvasContent Content, string? MessageId, string? RunId);

internal sealed record ChatConversationCanvasPrompt(
    ChatConversationCanvasSelection Selection, long Generation, string Text);

internal enum ChatConversationCanvasPromptResult { Accepted, Rejected, Stale }
internal enum ChatConversationCanvasPhase { Waiting, Loading, Loaded, Ready, Unsupported, Blocked, Error }

internal sealed record ChatConversationCanvasSnapshot(
    ChatConversationCanvasPhase Phase,
    ChatConversationPanelOperation? Operation = null,
    CanvasDocumentView? Document = null,
    string? EmbedSandboxMode = null,
    bool InteractionBlocked = false);

/// <summary>One selected document lifetime, subordinate to the foundation's current operation lease.</summary>
internal sealed class ChatConversationCanvasController : IDisposable
{
    internal const string PanelId = "canvas";
    private const int MaximumAutomaticAttempts = 8;
    private const int DefaultRetryDelayMilliseconds = 1000;
    private const int MaximumRetryDelayMilliseconds = 30000;
    private const int MaximumServerRetryMilliseconds = 300000;
    private readonly ChatConversationPanelState _panels;
    private readonly ChatConversationCanvasSelection _selection;
    private readonly Func<ChatConversationCanvasPrompt, CancellationToken, Task<ChatConversationCanvasPromptResult>> _submitPrompt;
    private readonly Func<TimeSpan, CancellationToken, Task> _waitForRetry;
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _promptTimes = new();
    private CancellationTokenSource? _readCancellation;
    private ChatConversationCanvasSnapshot _snapshot = new(ChatConversationCanvasPhase.Waiting);
    private long _revision;
    private bool _promptInFlight;
    private bool _disposed;

    internal ChatConversationCanvasController(ChatConversationPanelState panels,
        ChatConversationCanvasSelection selection,
        Func<ChatConversationCanvasPrompt, CancellationToken, Task<ChatConversationCanvasPromptResult>> submitPrompt,
        Func<TimeSpan, CancellationToken, Task>? waitForRetry = null)
    {
        _panels = panels;
        _selection = selection;
        _submitPrompt = submitPrompt;
        _waitForRetry = waitForRetry ?? (static (delay, token) => Task.Delay(delay, token));
        panels.Changed += OnBindingChanged;
        panels.Disposed += Dispose;
    }

    internal event Action? Changed;
    internal event Action? Invalidated;
    internal ChatConversationCanvasSnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal ChatConversationCanvasSelection Selection => _selection;
    internal void Close() => _panels.Close(PanelId);
    internal void InteractionBlocked()
    {
        var snapshot = Snapshot;
        if (!snapshot.InteractionBlocked && snapshot.Operation?.IsCurrent == true)
            PublishCurrent(snapshot with { InteractionBlocked = true });
    }

    internal async Task LoadAsync()
    {
        CancellationTokenSource? previous;
        var operation = _panels.BeginOperation(PanelId, _selection.RunId);
        long revision;
        CancellationTokenSource cancellation;
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed) return;
            revision = ++_revision;
            previous = _readCancellation;
            cancellation = operation is null ? new() :
                CancellationTokenSource.CreateLinkedTokenSource(operation.CancellationToken);
            token = cancellation.Token;
            _readCancellation = cancellation;
        }
        Cancel(previous);
        previous?.Dispose();
        Invalidated?.Invoke();
        if (operation is null || operation.Context.Identity != _selection.Owner || !operation.IsCurrent)
        {
            Publish(revision, new(ChatConversationCanvasPhase.Waiting));
            return;
        }
        if (_selection.Content.Kind != ConversationCanvasContentKind.Document ||
            !ConversationCanvasContent.IsDocumentId(_selection.Content.DocumentId))
        {
            Publish(revision, new(ChatConversationCanvasPhase.Unsupported, operation));
            return;
        }
        Publish(revision, new(ChatConversationCanvasPhase.Loading, operation));
        var backoffMilliseconds = int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_CANVAS_RETRY_DELAY_MS"),
            out var configuredDelay) && configuredDelay is >= 100 and <= MaximumRetryDelayMilliseconds
                ? configuredDelay : DefaultRetryDelayMilliseconds;
        for (var attempt = 0; attempt < MaximumAutomaticAttempts; attempt++)
        {
            if (!IsReadCurrent(revision, operation, token)) return;
            Exception retryError;
            try
            {
                var document = await operation.Context.Client.ViewCanvasDocumentAsync(
                    new(_selection.Content.DocumentId!, operation.Context.Identity.SessionKey),
                    operation.Context.ConnectionEpoch, token).ConfigureAwait(false);
                if (!IsReadCurrent(revision, operation, token)) return;
                Publish(revision, new(ChatConversationCanvasPhase.Loaded, operation, document, document.EmbedSandboxMode));
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (CanvasDocumentPendingException ex) { retryError = ex; }
            catch (GatewayRequestException ex) when (ex.DetailType == "canvas_widget_pending") { retryError = ex; }
            catch (TimeoutException ex) { retryError = ex; }
            catch (OperationCanceledException ex) { Fail(revision, operation, ChatConversationCanvasPhase.Error, ex); return; }
            catch (NotSupportedException ex) { Fail(revision, operation, ChatConversationCanvasPhase.Unsupported, ex); return; }
            catch (InvalidDataException ex) { Fail(revision, operation, ChatConversationCanvasPhase.Blocked, ex); return; }
            catch (InvalidOperationException ex) { Fail(revision, operation, ChatConversationCanvasPhase.Error, ex); return; }
            catch (IOException ex) { Fail(revision, operation, ChatConversationCanvasPhase.Error, ex); return; }
            catch (JsonException ex) { Fail(revision, operation, ChatConversationCanvasPhase.Blocked, ex); return; }
            catch (UriFormatException ex) { Fail(revision, operation, ChatConversationCanvasPhase.Blocked, ex); return; }
            catch (WebSocketException ex) { Fail(revision, operation, ChatConversationCanvasPhase.Error, ex); return; }
            if (!IsReadCurrent(revision, operation, token)) return;
            if (attempt + 1 == MaximumAutomaticAttempts)
            {
                Fail(revision, operation, ChatConversationCanvasPhase.Error, retryError);
                return;
            }
            Publish(revision, new(ChatConversationCanvasPhase.Waiting, operation));
            var delayMilliseconds = backoffMilliseconds;
            if (retryError is GatewayRequestException { Details: { ValueKind: JsonValueKind.Object } details } &&
                details.TryGetProperty("retryAfterMs", out var retryAfter) && retryAfter.ValueKind == JsonValueKind.Number &&
                retryAfter.TryGetInt32(out var hint) && hint > 0)
                delayMilliseconds = Math.Max(delayMilliseconds, Math.Min(hint, MaximumServerRetryMilliseconds));
            try
            {
                await _waitForRetry(TimeSpan.FromMilliseconds(delayMilliseconds), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            backoffMilliseconds = Math.Min(backoffMilliseconds * 2, MaximumRetryDelayMilliseconds);
        }
    }

    internal void UnexpectedFailure(Exception exception)
    {
        Logger.Warn($"[ConversationCanvas] Document operation failed ({exception.GetType().Name}).");
        if (Snapshot.Operation is { IsCurrent: true } operation) SurfaceFailed(operation);
    }

    internal void SurfaceReady(ChatConversationPanelOperation operation)
    {
        var snapshot = Snapshot;
        if (operation.IsCurrent && ReferenceEquals(snapshot.Operation, operation))
            PublishCurrent(snapshot with { Phase = ChatConversationCanvasPhase.Ready });
    }

    internal void SurfaceSuspended(ChatConversationPanelOperation operation)
    {
        if (operation.IsCurrent && ReferenceEquals(Snapshot.Operation, operation))
            PublishCurrent(Snapshot with { Phase = ChatConversationCanvasPhase.Loaded });
    }

    internal void SurfaceFailed(ChatConversationPanelOperation operation)
    {
        if (operation.IsCurrent && ReferenceEquals(Snapshot.Operation, operation))
        {
            PublishCurrent(Snapshot with { Phase = ChatConversationCanvasPhase.Blocked, Document = null });
            Invalidated?.Invoke();
        }
    }

    internal async Task<ChatConversationCanvasPromptResult> SubmitPromptAsync(
        string? raw, Func<bool> isInteractable, DateTimeOffset now)
    {
        var snapshot = Snapshot;
        if (snapshot.Phase != ChatConversationCanvasPhase.Ready ||
            snapshot.Operation is not { IsCurrent: true } operation || !isInteractable() ||
            ConversationCanvasBoundary.ConversationalPrompt(raw) is not { } text)
            return ChatConversationCanvasPromptResult.Rejected;
        lock (_gate)
        {
            while (_promptTimes.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromMinutes(1))
                _promptTimes.Dequeue();
            if (_disposed || _promptInFlight || _promptTimes.Count >= 10)
                return ChatConversationCanvasPromptResult.Rejected;
            _promptInFlight = true;
            _promptTimes.Enqueue(now);
        }
        try
        {
            if (!operation.IsCurrent || !isInteractable()) return ChatConversationCanvasPromptResult.Stale;
            var result = await _submitPrompt(new(_selection, operation.Generation, text),
                operation.CancellationToken).ConfigureAwait(false);
            return operation.IsCurrent ? result : ChatConversationCanvasPromptResult.Stale;
        }
        finally
        {
            lock (_gate) _promptInFlight = false;
        }
    }

    private void OnBindingChanged()
    {
        if (Snapshot.Operation?.IsCurrent == true) return;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            _revision++;
            cancellation = _readCancellation;
        }
        Cancel(cancellation);
        Volatile.Write(ref _snapshot, new(ChatConversationCanvasPhase.Waiting));
        Invalidated?.Invoke();
        Changed?.Invoke();
    }

    private void Fail(long revision, ChatConversationPanelOperation operation,
        ChatConversationCanvasPhase phase, Exception exception)
    {
        Logger.Warn($"[ConversationCanvas] Document read failed ({exception.GetType().Name}).");
        if (operation.IsCurrent) Publish(revision, new(phase, operation));
    }

    private bool IsReadCurrent(long revision, ChatConversationPanelOperation operation, CancellationToken token)
    {
        if (token.IsCancellationRequested || !operation.IsCurrent) return false;
        lock (_gate) return !_disposed && revision == _revision;
    }

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException)
        {
            // A superseding read finished retiring this captured source before cancellation.
        }
    }

    private void Publish(long revision, ChatConversationCanvasSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed || revision != _revision ||
                snapshot.Operation is { IsCurrent: false }) return;
            Volatile.Write(ref _snapshot, snapshot);
        }
        Changed?.Invoke();
    }

    private void PublishCurrent(ChatConversationCanvasSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed || snapshot.Operation?.IsCurrent != true) return;
            Volatile.Write(ref _snapshot, snapshot);
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _revision++;
            cancellation = _readCancellation;
            _readCancellation = null;
            Volatile.Write(ref _snapshot, new(ChatConversationCanvasPhase.Waiting));
        }
        _panels.Changed -= OnBindingChanged;
        _panels.Disposed -= Dispose;
        Cancel(cancellation);
        cancellation?.Dispose();
        Invalidated?.Invoke();
        Invalidated = null;
        Changed = null;
    }
}
