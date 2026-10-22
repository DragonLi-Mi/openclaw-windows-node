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
    private readonly ChatConversationPanelState _panels;
    private readonly ChatConversationCanvasSelection _selection;
    private readonly Func<ChatConversationCanvasPrompt, CancellationToken, Task<ChatConversationCanvasPromptResult>> _submitPrompt;
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _promptTimes = new();
    private CancellationTokenSource? _readCancellation;
    private ChatConversationCanvasSnapshot _snapshot = new(ChatConversationCanvasPhase.Waiting);
    private long _revision;
    private bool _promptInFlight;
    private bool _disposed;

    internal ChatConversationCanvasController(ChatConversationPanelState panels,
        ChatConversationCanvasSelection selection,
        Func<ChatConversationCanvasPrompt, CancellationToken, Task<ChatConversationCanvasPromptResult>> submitPrompt)
    {
        _panels = panels;
        _selection = selection;
        _submitPrompt = submitPrompt;
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
        if (Snapshot.Operation?.IsCurrent == true)
            PublishCurrent(Snapshot with { InteractionBlocked = true });
    }

    internal async Task LoadAsync()
    {
        CancellationTokenSource? previous;
        var operation = _panels.BeginOperation(PanelId, _selection.RunId);
        long revision;
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_disposed) return;
            revision = ++_revision;
            previous = _readCancellation;
            cancellation = operation is null ? new() :
                CancellationTokenSource.CreateLinkedTokenSource(operation.CancellationToken);
            _readCancellation = cancellation;
        }
        previous?.Cancel();
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
        try
        {
            if (!operation.IsCurrent) return;
            var document = await operation.Context.Client.ViewCanvasDocumentAsync(
                new(_selection.Content.DocumentId!, operation.Context.Identity.SessionKey),
                operation.Context.ConnectionEpoch, cancellation.Token).ConfigureAwait(false);
            if (!operation.IsCurrent || cancellation.IsCancellationRequested) return;
            Publish(revision, new(ChatConversationCanvasPhase.Loaded, operation, document));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Publish(revision, new(ChatConversationCanvasPhase.Waiting));
        }
        catch (OperationCanceledException ex) { Fail(revision, operation, ChatConversationCanvasPhase.Error, ex); }
        catch (NotSupportedException ex) { Fail(revision, operation, ChatConversationCanvasPhase.Unsupported, ex); }
        catch (InvalidDataException ex) { Fail(revision, operation, ChatConversationCanvasPhase.Blocked, ex); }
        catch (InvalidOperationException ex) { Fail(revision, operation, ChatConversationCanvasPhase.Error, ex); }
        catch (TimeoutException ex) { Fail(revision, operation, ChatConversationCanvasPhase.Error, ex); }
        catch (IOException ex) { Fail(revision, operation, ChatConversationCanvasPhase.Error, ex); }
        catch (JsonException ex) { Fail(revision, operation, ChatConversationCanvasPhase.Blocked, ex); }
        catch (UriFormatException ex) { Fail(revision, operation, ChatConversationCanvasPhase.Blocked, ex); }
        catch (WebSocketException ex) { Fail(revision, operation, ChatConversationCanvasPhase.Error, ex); }
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
        cancellation?.Cancel();
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
        cancellation?.Cancel();
        cancellation?.Dispose();
        Invalidated?.Invoke();
        Invalidated = null;
        Changed = null;
    }
}
