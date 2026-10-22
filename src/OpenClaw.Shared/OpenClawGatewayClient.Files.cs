using System.Net.Http;
using System.Text.Json;

namespace OpenClaw.Shared;

public partial class OpenClawGatewayClient
{
    private static readonly JsonSerializerOptions FilesJson = new()
    {
        PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SessionFilesCapabilities SessionFilesCapabilities
    {
        get
        {
            var epoch = SessionMutationConnectionEpoch;
            if (epoch is null || !HasServerMethodAdvertisement)
                return new(SessionFilesReadiness.Waiting);
            var methods = AdvertisedServerMethods;
            bool Has(string method) => methods.Contains(method, StringComparer.Ordinal);
            var result = new SessionFilesCapabilities(
                Has("sessions.files.list") || Has("sessions.files.get")
                    ? SessionFilesReadiness.Ready : SessionFilesReadiness.Unsupported,
                Has("sessions.files.list"), Has("sessions.files.get"), Has("sessions.files.set"),
                Has("sessions.files.download"), Has("sessions.diff"));
            return SessionMutationConnectionEpoch == epoch ? result : new(SessionFilesReadiness.Waiting);
        }
    }

    public async Task<SessionFileList> ListSessionFilesAsync(SessionFileScope scope, long connectionEpoch,
        string? path = null, string? search = null, CancellationToken cancellationToken = default,
        int timeoutMs = SessionFileLimits.RequestTimeoutMilliseconds)
    {
        ValidateFileScope(scope);
        var parameters = new Dictionary<string, object?>
        {
            ["sessionKey"] = scope.SessionKey, ["agentId"] = scope.AgentId
        };
        if (path is not null) parameters["path"] = path;
        if (search is not null) parameters["search"] = search;
        var payload = await RequestFilePayloadAsync("sessions.files.list", parameters, connectionEpoch,
            cancellationToken, timeoutMs).ConfigureAwait(false);
        RequireFileSession(payload, scope);
        if (!payload.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            throw new JsonException("The Gateway returned an invalid session file list.");
        return ParseSessionFileList(payload, scope.SessionKey);
    }

    public async Task<SessionFileContent> GetSessionFileAsync(SessionFileRequest request, long connectionEpoch,
        CancellationToken cancellationToken = default, int timeoutMs = SessionFileLimits.RequestTimeoutMilliseconds)
    {
        var payload = await RequestFilePayloadAsync("sessions.files.get", FileParameters(request), connectionEpoch,
            cancellationToken, timeoutMs).ConfigureAwait(false);
        return ReadFileSnapshot(payload, request.Scope, request.Path);
    }

    public async Task<SessionFileContent> SetSessionFileAsync(SessionFileWrite request, long connectionEpoch,
        CancellationToken cancellationToken = default, int timeoutMs = SessionFileLimits.RequestTimeoutMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!SessionFileLimits.IsContentHash(request.ExpectedHash))
            throw new ArgumentException("A 64-character lowercase SHA256 file hash is required.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.ExpectedSource);
        ArgumentNullException.ThrowIfNull(request.Content);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExpectedSource.WorkspaceId);
        if (!GrantedOperatorScopes.Contains("operator.admin", StringComparer.Ordinal))
            throw new UnauthorizedAccessException("Session file editing requires operator.admin.");
        var parameters = BuildSessionFileWriteParameters(request);
        var payload = await RequestFilePayloadAsync("sessions.files.set", parameters, connectionEpoch,
            cancellationToken, timeoutMs).ConfigureAwait(false);
        var snapshot = ReadFileSnapshot(payload, request.Scope, request.Path);
        if (snapshot.Source != request.ExpectedSource || !SessionFileLimits.IsContentHash(snapshot.Hash))
            throw new JsonException("The Gateway did not confirm the original file source and saved hash.");
        return snapshot;
    }

    public async Task<SessionFileDownload> GetSessionFileDownloadAsync(SessionFileRequest request, long connectionEpoch,
        CancellationToken cancellationToken = default, int timeoutMs = SessionFileLimits.RequestTimeoutMilliseconds)
    {
        var payload = await RequestFilePayloadAsync("sessions.files.download", FileParameters(request), connectionEpoch,
            cancellationToken, timeoutMs).ConfigureAwait(false);
        var download = payload.Deserialize<SessionFileDownload>(FilesJson);
        if (download is null || string.IsNullOrWhiteSpace(download.Filename) ||
            string.IsNullOrWhiteSpace(download.MimeType) || download.ExpiresAt <= DateTimeOffset.UtcNow ||
            !TryBuildMediaHttpBaseUri(_currentGatewayUrl, out var origin) ||
            !TryResolveSessionDownloadUri(origin, download.Url, out _))
            throw new JsonException("The Gateway returned an invalid file download grant.");
        return download;
    }

    public async Task DownloadSessionFileAsync(SessionFileDownload download, long connectionEpoch, Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        RequireFileEpoch(connectionEpoch);
        if (download.ExpiresAt <= DateTimeOffset.UtcNow ||
            !TryBuildMediaHttpBaseUri(_currentGatewayUrl, out var origin) ||
            !TryResolveSessionDownloadUri(origin, download.Url, out var uri))
            throw new InvalidOperationException("The file download grant is invalid or expired.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CancellationToken);
        linked.CancelAfter(TimeSpan.FromMilliseconds(SessionFileLimits.RequestTimeoutMilliseconds));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _assistantMediaHttpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        RequireFileEpoch(connectionEpoch);
        if (response.RequestMessage?.RequestUri != uri)
            throw new HttpRequestException("The file download redirected outside its grant.");
        using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) != 0)
        {
            RequireFileEpoch(connectionEpoch);
            await destination.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
        }
        RequireFileEpoch(connectionEpoch);
    }

    public async Task<SessionDiff> GetSessionDiffAsync(SessionDiffQuery query, long connectionEpoch,
        CancellationToken cancellationToken = default, int timeoutMs = SessionFileLimits.RequestTimeoutMilliseconds)
    {
        ValidateFileScope(query.Scope);
        var parameters = new Dictionary<string, object?>
        {
            ["sessionKey"] = query.Scope.SessionKey, ["agentId"] = query.Scope.AgentId
        };
        if (query.Kind == SessionDiffScope.Commit)
            ArgumentException.ThrowIfNullOrWhiteSpace(query.Commit);
        else if (query.Commit is not null)
            throw new ArgumentException("A commit is only valid for commit scope.", nameof(query));
        parameters["scope"] = query.Kind switch
        {
            SessionDiffScope.All => "all",
            SessionDiffScope.Uncommitted => "uncommitted",
            SessionDiffScope.Commit => "commit",
            _ => throw new ArgumentOutOfRangeException(nameof(query))
        };
        if (query.Commit is not null) parameters["commit"] = query.Commit;
        parameters["summaryOnly"] = query.SummaryOnly;
        parameters["poll"] = query.Poll;
        var payload = await RequestFilePayloadAsync("sessions.diff", parameters, connectionEpoch,
            cancellationToken, timeoutMs).ConfigureAwait(false);
        RequireFileSession(payload, query.Scope);
        var diff = payload.Deserialize<SessionDiff>(FilesJson);
        if (diff is null || diff.Files is null ||
            !payload.TryGetProperty("additions", out var additions) || !additions.TryGetInt32(out var added) || added < 0 ||
            !payload.TryGetProperty("deletions", out var deletions) || !deletions.TryGetInt32(out var removed) || removed < 0 ||
            diff.Files.Any(f => string.IsNullOrWhiteSpace(f.Path) || f.Additions < 0 || f.Deletions < 0 ||
                f.Status is not ("added" or "modified" or "deleted" or "renamed")) ||
            diff.UnavailableReason is not (null or "unknown_session" or "not_git" or "unknown_commit" or "workspace_stopped"))
            throw new JsonException("The Gateway returned invalid session changes.");
        return diff;
    }

    private async Task<JsonElement> RequestFilePayloadAsync(string method, object parameters, long epoch,
        CancellationToken cancellationToken, int timeoutMs)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireFileEpoch(epoch);
        if (!HasServerMethodAdvertisement)
            throw new InvalidOperationException("Waiting for the Gateway method advertisement.");
        if (!AdvertisedServerMethods.Contains(method, StringComparer.Ordinal))
            throw new NotSupportedException($"{method} is not advertised by this Gateway.");
        // Do not abandon an in-flight write. Its bounded reply still belongs to this epoch.
        var payload = await SendResponseRequestAsync(method, parameters, timeoutMs, epoch).ConfigureAwait(false);
        RequireFileEpoch(epoch);
        cancellationToken.ThrowIfCancellationRequested();
        return payload;
    }

    private void RequireFileEpoch(long epoch)
    {
        if (SessionMutationConnectionEpoch != epoch)
            throw new InvalidOperationException("The Gateway connection changed during file access.");
    }

    private static void ValidateFileScope(SessionFileScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.SessionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.AgentId);
    }

    private static Dictionary<string, object?> FileParameters(SessionFileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);
        ValidateFileScope(request.Scope);
        var parameters = new Dictionary<string, object?>
        {
            ["sessionKey"] = request.Scope.SessionKey, ["agentId"] = request.Scope.AgentId, ["path"] = request.Path
        };
        if (request.MessageEntryId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.MessageEntryId);
            parameters["messageEntryId"] = request.MessageEntryId;
        }
        return parameters;
    }

    private static Dictionary<string, object?> BuildSessionFileWriteParameters(SessionFileWrite request)
    {
        ValidateFileScope(request.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);
        return new()
        {
            ["sessionKey"] = request.Scope.SessionKey, ["agentId"] = request.Scope.AgentId, ["path"] = request.Path,
            ["content"] = request.Content, ["expectedHash"] = request.ExpectedHash,
            ["expectedSource"] = JsonSerializer.SerializeToElement(request.ExpectedSource, FilesJson)
        };
    }

    private static void RequireFileSession(JsonElement payload, SessionFileScope scope)
    {
        if (payload.ValueKind != JsonValueKind.Object || GetString(payload, "sessionKey") != scope.SessionKey)
            throw new JsonException("The Gateway returned a different session for file access.");
    }

    private static SessionFileContent ReadFileSnapshot(JsonElement payload, SessionFileScope scope, string path)
    {
        RequireFileSession(payload, scope);
        if (!payload.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.Object ||
            string.IsNullOrWhiteSpace(GetString(file, "path")))
            throw new JsonException("The Gateway returned an invalid file snapshot.");
        return ParseSessionFileContent(payload, scope.SessionKey, path);
    }

    internal static SessionFileSource? ParseSessionFileSource(JsonElement payload)
    {
        if (!payload.TryGetProperty("source", out var source)) return null;
        if (source.ValueKind != JsonValueKind.Object ||
            string.IsNullOrWhiteSpace(GetString(source, "workspaceId")) ||
            !source.TryGetProperty("root", out var root) ||
            root.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
            !source.TryGetProperty("fileRoot", out var fileRoot) ||
            fileRoot.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new JsonException("The Gateway returned an invalid session file source.");
        return new(GetString(source, "workspaceId")!, root.GetString(), fileRoot.GetString());
    }

    internal static bool TryResolveSessionDownloadUri(Uri origin, string? relative, out Uri uri)
    {
        uri = null!;
        const string prefix = "/api/artifacts/download/";
        if (relative is null || !relative.StartsWith(prefix, StringComparison.Ordinal) ||
            relative.Contains('\\') || relative.Contains('%') || relative.Contains('?') || relative.Contains('#'))
            return false;
        var segments = relative[prefix.Length..].Split('/');
        if (segments.Length != 2 || segments.Any(s => s.Length == 0 || s is "." or ".." ||
            s.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))))
            return false;
        var path = origin.AbsolutePath.TrimEnd('/') + relative;
        return Uri.TryCreate(new Uri(origin.GetLeftPart(UriPartial.Authority)), path, out uri!);
    }
}
