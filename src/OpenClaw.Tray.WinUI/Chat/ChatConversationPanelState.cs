using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

internal sealed record ChatConversationIdentity(
    string GatewayId,
    string SigningDeviceId,
    string SessionKey,
    string? SessionId,
    string? AgentId);

/// <summary>
/// Captured from the selected session on the authenticated, manager-owned client.
/// Location metadata is not filesystem authority. Consumers must resolve the session's source.
/// </summary>
internal sealed record ChatConversationContext(
    ChatConversationIdentity Identity,
    IOperatorGatewayClient Client,
    long ConnectionEpoch,
    string? ExecNodeId = null,
    string? WorktreeId = null)
{
    internal static ChatConversationContext? Capture(
        string? gatewayId, IOperatorGatewayClient client, SessionInfo? session)
    {
        var signingDeviceId = client.AuthenticatedSigningDeviceId;
        if (string.IsNullOrWhiteSpace(gatewayId) ||
            !client.IsConnectedToGateway ||
            client.SessionMutationConnectionEpoch is not { } epoch ||
            string.IsNullOrWhiteSpace(signingDeviceId) ||
            session is null || string.IsNullOrWhiteSpace(session.Key))
            return null;

        return new(new(gatewayId, signingDeviceId, session.Key,
            session.SessionId, session.AgentId), client, epoch, session.ExecNode, session.Worktree?.Id);
    }

    internal bool IsTransportCurrent => Client.IsConnectedToGateway &&
        Client.SessionMutationConnectionEpoch is { } epoch && epoch == ConnectionEpoch &&
        !string.IsNullOrWhiteSpace(Client.AuthenticatedSigningDeviceId) &&
        string.Equals(Client.AuthenticatedSigningDeviceId, Identity.SigningDeviceId, StringComparison.Ordinal);
}

internal sealed record ChatConversationPanelLayout(
    IReadOnlyList<string> OpenPanels, string? SelectedPanel, double Width = 440)
{
    internal static ChatConversationPanelLayout Empty { get; } = new([], null);
}

/// <summary>
/// One mounted conversation host's retained layout and cancellable selection lifetime.
/// A lease fences local work, not a Gateway mutation: writes still require an epoch-bound RPC.
/// </summary>
public sealed class ChatConversationPanelState : IDisposable
{
    internal const double MinimumPanelWidth = 280;
    internal const double MaximumPanelWidth = 800;
    internal const double MinimumConversationWidth = 360;
    internal const double ResizeStep = 16;
    private readonly object _gate = new();
    private readonly Func<string, ChatConversationContext?> _resolveContext;
    private readonly Dictionary<ChatConversationIdentity, ChatConversationPanelLayout> _layouts = [];
    private CancellationTokenSource _cancellation = new();
    private ChatConversationContext? _context;
    private ChatConversationPanelLayout _layout = ChatConversationPanelLayout.Empty;
    private string? _sessionKey;
    private string? _expectedSelection;
    private string? _selectedContent;
    private string? _selectedRun;
    private long _generation;
    private bool _active = true;
    private bool _disposed;

    internal ChatConversationPanelState(Func<string, ChatConversationContext?> resolveContext) =>
        _resolveContext = resolveContext;

    internal event Action? Changed;
    internal event Action? Disposed;
    internal ChatConversationContext? Context { get { lock (_gate) return _context; } }
    internal ChatConversationPanelLayout Layout { get { lock (_gate) return _layout; } }
    internal long Generation { get { lock (_gate) return _generation; } }
    internal bool IsReady
    {
        get
        {
            var current = ResolveCurrent();
            lock (_gate) return IsContextCurrent(current);
        }
    }

    internal ChatConversationContext? Resolve(string sessionKey) => _resolveContext(sessionKey);

    internal void Refresh()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string? key;
            long generation;
            lock (_gate)
            {
                key = _sessionKey;
                generation = _generation;
            }
            var context = key is null ? null : _resolveContext(key);
            if (ApplyBinding(key, context, generation)) return;
        }
    }

    internal void CommitSelection(string? sessionKey)
    {
        lock (_gate) _expectedSelection = null;
        Bind(sessionKey);
    }

    internal void Bind(string? sessionKey)
    {
        var context = sessionKey is null ? null : _resolveContext(sessionKey);
        ApplyBinding(sessionKey, context);
    }

    private bool ApplyBinding(string? sessionKey, ChatConversationContext? context, long? expectedGeneration = null)
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            if (expectedGeneration.HasValue && expectedGeneration != _generation) return false;
            if (_disposed || !_active ||
                _expectedSelection is not null && _expectedSelection != sessionKey)
                return true;
            var selectionPending = _expectedSelection is not null;
            _expectedSelection = null;
            if (!selectionPending && _sessionKey == sessionKey && Equals(_context, context)) return true;
            SaveLayout();
            var retainingOfflineSelection = _sessionKey == sessionKey && context is null;
            _active = true;
            _sessionKey = sessionKey;
            _context = context;
            _selectedContent = null;
            _selectedRun = null;
            _layout = context is null
                ? retainingOfflineSelection ? _layout : ChatConversationPanelLayout.Empty
                : _layouts.GetValueOrDefault(context.Identity, ChatConversationPanelLayout.Empty);
            previous = AdvanceGeneration();
        }
        Cancel(previous);
        Changed?.Invoke();
        return true;
    }

    internal void Suspend()
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            if (_disposed || !_active) return;
            SaveLayout();
            _active = false;
            previous = AdvanceGeneration();
        }
        Cancel(previous);
        Changed?.Invoke();
    }

    internal void Resume()
    {
        string? key;
        lock (_gate)
        {
            if (_disposed || _active) return;
            _active = true;
            key = _expectedSelection ?? _sessionKey;
        }
        Bind(key);
        Changed?.Invoke();
    }

    internal void ExpectSelection(string sessionKey)
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            if (_disposed || _sessionKey == sessionKey && _expectedSelection is null) return;
            _expectedSelection = sessionKey;
            previous = AdvanceGeneration();
        }
        Cancel(previous);
        Changed?.Invoke();
    }

    internal bool Open(string panelId, string? contentId = null, string? runId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(panelId);
        var current = ResolveCurrent();
        CancellationTokenSource previous;
        lock (_gate)
        {
            if (!IsContextCurrent(current)) return false;
            if (_layout.SelectedPanel == panelId && _selectedContent == contentId && _selectedRun == runId)
                return true;
            _selectedContent = contentId;
            _selectedRun = runId;
            _layout = _layout with
            {
                OpenPanels = _layout.OpenPanels.Contains(panelId, StringComparer.Ordinal)
                    ? _layout.OpenPanels : [.. _layout.OpenPanels, panelId],
                SelectedPanel = panelId
            };
            SaveLayout();
            previous = AdvanceGeneration();
        }
        Cancel(previous);
        Changed?.Invoke();
        return true;
    }

    internal void Close(string panelId)
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            if (_disposed || !_layout.OpenPanels.Contains(panelId, StringComparer.Ordinal)) return;
            if (_layout.SelectedPanel == panelId)
            {
                _selectedContent = null;
                _selectedRun = null;
            }
            var remaining = _layout.OpenPanels.Where(id => id != panelId).ToArray();
            _layout = _layout with
            {
                OpenPanels = remaining,
                SelectedPanel = _layout.SelectedPanel == panelId ? remaining.LastOrDefault() : _layout.SelectedPanel
            };
            SaveLayout();
            previous = AdvanceGeneration();
        }
        Cancel(previous);
        Changed?.Invoke();
    }

    internal void Resize(double width)
    {
        if (!double.IsFinite(width)) throw new ArgumentOutOfRangeException(nameof(width));
        lock (_gate)
        {
            if (_disposed) return;
            width = Math.Clamp(width, MinimumPanelWidth, MaximumPanelWidth);
            if (_layout.Width == width) return;
            _layout = _layout with { Width = width };
            SaveLayout();
        }
        Changed?.Invoke();
    }

    internal ChatConversationPanelOperation? BeginOperation(string panelId, string? runId = null)
    {
        var current = ResolveCurrent();
        lock (_gate)
        {
            if (!IsContextCurrent(current) || _layout.SelectedPanel != panelId) return null;
            if (runId is not null && runId != _selectedRun) return null;
            var context = _context!;
            return new(this, context, panelId, _selectedRun, _generation, _cancellation.Token,
                new WorkspaceSessionOperation(context.Client,
                    () => _resolveContext(context.Identity.SessionKey)?.Client));
        }
    }

    internal bool IsCurrent(ChatConversationPanelOperation operation)
    {
        var current = ResolveCurrent();
        var connectionCurrent = operation.Connection.IsCurrent;
        lock (_gate)
            return !operation.CancellationToken.IsCancellationRequested &&
                operation.Generation == _generation &&
                _layout.SelectedPanel == operation.PanelId &&
                _selectedRun == operation.RunId &&
                Equals(operation.Context, _context) &&
                connectionCurrent && IsContextCurrent(current);
    }

    private ChatConversationContext? ResolveCurrent()
    {
        string? key;
        lock (_gate) key = _sessionKey;
        return key is null ? null : _resolveContext(key);
    }

    private bool IsContextCurrent(ChatConversationContext? current) => !_disposed && _active && _expectedSelection is null &&
        _context is { IsTransportCurrent: true } context &&
        _sessionKey == context.Identity.SessionKey &&
        Equals(context, current);

    private void SaveLayout()
    {
        if (_context is { } context) _layouts[context.Identity] = _layout;
    }

    private CancellationTokenSource AdvanceGeneration()
    {
        _generation++;
        var previous = _cancellation;
        _cancellation = new();
        return previous;
    }

    private static void Cancel(CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        cancellation.Dispose();
    }

    public void Dispose()
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            previous = _cancellation;
            _generation++;
            _context = null;
            _layouts.Clear();
        }
        Cancel(previous);
        Changed = null;
        Disposed?.Invoke();
        Disposed = null;
    }
}

internal sealed class ChatConversationPanelOperation(
    ChatConversationPanelState owner,
    ChatConversationContext context,
    string panelId,
    string? runId,
    long generation,
    CancellationToken cancellationToken,
    WorkspaceSessionOperation connection)
{
    internal ChatConversationContext Context { get; } = context;
    internal string PanelId { get; } = panelId;
    internal string? RunId { get; } = runId;
    internal long Generation { get; } = generation;
    internal CancellationToken CancellationToken { get; } = cancellationToken;
    internal WorkspaceSessionOperation Connection { get; } = connection;
    internal bool IsCurrent => owner.IsCurrent(this);
}
