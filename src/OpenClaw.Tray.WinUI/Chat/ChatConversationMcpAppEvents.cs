using OpenClawTray.Presentation;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

/// <summary>
/// Root supplies authenticated, original-source deliveries. Transport identity alone is insufficient.
/// Keep unavailable until the producer's nonnull GatewayDataReadSource and canonical binding are integrated.
/// Question/plugin attention uses this explicit hook, not another payload parser.
/// </summary>
internal interface IChatConversationMcpAppEventSource
{
    bool IsAvailable { get; }
    IDisposable Subscribe(ChatConversationMcpAppAction capturedOwner, Action<ChatConversationMcpAppEvent> receive);
}

internal sealed class ChatConversationMcpAppEvents(
    IChatConversationMcpAppEventSource source, IUiDispatcher dispatcher, GatewayDataReadSource capturedSource,
    Func<ChatConversationMcpAppAction, GatewayDataReadSource, bool> matchesCanonicalReceipt) : IChatConversationMcpAppEvents
{
    public bool IsAvailable => source.IsAvailable && capturedSource.IsCurrent;

    /// <remarks>An unavailable subscription stays inert. Explicitly retry with a new subscription after source admission.</remarks>
    public IDisposable Subscribe(ChatConversationMcpAppAction owner, string viewId, Action<ChatConversationMcpAppEvent> receive)
    {
        var subscription = new Subscription(owner, viewId, dispatcher, capturedSource, matchesCanonicalReceipt, receive);
        if (IsAvailable && owner.IsCurrent && matchesCanonicalReceipt(owner, capturedSource) &&
            owner.Selection.Content.McpApp?.ViewId == viewId)
            subscription.Attach(source.Subscribe(owner, subscription.Receive));
        return subscription;
    }

    private sealed class Subscription(ChatConversationMcpAppAction owner, string viewId,
        IUiDispatcher dispatcher, GatewayDataReadSource capturedSource,
        Func<ChatConversationMcpAppAction, GatewayDataReadSource, bool> matchesCanonicalReceipt,
        Action<ChatConversationMcpAppEvent> receive) : IDisposable
    {
        private readonly object _gate = new();
        private IDisposable? _source;
        private bool _disposed;

        internal void Attach(IDisposable subscription)
        {
            lock (_gate)
            {
                if (!_disposed) { _source = subscription; return; }
            }
            subscription.Dispose();
        }

        private bool Active { get { lock (_gate) return !_disposed; } }
        private bool Current(ChatConversationMcpAppEvent delivery) => Active && owner.IsCurrent &&
            delivery.Source is not null && delivery.Source.IsCurrent && capturedSource.IsCurrent &&
            ReferenceEquals(delivery.Source.Client, owner.Operation.Context.Client) &&
            delivery.Source.ConnectionEpoch == capturedSource.ConnectionEpoch &&
            delivery.Source.ProfileDataRevision == capturedSource.ProfileDataRevision &&
            matchesCanonicalReceipt(owner, delivery.Source) &&
            delivery.Owner == owner.Selection.Owner &&
            delivery.ConnectionEpoch == owner.Operation.Context.ConnectionEpoch &&
            delivery.AccountRevision == owner.Operation.Context.AccountRevision &&
            delivery.RunId == owner.Selection.RunId && delivery.ViewId == viewId && Active;

        internal void Receive(ChatConversationMcpAppEvent delivery)
        {
            if (!Current(delivery)) return;
            var captured = delivery with { Parameters = delivery.Parameters.Clone() };
            if (!dispatcher.TryEnqueue(() =>
            {
                if (!Current(captured)) return;
                receive(captured);
            })) Dispose();
        }

        public void Dispose()
        {
            IDisposable? subscription;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                subscription = _source;
                _source = null;
            }
            subscription?.Dispose();
        }
    }
}
