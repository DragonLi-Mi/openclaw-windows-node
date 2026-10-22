namespace OpenClawTray.Chat;

internal sealed record ChatConversationMcpAppPendingConfirmation(long Id, ChatConversationMcpAppConfirmation Request);

/// <summary>Root can use this as its ConfirmAsync hook and render the matching controlled native dialog.</summary>
internal sealed class ChatConversationMcpAppConfirmationState : IDisposable
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _completion;
    private ChatConversationMcpAppPendingConfirmation? _pending;
    private long _revision;
    private bool _disposed;

    internal event Action? Changed;
    internal ChatConversationMcpAppPendingConfirmation? Pending { get { lock (_gate) return _pending; } }

    internal async Task<bool> ConfirmAsync(ChatConversationMcpAppConfirmation request, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> completion;
        long id;
        lock (_gate)
        {
            if (_disposed || _pending is not null || !request.Owner.IsCurrent || cancellationToken.IsCancellationRequested)
                return false;
            id = ++_revision;
            _pending = new(id, request);
            _completion = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        using var registration = cancellationToken.Register(() => Respond(id, false));
        using var ownerRegistration = request.Owner.Operation.CancellationToken.Register(() => Respond(id, false));
        Changed?.Invoke();
        var accepted = await completion.Task;
        return accepted && request.Owner.IsCurrent && !cancellationToken.IsCancellationRequested;
    }

    internal void Respond(long id, bool accepted)
    {
        TaskCompletionSource<bool>? completion;
        lock (_gate)
        {
            if (_pending?.Id != id) return;
            accepted &= !_disposed && _pending.Request.Owner.IsCurrent;
            completion = _completion;
            _completion = null;
            _pending = null;
        }
        completion?.TrySetResult(accepted);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        long? id;
        lock (_gate) { if (_disposed) return; _disposed = true; id = _pending?.Id; }
        if (id is { } current) Respond(current, false);
    }
}
