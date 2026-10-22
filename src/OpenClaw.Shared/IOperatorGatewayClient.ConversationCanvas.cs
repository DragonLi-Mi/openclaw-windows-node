namespace OpenClaw.Shared;

public partial interface IOperatorGatewayClient
{
    /// <summary>Reads one saved document for the captured conversation and authenticated connection.</summary>
    Task<CanvasDocumentView> ViewCanvasDocumentAsync(
        CanvasDocumentRequest request, long connectionEpoch,
        CancellationToken cancellationToken = default, int timeoutMs = 30000) =>
        Task.FromException<CanvasDocumentView>(
            new NotSupportedException("Conversation Canvas documents are not supported by this Gateway client."));
}
