namespace OpenClaw.Shared;

public partial interface IOperatorGatewayClient
{
    SessionFilesCapabilities SessionFilesCapabilities => new(SessionFilesReadiness.Waiting);

    Task<SessionFileList> ListSessionFilesAsync(SessionFileScope scope, long connectionEpoch,
        string? path = null, string? search = null, CancellationToken cancellationToken = default,
        int timeoutMs = SessionFileLimits.RequestTimeoutMilliseconds) =>
        Task.FromException<SessionFileList>(new NotSupportedException("Session Files is not supported by this client."));

    Task<SessionFileContent> GetSessionFileAsync(SessionFileRequest request, long connectionEpoch,
        CancellationToken cancellationToken = default, int timeoutMs = SessionFileLimits.RequestTimeoutMilliseconds) =>
        Task.FromException<SessionFileContent>(new NotSupportedException("Session Files is not supported by this client."));

    Task<SessionFileContent> SetSessionFileAsync(SessionFileWrite request, long connectionEpoch,
        CancellationToken cancellationToken = default, int timeoutMs = SessionFileLimits.RequestTimeoutMilliseconds) =>
        Task.FromException<SessionFileContent>(new NotSupportedException("Session Files editing is not supported by this client."));

    Task<SessionFileDownload> GetSessionFileDownloadAsync(SessionFileRequest request, long connectionEpoch,
        CancellationToken cancellationToken = default, int timeoutMs = SessionFileLimits.RequestTimeoutMilliseconds) =>
        Task.FromException<SessionFileDownload>(new NotSupportedException("Session Files download is not supported by this client."));

    Task DownloadSessionFileAsync(SessionFileDownload download, long connectionEpoch, Stream destination,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Session Files download is not supported by this client."));

    Task<SessionDiff> GetSessionDiffAsync(SessionDiffQuery query, long connectionEpoch,
        CancellationToken cancellationToken = default, int timeoutMs = SessionFileLimits.RequestTimeoutMilliseconds) =>
        Task.FromException<SessionDiff>(new NotSupportedException("Session changes are not supported by this client."));
}
