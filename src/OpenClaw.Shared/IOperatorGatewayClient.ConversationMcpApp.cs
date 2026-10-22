using System.Text.Json;

namespace OpenClaw.Shared;

public partial interface IOperatorGatewayClient
{
    Task<ConversationMcpAppView> ViewConversationMcpAppAsync(
        ConversationMcpAppScope scope, long connectionEpoch,
        CancellationToken cancellationToken = default, int timeoutMs = 30000) =>
        Task.FromException<ConversationMcpAppView>(
            new NotSupportedException("Conversation MCP Apps are not supported by this Gateway client."));

    /// <summary>
    /// A view-scoped MCP operation, never arbitrary Gateway RPC. Callers own capability,
    /// focus and confirmation checks. A cancelled wait does not retract an admitted operation.
    /// A write may already have executed when cancellation, timeout or the post-response
    /// connection guard throws. Treat that outcome as unknown, not failed, and never retry automatically.
    /// </summary>
    Task<JsonElement> InvokeConversationMcpAppAsync(
        ConversationMcpAppScope scope, ConversationMcpAppOperation operation, JsonElement parameters,
        long connectionEpoch, CancellationToken cancellationToken = default, int timeoutMs = 30000) =>
        Task.FromException<JsonElement>(
            new NotSupportedException("Conversation MCP App operations are not supported by this Gateway client."));
}
