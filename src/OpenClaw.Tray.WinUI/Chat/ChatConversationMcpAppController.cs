using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

/// <summary>Subordinate to the canonical panel binding. Missing integration never mounts a blank App.</summary>
internal sealed class ChatConversationMcpAppController : IDisposable
{
    private readonly ChatConversationPanelState _panels;
    private readonly IChatConversationMcpAppActions? _actions;
    private readonly IChatConversationMcpAppEvents? _events;
    private readonly object _gate = new();
    private CancellationTokenSource? _read;
    private long _revision;
    private bool _disposed;
    private ChatConversationMcpAppSnapshot _snapshot = new(ChatConversationMcpAppPhase.Waiting);

    internal ChatConversationMcpAppController(ChatConversationPanelState panels,
        ChatConversationCanvasSelection selection, IChatConversationMcpAppActions? actions,
        IChatConversationMcpAppEvents? events)
    {
        _panels = panels;
        Selection = selection;
        _actions = actions;
        _events = events;
        panels.Changed += BindingChanged;
        panels.Disposed += Dispose;
    }

    internal ChatConversationCanvasSelection Selection { get; }
    internal ChatConversationMcpAppSnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal event Action? Changed;
    internal event Action? Invalidated;
    internal void Close() => _panels.Close(ChatConversationCanvasController.PanelId);

    internal async Task LoadAsync()
    {
        long revision;
        CancellationTokenSource? previous;
        lock (_gate)
        {
            if (_disposed) return;
            revision = ++_revision;
            previous = _read;
            _read = null;
        }
        Cancel(previous);
        previous?.Dispose();
        Invalidated?.Invoke();
        if (Selection.Content.Kind != ConversationCanvasContentKind.McpApp ||
            Selection.Content.McpApp is not { ViewId.Length: > 0 })
        {
            PublishRead(revision, new(ChatConversationMcpAppPhase.Blocked));
            return;
        }
        // No caller can opt out of the account/content gate by supplying a successful callback.
        var operation = ChatConversationMcpAppIntegration.AcquireContentOperation(_panels, Selection);
        if (operation is null || _actions is null || _events is null)
        {
            PublishRead(revision, new(ChatConversationMcpAppPhase.Unavailable));
            return;
        }
        if (!operation.IsCurrent || operation.Context.Identity != Selection.Owner)
        {
            PublishRead(revision, new(ChatConversationMcpAppPhase.Waiting));
            return;
        }
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed || revision != _revision || !operation.IsCurrent) return;
            _read = CancellationTokenSource.CreateLinkedTokenSource(operation.CancellationToken);
            token = _read.Token;
        }
        PublishRead(revision, new(ChatConversationMcpAppPhase.Loading, operation));
        try
        {
            var scope = new ConversationMcpAppScope(Selection.Owner.SessionKey,
                Selection.Content.McpApp.ViewId, Selection.Owner.AgentId);
            var view = await operation.Context.Client.ViewConversationMcpAppAsync(scope,
                operation.Context.ConnectionEpoch, token);
            if (Current(revision, operation, token))
                PublishRead(revision, new(ChatConversationMcpAppPhase.Loaded, operation, view));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException or
            TimeoutException or OperationCanceledException or System.Net.WebSockets.WebSocketException or NotSupportedException)
        {
            if (Current(revision, operation, token))
                PublishRead(revision, new(IsExpired(ex) ? ChatConversationMcpAppPhase.Expired :
                    ex is NotSupportedException ? ChatConversationMcpAppPhase.Unavailable :
                    ChatConversationMcpAppPhase.Error, operation));
        }
    }

    internal ChatConversationMcpAppSession CreateSession(ChatConversationPanelOperation operation,
        ConversationMcpAppView view, Func<bool> interactable, Action<JsonElement> send, Action initialized)
    {
        if (_disposed || !operation.IsCurrent || !ReferenceEquals(Snapshot.Operation, operation) ||
            !ReferenceEquals(Snapshot.View, view) || _actions is null || _events is null)
            throw new InvalidOperationException("The current App content binding is not available.");
        var session = new ChatConversationMcpAppSession(new(Selection, operation), view, _actions, _events,
            interactable, send, Notice, Presentation, initialized);
        session.Expired += () =>
        {
            if (!operation.IsCurrent) return;
            Publish(Snapshot with { Phase = ChatConversationMcpAppPhase.Expired, View = null });
            Invalidated?.Invoke();
        };
        return session;
    }

    internal void SurfaceReady(ChatConversationPanelOperation operation)
    {
        if (!_disposed && operation.IsCurrent && ReferenceEquals(Snapshot.Operation, operation))
            Publish(Snapshot with { Phase = ChatConversationMcpAppPhase.Ready });
    }

    internal void SurfaceSuspended(ChatConversationPanelOperation operation)
    {
        if (!_disposed && operation.IsCurrent && ReferenceEquals(Snapshot.Operation, operation))
            Publish(Snapshot with { Phase = ChatConversationMcpAppPhase.Loaded });
    }

    internal void SurfaceFailed(ChatConversationPanelOperation operation)
    {
        if (!_disposed && operation.IsCurrent && ReferenceEquals(Snapshot.Operation, operation))
        {
            Publish(Snapshot with { Phase = ChatConversationMcpAppPhase.Blocked, View = null });
            Invalidated?.Invoke();
        }
    }

    internal void Notice(ChatConversationMcpAppNotice notice)
    {
        if (notice == ChatConversationMcpAppNotice.None && Snapshot.Notice == ChatConversationMcpAppNotice.Unknown) return;
        if (!_disposed && Snapshot.Operation?.IsCurrent == true && Snapshot.Notice != notice)
            Publish(Snapshot with { Notice = notice });
    }

    private void Presentation(string mode, double height)
    {
        if (!_disposed && Snapshot.Operation?.IsCurrent == true)
            Publish(Snapshot with { DisplayMode = mode, Height = height });
    }

    internal void UnexpectedFailure(Exception exception)
    {
        Logger.Warn($"[ConversationMcpApp] Content host failed ({exception.GetType().Name}).");
        if (Snapshot.Operation is { } operation) SurfaceFailed(operation);
    }

    private bool Current(long revision, ChatConversationPanelOperation operation, CancellationToken token)
    {
        lock (_gate)
            return !_disposed && revision == _revision && operation.IsCurrent && !token.IsCancellationRequested;
    }
    private static bool IsExpired(Exception exception) =>
        exception is GatewayRequestException { Code: "UNAVAILABLE", Details: { ValueKind: JsonValueKind.Object } details } &&
        details.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String &&
        code.GetString() == "MCP_APP_VIEW_EXPIRED";
    private void PublishRead(long revision, ChatConversationMcpAppSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed || revision != _revision || snapshot.Operation?.IsCurrent == false) return;
            Volatile.Write(ref _snapshot, snapshot);
        }
        Changed?.Invoke();
    }
    private void Publish(ChatConversationMcpAppSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed || snapshot.Operation?.IsCurrent != true ||
                !ReferenceEquals(Snapshot.Operation, snapshot.Operation)) return;
            Volatile.Write(ref _snapshot, snapshot);
        }
        Changed?.Invoke();
    }
    private void BindingChanged()
    {
        CancellationTokenSource? read;
        lock (_gate)
        {
            if (_disposed || Snapshot.Operation?.IsCurrent == true) return;
            _revision++;
            read = _read;
            Volatile.Write(ref _snapshot, new(ChatConversationMcpAppPhase.Waiting));
        }
        Cancel(read);
        Invalidated?.Invoke();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        CancellationTokenSource? read;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _revision++;
            read = _read;
            _read = null;
            Volatile.Write(ref _snapshot, new(ChatConversationMcpAppPhase.Waiting));
        }
        _panels.Changed -= BindingChanged;
        _panels.Disposed -= Dispose;
        Cancel(read);
        read?.Dispose();
        Invalidated?.Invoke();
    }

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
