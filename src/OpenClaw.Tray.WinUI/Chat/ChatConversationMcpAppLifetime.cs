using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

/// <summary>
/// One panel host owns this barrier across renders. The server has no render-generation subscription key.
/// A failed or uncertain old subscription cannot be followed by a new same-socket subscription.
/// </summary>
internal enum ChatConversationMcpAppRetirement { Drained, RequiresFreshConnection }

internal sealed class ChatConversationMcpAppLifetime(
    ChatConversationContext capturedContext, GatewayDataReadSource capturedSource,
    Func<ChatConversationContext, GatewayDataReadSource, bool> isCanonicalSourceCurrent)
{
    private const int MaximumScopes = 128;
    private readonly object _gate = new();
    private readonly Dictionary<ScopeKey, Task<bool>> _tails = [];
    private readonly HashSet<ChatConversationMcpAppMountLease> _mounts = [];
    private bool _revoked;
    private sealed record ScopeKey(IOperatorGatewayClient Client, long Epoch, string Session, string View, string? Agent);

    internal async Task<ChatConversationMcpAppMountLease?> AcquireAsync(
        ChatConversationMcpAppAction owner, CancellationToken token)
    {
        var context = owner.Operation.Context;
        if (context != capturedContext || !ReferenceEquals(context.Client, capturedContext.Client)) return null;
        var scope = new ConversationMcpAppScope(owner.Selection.Owner.SessionKey,
            owner.Selection.Content.McpApp!.ViewId, owner.Selection.Owner.AgentId);
        var key = new ScopeKey(context.Client, context.ConnectionEpoch, scope.SessionKey, scope.ViewId, scope.AgentId);
        Task<bool> predecessor;
        var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ChatConversationMcpAppMountLease? mount = null;
        lock (_gate)
        {
            if (_revoked) return null;
            if (_tails.Count >= MaximumScopes && !_tails.ContainsKey(key)) return null;
            predecessor = _tails.GetValueOrDefault(key, Task.FromResult(true));
            _tails[key] = released.Task;
        }
        try
        {
            var safe = await predecessor.WaitAsync(token);
            if (!safe || !owner.IsCurrent || !SourceCurrent())
            {
                Release(safe);
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            // Preserve the predecessor even when this queued mount is cancelled.
            _ = ForwardReleaseAsync();
            return null;
        }
        catch
        {
            Release(false);
            throw;
        }
        mount = new(owner, scope, capturedSource, () => SourceCurrent() &&
            context.IsTransportCurrent && owner.Operation.Connection.IsCurrent, Release);
        lock (_gate)
        {
            if (!_revoked) { _mounts.Add(mount); return mount; }
        }
        mount.RevokeLocal();
        _ = mount.RetireAsync();
        return null;

        bool SourceCurrent() => capturedSource.IsCurrent && ReferenceEquals(capturedSource.Client, context.Client) &&
            capturedSource.ConnectionEpoch == context.ConnectionEpoch && isCanonicalSourceCurrent(context, capturedSource);

        async Task ForwardReleaseAsync()
        {
            Release(await predecessor);
        }
        void Release(bool safe)
        {
            released.TrySetResult(safe);
            lock (_gate) if (mount is not null) _mounts.Remove(mount);
            if (!safe) return;
            lock (_gate)
                if (_tails.TryGetValue(key, out var tail) && ReferenceEquals(tail, released.Task))
                    _tails.Remove(key);
        }
    }

    internal async Task<ChatConversationMcpAppRetirement> RetireAsync()
    {
        ChatConversationMcpAppMountLease[] mounts;
        Task<bool>[] tails;
        lock (_gate)
        {
            _revoked = true;
            mounts = _mounts.ToArray();
            tails = _tails.Values.ToArray();
        }
        foreach (var mount in mounts) mount.RevokeLocal();
        await Task.WhenAll(mounts.Select(mount => mount.RetireSessionAsync()));
        var results = await Task.WhenAll(tails);
        return results.All(safe => safe) ? ChatConversationMcpAppRetirement.Drained :
            ChatConversationMcpAppRetirement.RequiresFreshConnection;
    }
}

internal sealed class ChatConversationMcpAppMountLease(
    ChatConversationMcpAppAction owner, ConversationMcpAppScope scope, GatewayDataReadSource source,
    Func<bool> isSourceCurrent, Action<bool> release)
{
    private readonly object _gate = new();
    private readonly HashSet<string> _subscriptions = new(StringComparer.Ordinal);
    private TaskCompletionSource _drained = Completed();
    private int _pending;
    private bool _retiring;
    private bool _uncertain;
    private Task<bool>? _retirement;
    private Func<Task>? _retireSession;
    private bool _revoked;

    internal bool SessionOwned { get { lock (_gate) return _retireSession is not null; } }
    internal GatewayDataReadSource Source => source;
    internal bool IsCurrent { get { lock (_gate) return !_retiring && !_revoked && owner.IsCurrent && isSourceCurrent(); } }
    internal void RevokeLocal() { lock (_gate) _revoked = true; }
    internal void AttachSession(Func<Task> retire) { lock (_gate) _retireSession = retire; }
    internal async Task RetireSessionAsync()
    {
        Func<Task>? retire;
        lock (_gate) retire = _retireSession;
        if (retire is not null) await retire();
        await RetireAsync();
    }
    internal bool Contains(string uri) { lock (_gate) return !_retiring && _subscriptions.Contains(uri); }

    internal async Task<JsonElement> SubscriptionAsync(ConversationMcpAppOperation operation,
        JsonElement parameters, CancellationToken token)
    {
        var uri = parameters.GetProperty("uri").GetString()!;
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!IsCurrent) throw new OperationCanceledException();
            if (operation == ConversationMcpAppOperation.SubscribeResource)
            {
                if (_subscriptions.Count + _pending >= 128 && !_subscriptions.Contains(uri))
                    throw new ConversationMcpAppProtocolException(-32000, "The App subscription limit was reached.");
            }
            if (_pending++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            // Observe the real response, not a cancelled local wait. Retirement must drain it first.
            var result = await owner.Operation.Context.Client.InvokeConversationMcpAppAsync(scope, operation,
                parameters, owner.Operation.Context.ConnectionEpoch, CancellationToken.None);
            ConversationMcpAppProtocol.ValidateGatewayResult(operation, result);
            lock (_gate)
            {
                if (operation == ConversationMcpAppOperation.SubscribeResource) _subscriptions.Add(uri);
                else _subscriptions.Remove(uri);
            }
            return result;
        }
        catch (Exception ex)
        {
            // A timeout or a malformed response cannot establish what the server retained.
            if (ex is not (GatewayRequestException or NotSupportedException))
                lock (_gate) _uncertain = true;
            throw;
        }
        finally
        {
            lock (_gate) if (--_pending == 0) _drained.TrySetResult();
        }
    }

    internal Task<bool> RetireAsync()
    {
        lock (_gate)
        {
            _retiring = true;
            return _retirement ??= ReleaseAsync();
        }
    }

    private async Task<bool> ReleaseAsync()
    {
        // RetireAsync publishes the single retirement task while holding the mount gate.
        await Task.Yield();
        var safe = false;
        try
        {
            await _drained.Task;
            string[] subscriptions;
            lock (_gate)
            {
                subscriptions = _subscriptions.ToArray();
                safe = !_uncertain;
            }
            foreach (var uri in subscriptions)
            {
                try
                {
                    if (!isSourceCurrent()) { safe = false; break; }
                    var result = await owner.Operation.Context.Client.InvokeConversationMcpAppAsync(scope,
                        ConversationMcpAppOperation.UnsubscribeResource, JsonSerializer.SerializeToElement(new { uri }),
                        owner.Operation.Context.ConnectionEpoch, CancellationToken.None);
                    ConversationMcpAppProtocol.ValidateGatewayResult(ConversationMcpAppOperation.UnsubscribeResource, result);
                    if (!isSourceCurrent()) safe = false;
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
                {
                    safe = false;
                    Logger.Warn($"[ConversationMcpApp] Subscription cleanup outcome is unknown ({ex.GetType().Name}).");
                }
            }
            return safe;
        }
        catch { safe = false; throw; }
        finally { release(safe); }
    }

    private static TaskCompletionSource Completed()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completed.SetResult();
        return completed;
    }
}
