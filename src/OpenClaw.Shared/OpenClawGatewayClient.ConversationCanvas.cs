namespace OpenClaw.Shared;

public partial class OpenClawGatewayClient
{
    public async Task<CanvasDocumentView> ViewCanvasDocumentAsync(
        CanvasDocumentRequest request, long connectionEpoch,
        CancellationToken cancellationToken = default, int timeoutMs = 30000)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionKey);
        if (!ConversationCanvasContent.IsDocumentId(request.DocumentId))
            throw new ArgumentException("The Canvas document identity is invalid.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        const string method = "canvas.document.view";
        var support = ReadContentMethodSupport(method, connectionEpoch);
        if (!support.Known)
            throw new CanvasDocumentPendingException();
        if (!support.Supported)
            throw new NotSupportedException("The Gateway does not advertise conversation Canvas documents.");
        var signingIdentity = AuthenticatedSigningDeviceId;
        var payload = await SendResponseRequestAsync(method,
                new { docId = request.DocumentId, sessionKey = request.SessionKey }, timeoutMs, connectionEpoch)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        RequireContentConnection(connectionEpoch);
        if (signingIdentity != AuthenticatedSigningDeviceId)
            throw new InvalidOperationException("The Canvas authentication context changed.");
        return ConversationCanvasBoundary.ParseDocument(payload, _currentGatewayUrl);
    }

    private (bool Known, bool Supported) ReadContentMethodSupport(string method, long connectionEpoch)
    {
        RequireContentConnection(connectionEpoch);
        var known = HasServerMethodAdvertisement;
        var supported = AdvertisedServerMethods.Contains(method, StringComparer.Ordinal);
        RequireContentConnection(connectionEpoch);
        return (known, supported);
    }

    private void RequireContentConnection(long connectionEpoch)
    {
        if (!IsConnectedToGateway || SessionMutationConnectionEpoch != connectionEpoch)
            throw new InvalidOperationException("The Gateway connection changed before the conversation content request could be sent.");
    }
}
