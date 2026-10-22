using System.Reflection;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ConversationFilesTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly SessionFileScope Scope = new("agent:research:one", "research");
    private static readonly SessionFileSource Source = new("workspace-one", "/conversation", "/conversation");
    private static readonly SessionFileRequest Request = new(Scope, "notes.txt");

    [Theory]
    [InlineData("alpha\nbeta\n", "alpha\rbeta\r", "alpha\nbeta\n")]
    [InlineData("alpha\r\nbeta\r\n", "alpha\rbeta\r", "alpha\r\nbeta\r\n")]
    [InlineData("alpha\rbeta\r", "alpha\rbeta\r", "alpha\rbeta\r")]
    [InlineData("\uFEFFalpha\nbeta", "alpha\rbeta", "\uFEFFalpha\nbeta")]
    [InlineData("\uFEFFalpha\r\nbeta", "alpha\rbeta", "\uFEFFalpha\r\nbeta")]
    [InlineData("no final newline", "no final newline", "no final newline")]
    [InlineData("", "", "")]
    public void NativeEditorRoundTripPreservesExactRepresentation(string original, string native, string expected)
    {
        var format = ConversationFileText.Inspect(original);
        Assert.True(format.Uniform);
        Assert.Equal(native, format.ToNative(original));
        Assert.Equal(expected, format.FromNative(native));
    }

    [Fact]
    public void MixedLineEndingsCannotBeSavedThroughNativeNormalization()
    {
        var format = ConversationFileText.Inspect("first\r\nsecond\nthird");
        Assert.False(format.Uniform);
        Assert.Throws<InvalidOperationException>(() => format.FromNative("first\rsecond\rthird"));
    }

    [Theory]
    [InlineData("../../notes.txt", "notes.txt")]
    [InlineData("C:\\private\\notes.txt", "notes.txt")]
    [InlineData("CON.txt", "download.bin")]
    [InlineData("report.txt:stream", "report.txtstream")]
    [InlineData("NUL", "download.bin")]
    [InlineData("..", "download.bin")]
    public void DownloadSuggestionIsOnlyASafeLeaf(string input, string expected) =>
        Assert.Equal(expected, ConversationFilesController.SafeDownloadName(input));

    [Fact]
    public async Task SaveTargetsSelectedSessionWithOriginalSourceHashAndFileLineEndings()
    {
        using var fixture = new Fixture("first\r\nsecond");
        await fixture.Controller.OpenAsync(Request);
        fixture.Controller.BeginEdit();
        fixture.Controller.UpdateNativeText(fixture.Controller.EditorTarget!, "first\rchanged", 6, 7);
        await fixture.Controller.SaveAsync();
        var write = Assert.Single(fixture.Proxy.Writes);
        Assert.Equal(Scope, write.Scope);
        Assert.Equal("notes.txt", write.Path);
        Assert.Equal(Source, write.ExpectedSource);
        Assert.Equal(HashA, write.ExpectedHash);
        Assert.Equal("first\r\nchanged", write.Content);
        Assert.False(fixture.Controller.View.Document!.Dirty);
        Assert.Equal(ConversationFilesNotice.Saved, fixture.Controller.View.Notice);
    }

    [Fact]
    public async Task ConflictKeepsDraftAndExplicitOverwriteUsesReviewedFreshHash()
    {
        using var fixture = new Fixture();
        await fixture.Controller.OpenAsync(Request);
        fixture.Controller.BeginEdit();
        fixture.Controller.UpdateNativeText(fixture.Controller.EditorTarget!, "local draft", 4, 3);
        fixture.Proxy.Snapshot = Snapshot("other writer", HashB);
        await fixture.Controller.SaveAsync();
        Assert.Empty(fixture.Proxy.Writes);
        Assert.Equal("local draft", fixture.Controller.View.Document!.Text);
        Assert.Equal("other writer", fixture.Controller.View.Document.LatestText);
        Assert.Equal(ConversationFilesNotice.Conflict, fixture.Controller.View.Notice);
        await fixture.Controller.SaveAsync(overwrite: true);
        Assert.Equal(HashB, Assert.Single(fixture.Proxy.Writes).ExpectedHash);
    }

    [Fact]
    public async Task SourceChangeCannotRetargetOverwriteOrReload()
    {
        using var fixture = new Fixture();
        await fixture.Controller.OpenAsync(Request);
        fixture.Controller.BeginEdit();
        fixture.Controller.UpdateNativeText(fixture.Controller.EditorTarget!, "keep me", 1, 2);
        fixture.Proxy.Snapshot = Snapshot("replacement", HashB, Source with { WorkspaceId = "other-workspace" });
        await fixture.Controller.SaveAsync();
        await fixture.Controller.SaveAsync(overwrite: true);
        await fixture.Controller.ReloadAsync();
        Assert.Empty(fixture.Proxy.Writes);
        Assert.Equal("keep me", fixture.Controller.View.Document!.Text);
        Assert.Equal(ConversationFilesNotice.SourceChanged, fixture.Controller.View.Notice);
        Assert.NotNull(fixture.Drafts.Find(fixture.Context.Identity, Request));
    }

    [Fact]
    public async Task CloseAndReconnectRemountRevalidatesRetainedDraftBeforeSave()
    {
        using var fixture = new Fixture();
        await fixture.Controller.OpenAsync(Request);
        fixture.Controller.BeginEdit();
        fixture.Controller.UpdateNativeText(fixture.Controller.EditorTarget!, "retained draft", 4, 2);
        fixture.Controller.Dispose();
        fixture.Proxy.Epoch++;
        fixture.Context = fixture.Context with { ConnectionEpoch = fixture.Proxy.Epoch };
        fixture.Proxy.Snapshot = Snapshot("changed while offline", HashB);
        fixture.Panels.Refresh();
        using var reconnected = new ConversationFilesController(fixture.Panels, fixture.Drafts);
        await reconnected.OpenAsync(Request);
        Assert.True(reconnected.View.Document!.Restored);
        Assert.Equal(4, reconnected.View.Document.SelectionStart);
        Assert.Equal(ConversationFilesNotice.Conflict, reconnected.View.Notice);
        await reconnected.SaveAsync();
        Assert.Empty(fixture.Proxy.Writes);
        Assert.Equal("retained draft", reconnected.View.Document.Text);
    }

    [Fact]
    public async Task OtherPrincipalCannotRestoreSameSessionPathDraft()
    {
        using var fixture = new Fixture();
        await fixture.Controller.OpenAsync(Request);
        fixture.Controller.BeginEdit();
        fixture.Controller.UpdateNativeText(fixture.Controller.EditorTarget!, "private unsaved text", 0, 0);
        fixture.Controller.Dispose();
        fixture.Proxy.Principal = "other-device";
        fixture.Context = fixture.Context with { Identity = fixture.Context.Identity with { SigningDeviceId = "other-device" } };
        fixture.Panels.Refresh();
        fixture.Panels.Open("files");
        using var other = new ConversationFilesController(fixture.Panels, fixture.Drafts);
        await other.OpenAsync(Request);
        Assert.False(other.View.Document!.Restored);
        Assert.Equal("original", other.View.Document.Text);
    }

    [Fact]
    public async Task LateReadAfterNavigationCannotPublishOrAdmitSave()
    {
        using var fixture = new Fixture();
        var pending = new TaskCompletionSource<SessionFileContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Proxy.PendingRead = pending.Task;
        var opening = fixture.Controller.OpenAsync(Request);
        fixture.Panels.ExpectSelection("agent:research:two");
        pending.SetResult(Snapshot("stale"));
        await opening;
        Assert.Null(fixture.Controller.View.Document);
        await fixture.Controller.SaveAsync();
        Assert.Empty(fixture.Proxy.Writes);
    }

    [Fact]
    public async Task OldEditorCallbackCannotMutateReplacementFile()
    {
        using var fixture = new Fixture();
        await fixture.Controller.OpenAsync(Request);
        fixture.Controller.BeginEdit();
        var oldTarget = fixture.Controller.EditorTarget!;
        var oldLease = fixture.Controller.CaptureLease()!;
        await fixture.Controller.OpenAsync(Request with { Path = "other.txt" });
        fixture.Controller.BeginEdit();
        fixture.Controller.UpdateNativeText(oldTarget, "old callback text", 0, 0);
        Assert.Equal("original", fixture.Controller.View.Document!.Text);
        Assert.False(oldLease.IsCurrent);
    }

    [Fact]
    public async Task SaveShortcutDuringReadDoesNotInvalidatePendingPreview()
    {
        using var fixture = new Fixture();
        var pending = new TaskCompletionSource<SessionFileContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Proxy.PendingRead = pending.Task;
        var opening = fixture.Controller.OpenAsync(Request);
        await fixture.Controller.SaveAsync();
        pending.SetResult(Snapshot());
        await opening;
        Assert.NotNull(fixture.Controller.View.Document);
        Assert.Equal(ConversationFilesStatus.Ready, fixture.Controller.View.Status);
        Assert.Empty(fixture.Proxy.Writes);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("scope")]
    [InlineData("source")]
    [InlineData("hash")]
    [InlineData("binary")]
    [InlineData("mixed")]
    public async Task UnsupportedAuthorityAndContentCannotEnterEditMode(string reason)
    {
        using var fixture = new Fixture();
        if (reason == "scope") fixture.Proxy.Admin = false;
        if (reason == "source") fixture.Proxy.Snapshot.Source = null;
        if (reason == "hash") fixture.Proxy.Snapshot.Hash = null;
        if (reason == "binary") fixture.Proxy.Snapshot.PreviewKind = "unsupported";
        if (reason == "mixed") fixture.Proxy.Snapshot.Content = "one\r\ntwo\n";
        await fixture.Controller.OpenAsync(reason == "snapshot" ? Request with { MessageEntryId = "gateway-entry-one" } : Request);
        fixture.Controller.BeginEdit();
        Assert.False(fixture.Controller.View.Document!.Editing);
        Assert.False(fixture.Controller.CanEdit);
    }

    private static SessionFileContent Snapshot(string text = "original", string hash = HashA, SessionFileSource? source = null) =>
        new() { Key = Scope.SessionKey, Path = "notes.txt", Name = "notes.txt", Root = "/conversation",
            Source = source ?? Source, Content = text, Hash = hash, PreviewKind = "text", ContentEncoding = "utf8" };

    private sealed class Fixture : IDisposable
    {
        internal IOperatorGatewayClient Client { get; }
        internal GatewayProxy Proxy { get; }
        internal ChatConversationContext Context;
        internal ChatConversationPanelState Panels { get; }
        internal ConversationFileDrafts Drafts { get; } = new();
        internal ConversationFilesController Controller { get; }

        internal Fixture(string text = "original")
        {
            Client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
            Proxy = (GatewayProxy)Client;
            Proxy.Snapshot = Snapshot(text);
            Context = new(new("gateway-one", "device-one", Scope.SessionKey, "session-one", Scope.AgentId), Client, 12);
            Panels = new(key => key == Scope.SessionKey ? Context : null);
            Panels.Bind(Scope.SessionKey);
            Panels.Open("files");
            Controller = new(Panels, Drafts);
        }
        public void Dispose() { Controller.Dispose(); Panels.Dispose(); Drafts.Clear(); }
    }

    public class GatewayProxy : DispatchProxy
    {
        public long Epoch = 12;
        public string Principal = "device-one";
        public bool Admin = true;
        public SessionFileContent Snapshot = ConversationFilesTests.Snapshot();
        public Task<SessionFileContent>? PendingRead;
        public List<SessionFileWrite> Writes = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_IsConnectedToGateway": return true;
                case "get_SessionMutationConnectionEpoch": return (long?)Epoch;
                case "get_AuthenticatedSigningDeviceId": return Principal;
                case "get_GrantedOperatorScopes": return Admin ? new[] { "operator.read", "operator.admin" } : ["operator.read"];
                case "get_SessionFilesCapabilities": return new SessionFilesCapabilities(SessionFilesReadiness.Ready, true, true, true, true, true);
                case "ListSessionFilesAsync": return Task.FromResult(new SessionFileList { Key = Scope.SessionKey, Root = "/conversation" });
                case "GetSessionFileAsync": return PendingRead ?? Task.FromResult(Snapshot);
                case "SetSessionFileAsync":
                    var write = Assert.IsType<SessionFileWrite>(args![0]);
                    Assert.Equal(Epoch, args[1]);
                    Writes.Add(write);
                    Snapshot = ConversationFilesTests.Snapshot(write.Content, HashB, write.ExpectedSource);
                    return Task.FromResult(Snapshot);
                default: throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }
}
