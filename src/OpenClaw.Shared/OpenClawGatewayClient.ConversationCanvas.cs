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
        RequireCanvasConnection(connectionEpoch);
        const string method = "canvas.document.view";
        var advertisementKnown = HasServerMethodAdvertisement;
        var supported = AdvertisedServerMethods.Contains(method, StringComparer.Ordinal);
        RequireCanvasConnection(connectionEpoch);
        if (!advertisementKnown)
            throw new CanvasDocumentPendingException();
        if (!supported)
            throw new NotSupportedException("The Gateway does not advertise conversation Canvas documents.");
        var signingIdentity = AuthenticatedSigningDeviceId;
        var payload = await SendResponseRequestAsync(method,
                new { docId = request.DocumentId, sessionKey = request.SessionKey }, timeoutMs, connectionEpoch)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        RequireCanvasConnection(connectionEpoch);
        if (signingIdentity != AuthenticatedSigningDeviceId)
            throw new InvalidOperationException("The Canvas authentication context changed.");
        return ConversationCanvasBoundary.ParseDocument(payload, _currentGatewayUrl);
    }

    private void RequireCanvasConnection(long connectionEpoch)
    {
        if (!IsConnectedToGateway || SessionMutationConnectionEpoch != connectionEpoch)
            throw new InvalidOperationException("The Gateway connection changed before the Canvas document could be read.");
    }
}
