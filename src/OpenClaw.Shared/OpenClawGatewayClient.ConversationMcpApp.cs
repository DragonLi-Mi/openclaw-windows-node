using System.Text.Json;

namespace OpenClaw.Shared;

public partial class OpenClawGatewayClient
{
    public async Task<ConversationMcpAppView> ViewConversationMcpAppAsync(
        ConversationMcpAppScope scope, long connectionEpoch,
        CancellationToken cancellationToken = default, int timeoutMs = 30000)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();
        var parameters = new Dictionary<string, string>
        {
            ["sessionKey"] = scope.SessionKey,
            ["viewId"] = scope.ViewId,
        };
        if (scope.AgentId is not null) parameters["agentId"] = scope.AgentId;
        var payload = await RequestConversationMcpAppAsync(
            "mcp.app.view", parameters, connectionEpoch, cancellationToken, timeoutMs).ConfigureAwait(false);
        return ConversationMcpAppView.Parse(payload, _currentGatewayUrl);
    }

    public Task<JsonElement> InvokeConversationMcpAppAsync(
        ConversationMcpAppScope scope, ConversationMcpAppOperation operation, JsonElement parameters,
        long connectionEpoch, CancellationToken cancellationToken = default, int timeoutMs = 30000)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return RequestConversationMcpAppAsync(ConversationMcpAppRequests.Method(operation),
            ConversationMcpAppRequests.Parameters(scope, operation, parameters),
            connectionEpoch, cancellationToken, timeoutMs);
    }

    private async Task<JsonElement> RequestConversationMcpAppAsync(
        string method, object parameters, long connectionEpoch, CancellationToken cancellationToken, int timeoutMs)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var support = ReadContentMethodSupport(method, connectionEpoch);
        if (!support.Known) throw new ConversationMcpAppPendingException();
        if (!support.Supported) throw new NotSupportedException("The Gateway does not advertise this MCP App operation.");
        var signingIdentity = AuthenticatedSigningDeviceId;
        var payload = await SendResponseRequestAsync(method, parameters, timeoutMs, connectionEpoch)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        RequireContentConnection(connectionEpoch);
        if (signingIdentity != AuthenticatedSigningDeviceId)
            throw new InvalidOperationException("The MCP App authentication context changed.");
        return payload;
    }
}
