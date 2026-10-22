using System.Text;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayProtocolLiveRoundTripTests
{
    private const string FilesKey = "agent:research:selected";
    private const string FilesHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly SessionFileScope FilesScope = new(FilesKey, "research");
    private static readonly SessionFileSource FilesSource = new("workspace-selected", "/work/selected", "/work/selected");

    private static void ConfigureFilesHello(LoopbackGatewayServer server, bool admin = true, string[]? methods = null)
    {
        server.OnMethod("connect", _ => new
        {
            type = "hello-ok", protocol = 4,
            auth = new { scopes = admin ? new[] { "operator.admin", "operator.read" } : ["operator.read"] },
            features = new { methods = methods ?? new[] { "sessions.files.list", "sessions.files.get",
                "sessions.files.set", "sessions.files.download", "sessions.diff" } }
        });
    }

    private static object FileSnapshot(string content = "original") => new
    {
        sessionKey = FilesKey, root = "/work/selected",
        source = new { workspaceId = "workspace-selected", root = "/work/selected", fileRoot = "/work/selected" },
        file = new { path = "notes.txt", workspacePath = "notes.txt", name = "notes.txt", kind = "modified",
            missing = false, content, hash = FilesHash, contentEncoding = "utf8", previewKind = "text", mimeType = "text/plain" }
    };

    [Fact]
    public async Task SessionFilesTransport_UsesExactAgentSessionAndCasSourceOnActualWire()
    {
        using var server = new LoopbackGatewayServer();
        ConfigureFilesHello(server);
        server.OnMethod("sessions.files.list", _ => new
        {
            sessionKey = FilesKey, root = "/work/selected", gitCheckout = true,
            files = new[] { new { path = "notes.txt", name = "notes.txt", kind = "modified", missing = false } },
            browser = new { path = "src", parentPath = "", search = "notes", truncated = true,
                entries = new[] { new { path = "notes.txt", name = "notes.txt", kind = "file" } } }
        });
        server.OnMethod("sessions.files.get", _ => FileSnapshot());
        server.OnMethod("sessions.files.set", _ => new
        {
            sessionKey = FilesKey, root = "/work/selected",
            source = new { workspaceId = "workspace-selected", root = "/work/selected", fileRoot = "/work/selected" },
            file = new { path = "notes.txt", name = "notes.txt", kind = "modified", missing = false, hash = FilesHash }
        });
        server.OnMethod("sessions.diff", _ => new
        {
            sessionKey = FilesKey, root = "/work/selected", branch = "task", baseRef = "main",
            additions = 2, deletions = 1, computing = false, truncated = true,
            files = new[] { new { path = "notes.txt", status = "modified", additions = 2, deletions = 1,
                patch = "@@ -1 +1 @@\n-old\n+new\n", binary = false, truncated = true } },
            commits = new[] { new { sha = "abc123", subject = "Selected commit" } }
        });
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server, configureHandshake: false);
        var epoch = client.SessionMutationConnectionEpoch!.Value;
        var listing = await client.ListSessionFilesAsync(FilesScope, epoch, "src", "notes");
        Assert.True(listing.GitCheckout);
        Assert.True(listing.Browser!.Truncated);
        var snapshot = await client.GetSessionFileAsync(new(FilesScope, "notes.txt", "gateway-entry"), epoch);
        Assert.Equal(FilesSource, snapshot.Source);
        Assert.Equal(FilesHash, snapshot.Hash);
        Assert.Equal("utf8", snapshot.ContentEncoding);
        Assert.Equal("text", snapshot.PreviewKind);
        var saved = await client.SetSessionFileAsync(new(FilesScope, "notes.txt", "\uFEFFedited\r\n", FilesHash, FilesSource), epoch);
        Assert.Null(saved.Content);
        Assert.Equal(FilesHash, saved.Hash);
        Assert.Equal(FilesSource, saved.Source);
        var diff = await client.GetSessionDiffAsync(new(FilesScope, SessionDiffScope.Commit, "abc123"), epoch);
        Assert.True(diff.Truncated);
        Assert.Equal(2, Assert.Single(diff.Files).Additions);
        Assert.Equal("Selected commit", Assert.Single(diff.Commits!).Subject);
        foreach (var method in new[] { "sessions.files.list", "sessions.files.get", "sessions.files.set", "sessions.diff" })
        {
            using var frame = JsonDocument.Parse(server.FrameFor(method));
            var parameters = frame.RootElement.GetProperty("params");
            Assert.Equal(FilesKey, parameters.GetProperty("sessionKey").GetString());
            Assert.Equal("research", parameters.GetProperty("agentId").GetString());
            Assert.False(parameters.TryGetProperty("key", out _));
            _output.WriteLine(method + ": exact selected scope captured");
        }
        using var writeFrame = JsonDocument.Parse(server.FrameFor("sessions.files.set"));
        var write = writeFrame.RootElement.GetProperty("params");
        Assert.Equal(new[] { "agentId", "content", "expectedHash", "expectedSource", "path", "sessionKey" },
            write.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(FilesHash, write.GetProperty("expectedHash").GetString());
        Assert.Equal("workspace-selected", write.GetProperty("expectedSource").GetProperty("workspaceId").GetString());
        Assert.Equal("/work/selected", write.GetProperty("expectedSource").GetProperty("fileRoot").GetString());
        using var getFrame = JsonDocument.Parse(server.FrameFor("sessions.files.get"));
        Assert.Equal("gateway-entry", getFrame.RootElement.GetProperty("params").GetProperty("messageEntryId").GetString());
        await client.DisconnectAsync();
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("cancelled")]
    [InlineData("scope")]
    [InlineData("hash")]
    [InlineData("advertisement")]
    public async Task SessionFilesTransport_InvalidWriteNeverReachesWire(string invalid)
    {
        using var server = new LoopbackGatewayServer();
        ConfigureFilesHello(server, invalid != "scope",
            invalid == "advertisement" ? ["sessions.files.get"] : null);
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server, configureHandshake: false);
        var epoch = client.SessionMutationConnectionEpoch!.Value;
        using var cancellation = new CancellationTokenSource();
        if (invalid == "cancelled") cancellation.Cancel();
        var error = await Record.ExceptionAsync(() => client.SetSessionFileAsync(
            new(FilesScope, "notes.txt", "draft", invalid == "hash" ? "not-a-hash" : FilesHash, FilesSource),
            invalid == "stale" ? epoch + 1 : epoch, cancellation.Token));
        Assert.NotNull(error);
        Assert.False(server.HasFrame("sessions.files.set"));
        await client.DisconnectAsync();
    }

    [Theory]
    [InlineData("session_file_conflict")]
    [InlineData("session_file_source_changed")]
    public async Task SessionFilesTransport_StructuredSaveFailureDoesNotBecomeSuccess(string type)
    {
        using var server = new LoopbackGatewayServer();
        ConfigureFilesHello(server);
        server.OnMethod("sessions.files.set", _ => new LoopbackResponse(false, null,
            new { code = "INVALID_REQUEST", message = "File changed", details = new { type, currentHash = FilesHash } }));
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server, configureHandshake: false);
        var error = await Assert.ThrowsAsync<GatewayRequestException>(() => client.SetSessionFileAsync(
            new(FilesScope, "notes.txt", "draft", FilesHash, FilesSource), client.SessionMutationConnectionEpoch!.Value));
        Assert.Equal(type, error.DetailType);
        Assert.Equal("INVALID_REQUEST", error.Code);
        await client.DisconnectAsync();
    }

    [Fact]
    public async Task SessionFilesTransport_DownloadUsesOriginalHttpBytesAndNeverPreviewBytes()
    {
        using var server = new LoopbackGatewayServer();
        ConfigureFilesHello(server);
        var original = new byte[] { 0, 255, 128, 64, 32, 13, 10 };
        server.ServeDownload(original);
        server.OnMethod("sessions.files.download", _ => new
        {
            filename = "original.bin", mimeType = "application/octet-stream",
            url = "/api/artifacts/download/selected/grant", expiresAt = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O")
        });
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server, configureHandshake: false);
        var epoch = client.SessionMutationConnectionEpoch!.Value;
        var grant = await client.GetSessionFileDownloadAsync(new(FilesScope, "original.bin", "message-one"), epoch);
        using var destination = new MemoryStream();
        await client.DownloadSessionFileAsync(grant, epoch, destination);
        Assert.Equal(original, destination.ToArray());
        Assert.Equal("/api/artifacts/download/selected/grant", Assert.Single(server.HttpRequests));
        Assert.False(server.HasFrame("sessions.files.get"));
        using var frame = JsonDocument.Parse(server.FrameFor("sessions.files.download"));
        Assert.Equal("research", frame.RootElement.GetProperty("params").GetProperty("agentId").GetString());
        Assert.Equal("message-one", frame.RootElement.GetProperty("params").GetProperty("messageEntryId").GetString());
        await client.DisconnectAsync();
    }

    [Theory]
    [InlineData("https://other.invalid/api/artifacts/download/a/b")]
    [InlineData("file:///C:/secret")]
    [InlineData("//other.invalid/api/artifacts/download/a/b")]
    [InlineData("/api/artifacts/download/../secret")]
    [InlineData("/api/artifacts/download/a/%2e%2e")]
    [InlineData("/api/artifacts/download/a/b?redirect=elsewhere")]
    public void SessionFilesTransport_RejectsNonGrantDownloadTargets(string target) =>
        Assert.False(OpenClawGatewayClient.TryResolveSessionDownloadUri(new("https://gateway.invalid"), target, out _));

    [Fact]
    public void SessionFilesTransport_PreservesConfiguredGatewayBasePath()
    {
        Assert.True(OpenClawGatewayClient.TryResolveSessionDownloadUri(new("https://gateway.invalid/prefix/"),
            "/api/artifacts/download/connection/ticket", out var uri));
        Assert.Equal("https://gateway.invalid/prefix/api/artifacts/download/connection/ticket", uri.AbsoluteUri);
    }
}
