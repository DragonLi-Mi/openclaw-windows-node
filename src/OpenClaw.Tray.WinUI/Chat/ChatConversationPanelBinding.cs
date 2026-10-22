using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

/// <summary>Observes only the manager-owned transport. No UI state is read by completion fences.</summary>
internal static class ChatConversationPanelBinding
{
    internal static ChatConversationPanelState Create(
        IGatewayConnectionManager manager, IOperatorGatewayClient client, Func<SessionInfo[]> sessions,
        ChatConversationPanelLayouts? layouts = null)
    {
        var initial = manager.CurrentSnapshot;
        var gatewayId = ReferenceEquals(manager.OperatorClient, client) &&
            ReferenceEquals(initial, manager.CurrentSnapshot) ? initial.GatewayId : null;
        var state = new ChatConversationPanelState(key =>
        {
            var before = manager.CurrentSnapshot;
            if (gatewayId is null || before.GatewayId != gatewayId ||
                before.OperatorState != RoleConnectionState.Connected ||
                !ReferenceEquals(manager.OperatorClient, client))
                return null;
            var context = ChatConversationContext.Capture(gatewayId, client,
                sessions().FirstOrDefault(session => string.Equals(session.Key, key, StringComparison.Ordinal)));
            return ReferenceEquals(before, manager.CurrentSnapshot) &&
                ReferenceEquals(manager.OperatorClient, client) &&
                context?.IsTransportCurrent == true ? context : null;
        }, layouts);
        void OnStateChanged(object? sender, GatewayConnectionSnapshot snapshot) => state.Refresh();
        void OnStatusChanged(object? sender, ConnectionStatus status) => state.Refresh();
        void OnHandshake(object? sender, EventArgs args) => state.Refresh();
        void OnSessions(object? sender, SessionInfo[] rows) => state.Refresh();
        void OnClientChanged(object? sender, OperatorClientChangedEventArgs args) => state.Refresh();
        manager.StateChanged += OnStateChanged;
        manager.OperatorClientChanged += OnClientChanged;
        client.StatusChanged += OnStatusChanged;
        client.HandshakeSucceeded += OnHandshake;
        client.SessionsUpdated += OnSessions;
        state.Disposed += () =>
        {
            manager.StateChanged -= OnStateChanged;
            manager.OperatorClientChanged -= OnClientChanged;
            client.StatusChanged -= OnStatusChanged;
            client.HandshakeSucceeded -= OnHandshake;
            client.SessionsUpdated -= OnSessions;
        };
        return state;
    }
}
