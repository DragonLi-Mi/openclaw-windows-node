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
    [InlineData("CON .txt", "download.bin")]
    [InlineData("COM\u00b9.log", "download.bin")]
    [InlineData("LPT\u00b2.txt", "download.bin")]
    [InlineData("CONOUT$", "download.bin")]
    public void DownloadSuggestionIsOnlyASafeLeaf(string input, string expected) =>
        Assert.Equal(expected, ConversationFilesController.SafeDownloadName(input));

    [Theory]
    [InlineData("notes.txt", ".txt")]
    [InlineData("archive.tar.gz", ".gz")]
    [InlineData("LICENSE", ".bin")]
    [InlineData("name.", ".bin")]
    public void SavePickerUsesConcreteExtensionsInsteadOfUnsupportedWildcard(string filename, string expected) =>
        Assert.Equal(expected, ConversationFilesController.DownloadExtension(filename));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunChangeDuringSaveReleasesBusyAndReconcilesOnlyExactCommittedText(bool committed)
    {
        using var fixture = new Fixture();
        await fixture.Controller.OpenAsync(Request);
        fixture.Controller.BeginEdit();
        fixture.Controller.UpdateNativeText(fixture.Controller.EditorTarget!, "captured save", 2, 1);
        var pending = new TaskCompletionSource<SessionFileContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Proxy.PendingWrite = pending.Task;
        var save = fixture.Controller.SaveAsync();
        Assert.True(fixture.Controller.View.Busy);
        fixture.Panels.Open("files", ConversationFilesController.FileContentId(Request), "replacement-run");
        Assert.False(fixture.Controller.View.Busy);
        Assert.Equal(ConversationFilesNotice.Uncertain, fixture.Controller.View.Notice);
        fixture.Proxy.Snapshot = Snapshot(committed ? "captured save" : "original", committed ? HashB : HashA);
        pending.SetResult(fixture.Proxy.Snapshot);
        await save;
        fixture.Proxy.PendingWrite = null;
        await fixture.Controller.SaveAsync();
        Assert.Single(fixture.Proxy.Writes);
        Assert.False(fixture.Controller.View.Busy);
        Assert.Equal(!committed, fixture.Controller.View.Document!.Dirty);
        Assert.Equal(committed ? ConversationFilesNotice.Saved : ConversationFilesNotice.Conflict, fixture.Controller.View.Notice);
        Assert.Equal(committed ? HashB : HashA, fixture.Controller.View.Document.ExpectedHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrSupersededUncertainReadKeepsDraftAndNeverResends(bool superseded)
    {
        using var fixture = new Fixture();
        await fixture.Controller.OpenAsync(Request);
        fixture.Controller.BeginEdit();
        fixture.Controller.UpdateNativeText(fixture.Controller.EditorTarget!, "uncertain save", 0, 0);
        var pendingWrite = new TaskCompletionSource<SessionFileContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Proxy.PendingWrite = pendingWrite.Task;
        var save = fixture.Controller.SaveAsync();
        fixture.Panels.Open("files", ConversationFilesController.FileContentId(Request), "new-run");
        pendingWrite.SetResult(Snapshot("uncertain save", HashB));
        await save;
        var pendingRead = new TaskCompletionSource<SessionFileContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Proxy.PendingRead = pendingRead.Task;
        var retry = fixture.Controller.SaveAsync();
        if (superseded)
        {
            fixture.Panels.Open("files", ConversationFilesController.FileContentId(Request), "third-run");
            pendingRead.SetResult(Snapshot("unrelated content", HashB));
        }
        else pendingRead.SetException(new TimeoutException("Synthetic get timeout"));
        await retry;
        Assert.False(fixture.Controller.View.Busy);
        Assert.Equal("uncertain save", fixture.Controller.View.Document!.Text);
        Assert.Equal("uncertain save", fixture.Controller.View.Document.PendingSaveText);
        Assert.True(fixture.Controller.View.Document.Dirty);
        Assert.Equal(HashA, fixture.Controller.View.Document.ExpectedHash);
        Assert.Equal(ConversationFilesNotice.Uncertain, fixture.Controller.View.Notice);
        Assert.Single(fixture.Proxy.Writes);
    }

    [Fact]
    public async Task UncertainReconciliationConfirmsCapturedSaveWithoutDiscardingNewerEdits()
    {
        using var fixture = new Fixture();
        await fixture.Controller.OpenAsync(Request);
        fixture.Controller.BeginEdit();
        fixture.Controller.UpdateNativeText(fixture.Controller.EditorTarget!, "captured save", 0, 0);
        var pending = new TaskCompletionSource<SessionFileContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Proxy.PendingWrite = pending.Task;
        var save = fixture.Controller.SaveAsync();
        fixture.Panels.Open("files", ConversationFilesController.FileContentId(Request), "new-run");
        fixture.Controller.UpdateNativeText(fixture.Controller.EditorTarget!, "newer local edits", 2, 1);
        fixture.Proxy.Snapshot = Snapshot("captured save", HashB);
        pending.SetResult(fixture.Proxy.Snapshot);
        await save;
        await fixture.Controller.SaveAsync();
        Assert.Single(fixture.Proxy.Writes);
        Assert.Equal("newer local edits", fixture.Controller.View.Document!.Text);
        Assert.Equal("captured save", fixture.Controller.View.Document.SavedText);
        Assert.True(fixture.Controller.View.Document.Dirty);
        Assert.Null(fixture.Controller.View.Document.PendingSaveText);
        Assert.Equal(HashB, fixture.Controller.View.Document.ExpectedHash);
        Assert.Equal(ConversationFilesNotice.PendingEdits, fixture.Controller.View.Notice);
    }

    [Fact]
    public async Task SelectionChangeDuringDownloadRollsBackDestinationAndReleasesBusy()
    {
        using var fixture = new Fixture();
        await fixture.Controller.OpenAsync(Request);
        var bytes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Proxy.PendingDownload = bytes.Task;
        var committed = false;
        var disposed = false;
        var download = fixture.Controller.DownloadAsync((_, _) => Task.FromResult<ConversationFileDestination?>(
            new(new MemoryStream(), () => { committed = true; return Task.CompletedTask; },
                () => { disposed = true; return ValueTask.CompletedTask; })));
        Assert.True(fixture.Controller.View.Busy);
        var other = Request with { Path = "other.txt" };
        fixture.Panels.Open("files", ConversationFilesController.FileContentId(other));
        Assert.False(fixture.Controller.View.Busy);
        await fixture.Controller.OpenAsync(other);
        bytes.SetResult();
        await download;
        Assert.False(committed);
        Assert.True(disposed);
        Assert.False(fixture.Controller.View.Busy);
        Assert.Equal(other, fixture.Controller.View.Document!.Request);
    }

    [Fact]
    public async Task SupersededSaveFinallyCannotClearNewDownloadBusyOwner()
    {
        using var fixture = new Fixture();
        await fixture.Controller.OpenAsync(Request);
        fixture.Controller.BeginEdit();
        fixture.Controller.UpdateNativeText(fixture.Controller.EditorTarget!, "pending", 0, 0);
        var old = new TaskCompletionSource<SessionFileContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Proxy.PendingWrite = old.Task;
        var save = fixture.Controller.SaveAsync();
        fixture.Panels.Open("files", ConversationFilesController.FileContentId(Request), "new-run");
        var bytes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Proxy.PendingDownload = bytes.Task;
        using var destination = new MemoryStream();
        var download = fixture.Controller.DownloadAsync((_, _) => Task.FromResult<ConversationFileDestination?>(
            new(destination, () => Task.CompletedTask, () => ValueTask.CompletedTask)));
        old.SetResult(Snapshot("pending", HashB));
        await save;
        Assert.True(fixture.Controller.View.Busy);
        bytes.SetResult();
        await download;
        Assert.False(fixture.Controller.View.Busy);
    }

    [Fact]
    public void DraftLookupUsesExplicitLatestRevisionNotDictionaryInsertionOrder()
    {
        using var fixture = new Fixture();
        var keyA = new ConversationFileDraftKey(fixture.Context.Identity, Request, new("/a", Source));
        var keyB = keyA with { Origin = new("/b", Source with { WorkspaceId = "b" }) };
        var a = new ConversationFileDraft(keyA, "old", "draft-a", HashA, ConversationFileText.Inspect("old"));
        var b = a with { Key = keyB, Text = "draft-b" };
        Assert.True(fixture.Drafts.Retain(a));
        Assert.True(fixture.Drafts.Retain(b));
        Assert.True(fixture.Drafts.Retain(a with { Text = "latest-a" }));
        Assert.Equal("latest-a", fixture.Drafts.Find(fixture.Context.Identity, Request)!.Text);
        fixture.Drafts.Clear();
        Assert.Null(fixture.Drafts.Find(fixture.Context.Identity, Request));
    }

    [Fact]
    public async Task CleanEditReservationIsReleasedOnCloseAndDraftLimitNotifiesOutsideLock()
    {
        using var fixture = new Fixture();
        await fixture.Controller.OpenAsync(Request);
        fixture.Controller.BeginEdit();
        var clean = fixture.Controller.EditorTarget!;
        Assert.True(fixture.Drafts.Contains(clean));
        fixture.Panels.Close("files");
        Assert.False(fixture.Drafts.Contains(clean));
        fixture.Panels.Open("files");
        await fixture.Controller.OpenAsync(Request);
        for (var index = 0; index < 32; index++)
        {
            var key = clean with { Request = Request with { Path = $"retained-{index}.txt" } };
            Assert.True(fixture.Drafts.Retain(new(key, "old", "unsaved", HashA, ConversationFileText.Inspect("old"))));
        }
        var observed = false;
        fixture.Controller.Changed += () =>
        {
            Assert.True(Task.Run(() => fixture.Controller.View).Wait(TimeSpan.FromSeconds(5)),
                "Changed must not be raised while the controller lock prevents another reader.");
            observed = true;
        };
        fixture.Controller.BeginEdit();
        Assert.True(observed);
        Assert.Equal(ConversationFilesNotice.DraftLimit, fixture.Controller.View.Notice);
    }
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
        Assert.True(fixture.Controller.CanEdit);
        Assert.Equal("utf8", fixture.Controller.View.Document.Snapshot.ContentEncoding);
        Assert.Equal("text", fixture.Controller.View.Document.Snapshot.PreviewKind);
    }

    [Theory]
    [InlineData("session_file_conflict")]
    [InlineData("session_file_too_large")]
    [InlineData("session_file_not_found")]
    public async Task KnownPreEffectSetRejectionClearsPendingAndAllowsExplicitRetry(string type)
    {
        using var fixture = new Fixture();
        await fixture.Controller.OpenAsync(Request);
        fixture.Controller.BeginEdit();
        fixture.Controller.UpdateNativeText(fixture.Controller.EditorTarget!, "draft", 0, 0);
        fixture.Proxy.WriteError = new GatewayRequestException("Unrelated message", "INVALID_REQUEST",
            System.Text.Json.JsonSerializer.SerializeToElement(new { type }));
        await fixture.Controller.SaveAsync();
        Assert.Null(fixture.Controller.View.Document!.PendingSaveText);
        Assert.NotEqual(ConversationFilesNotice.Conflict, fixture.Controller.View.Notice);
        fixture.Proxy.WriteError = null;
        await fixture.Controller.SaveAsync();
        Assert.Equal(2, fixture.Proxy.Writes.Count);
        Assert.False(fixture.Controller.View.Document.Dirty);
        Assert.Equal(ConversationFilesNotice.Saved, fixture.Controller.View.Notice);
    }

    [Fact]
    public async Task PostCommitForbiddenRemainsUncertainUntilMatchingCurrentRead()
    {
        using var fixture = new Fixture();
        await fixture.Controller.OpenAsync(Request);
        fixture.Controller.BeginEdit();
        fixture.Controller.UpdateNativeText(fixture.Controller.EditorTarget!, "written before error", 0, 0);
        fixture.Proxy.CommitBeforeWriteError = true;
        fixture.Proxy.WriteError = new GatewayRequestException("Authorization changed", "FORBIDDEN");
        await fixture.Controller.SaveAsync();
        Assert.Equal("written before error", fixture.Controller.View.Document!.PendingSaveText);
        Assert.Equal(ConversationFilesNotice.Permission, fixture.Controller.View.Notice);
        await fixture.Controller.OpenAsync(Request);
        Assert.Null(fixture.Controller.View.Document!.PendingSaveText);
        Assert.Equal(HashB, fixture.Controller.View.Document.ExpectedHash);
        Assert.False(fixture.Controller.View.Document.Dirty);
        Assert.Single(fixture.Proxy.Writes);
    }

    [Fact]
    public async Task RemountReconcilesCapturedSaveBeforeRestoringNewerUnsavedDraft()
    {
        using var fixture = new Fixture();
        await fixture.Controller.OpenAsync(Request);
        fixture.Controller.BeginEdit();
        fixture.Controller.UpdateNativeText(fixture.Controller.EditorTarget!, "sent", 0, 0);
        fixture.Proxy.PendingWrite = Task.FromException<SessionFileContent>(new TimeoutException("Synthetic lost reply"));
        await fixture.Controller.SaveAsync();
        fixture.Controller.UpdateNativeText(fixture.Controller.EditorTarget!, "newer local edits", 3, 2);
        fixture.Controller.Dispose();
        fixture.Proxy.Snapshot = Snapshot("sent", HashB);
        fixture.Proxy.Epoch++;
        fixture.Context = fixture.Context with { ConnectionEpoch = fixture.Proxy.Epoch };
        fixture.Panels.Refresh();
        using var reconnected = new ConversationFilesController(fixture.Panels, fixture.Drafts);
        Assert.Equal("newer local edits", reconnected.View.Document!.Text);
        Assert.Equal("sent", reconnected.View.Document.SavedText);
        Assert.Equal(HashB, reconnected.View.Document.ExpectedHash);
        Assert.Null(reconnected.View.Document.PendingSaveText);
        Assert.True(reconnected.View.Document.Dirty);
        Assert.Equal(ConversationFilesNotice.PendingEdits, reconnected.View.Notice);
        Assert.Single(fixture.Proxy.Writes);
    }

    [Fact]
    public async Task SaveNeverDispatchesIfAttemptedTextCannotBeRetained()
    {
        using var fixture = new Fixture();
        await fixture.Controller.OpenAsync(Request);
        fixture.Controller.BeginEdit();
        fixture.Controller.UpdateNativeText(fixture.Controller.EditorTarget!, "keep before dispatch", 0, 0);
        var pending = new TaskCompletionSource<SessionFileContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Proxy.PendingRead = pending.Task;
        var save = fixture.Controller.SaveAsync();
        fixture.Drafts.Clear();
        for (var index = 0; index < 32; index++)
        {
            var key = new ConversationFileDraftKey(fixture.Context.Identity, Request with { Path = $"other-{index}.txt" },
                new("/conversation", Source));
            Assert.True(fixture.Drafts.Retain(new(key, "old", "dirty", HashA, ConversationFileText.Inspect("old"))));
        }
        pending.SetResult(Snapshot());
        await save;
        Assert.Empty(fixture.Proxy.Writes);
        Assert.Equal(ConversationFilesNotice.DraftLimit, fixture.Controller.View.Notice);
        Assert.False(fixture.Controller.View.Busy);
        Assert.Null(fixture.Controller.View.Document!.PendingSaveText);
    }

    [Fact]
    public async Task TooLargePreviewStillOffersOriginalDownloadWithoutInventingWriteAuthority()
    {
        using var fixture = new Fixture();
        fixture.Proxy.PendingRead = Task.FromException<SessionFileContent>(new GatewayRequestException("Too large",
            "INVALID_REQUEST", System.Text.Json.JsonSerializer.SerializeToElement(new
                { type = "session_file_too_large", size = 2097152 })));
        await fixture.Controller.OpenAsync(Request);
        Assert.Equal(ConversationFilesStatus.Ready, fixture.Controller.View.Status);
        Assert.Equal(2097152, fixture.Controller.View.Document!.Snapshot.Size);
        Assert.Equal("unsupported", fixture.Controller.View.Document.Snapshot.PreviewKind);
        Assert.False(fixture.Controller.CanEdit);
        var committed = false;
        using var output = new MemoryStream();
        await fixture.Controller.DownloadAsync((_, _) => Task.FromResult<ConversationFileDestination?>(
            new(output, () => { committed = true; return Task.CompletedTask; }, () => ValueTask.CompletedTask)));
        Assert.True(committed);
        Assert.Equal(ConversationFilesNotice.Downloaded, fixture.Controller.View.Notice);
    }

    [Fact]
    public async Task EditLimitChecksUtf8BytesRatherThanOnlyNativeCharacterCount()
    {
        using var fixture = new Fixture();
        await fixture.Controller.OpenAsync(Request);
        fixture.Controller.BeginEdit();
        fixture.Controller.UpdateNativeText(fixture.Controller.EditorTarget!, new string('\u4e2d', 350000), 0, 0);
        await fixture.Controller.SaveAsync();
        Assert.Empty(fixture.Proxy.Writes);
        Assert.Equal(ConversationFilesNotice.InvalidContent, fixture.Controller.View.Notice);
        Assert.Null(fixture.Controller.View.Document!.PendingSaveText);
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
    public async Task DocumentACannotAcquireFileOperationsAfterRootSelectsContentB()
    {
        using var fixture = new Fixture();
        await fixture.Controller.OpenAsync(Request);
        fixture.Controller.BeginEdit();
        fixture.Controller.UpdateNativeText(fixture.Controller.EditorTarget!, "private draft A", 0, 0);
        var reads = fixture.Proxy.FileReads;
        var other = Request with { Path = "other.txt" };
        fixture.Panels.Open("files", ConversationFilesController.FileContentId(other));
        Assert.False(fixture.Controller.CanEdit);
        await fixture.Controller.SaveAsync();
        await fixture.Controller.ReloadAsync();
        var pickerOpened = false;
        await fixture.Controller.DownloadAsync((_, _) =>
        {
            pickerOpened = true;
            return Task.FromResult<ConversationFileDestination?>(null);
        });
        Assert.Empty(fixture.Proxy.Writes);
        Assert.Equal(reads, fixture.Proxy.FileReads);
        Assert.Equal(0, fixture.Proxy.DownloadGrants);
        Assert.False(pickerOpened);
        Assert.Equal("private draft A", fixture.Controller.View.Document!.Text);
    }

    [Fact]
    public async Task CanonicalUserChangeOnSameDeviceCannotRestorePriorUsersDraft()
    {
        using var fixture = new Fixture();
        await fixture.Controller.OpenAsync(Request);
        fixture.Controller.BeginEdit();
        fixture.Controller.UpdateNativeText(fixture.Controller.EditorTarget!, "user-one private draft", 0, 0);
        fixture.Controller.Dispose();
        fixture.Context = fixture.Context with
        {
            Identity = fixture.Context.Identity with { UserId = "user-two" },
            AccountRevision = fixture.Context.AccountRevision + 1
        };
        fixture.Panels.Refresh();
        fixture.Panels.Open("files");
        using var other = new ConversationFilesController(fixture.Panels, fixture.Drafts);
        await other.OpenAsync(Request);
        Assert.False(other.View.Document!.Restored);
        Assert.Equal("original", other.View.Document.Text);
        Assert.Null(fixture.Drafts.Find(fixture.Context.Identity, Request));
    }

    [Fact]
    public async Task UnknownCanonicalUserCannotReadRestoreOrSaveFiles()
    {
        using var fixture = new Fixture();
        await fixture.Controller.OpenAsync(Request);
        fixture.Controller.BeginEdit();
        fixture.Controller.UpdateNativeText(fixture.Controller.EditorTarget!, "retained private draft", 0, 0);
        var reads = fixture.Proxy.FileReads;
        fixture.Context = fixture.Context with { Identity = fixture.Context.Identity with { UserId = null } };
        fixture.Panels.Refresh();
        Assert.False(fixture.Panels.IsReady);
        Assert.False(fixture.Panels.Open("files"));
        await fixture.Controller.OpenAsync(Request);
        await fixture.Controller.SaveAsync();
        Assert.Null(fixture.Controller.View.Document);
        Assert.Equal(reads, fixture.Proxy.FileReads);
        Assert.Empty(fixture.Proxy.Writes);
    }

    [Fact]
    public async Task ProfileRevisionRevokesPendingReadWithoutPublishingItsResult()
    {
        using var fixture = new Fixture();
        var pending = new TaskCompletionSource<SessionFileContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Proxy.PendingRead = pending.Task;
        var opening = fixture.Controller.OpenAsync(Request);
        var lease = fixture.Controller.CaptureLease()!;
        fixture.Context = fixture.Context with { AccountRevision = fixture.Context.AccountRevision + 1 };
        fixture.Panels.Refresh();
        Assert.False(lease.IsCurrent);
        pending.SetResult(Snapshot("old profile revision"));
        await opening;
        Assert.Null(fixture.Controller.View.Document);
        Assert.Empty(fixture.Proxy.Writes);
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
            Context = new(new("gateway-one", "device-one", Scope.SessionKey, "session-one", Scope.AgentId, "user-one"),
                Client, 12, AccountRevision: 1);
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
        public Task<SessionFileContent>? PendingWrite;
        public Task? PendingDownload;
        public GatewayRequestException? WriteError;
        public bool CommitBeforeWriteError;
        public int FileReads;
        public int DownloadGrants;
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
                case "GetSessionFileAsync":
                    FileReads++;
                    return PendingRead ?? Task.FromResult(Snapshot);
                case "SetSessionFileAsync":
                    var write = Assert.IsType<SessionFileWrite>(args![0]);
                    Assert.Equal(Epoch, args[1]);
                    Writes.Add(write);
                    if (PendingWrite is not null) return PendingWrite;
                    if (WriteError is not null && !CommitBeforeWriteError)
                        return Task.FromException<SessionFileContent>(WriteError);
                    Snapshot = ConversationFilesTests.Snapshot(write.Content, HashB, write.ExpectedSource);
                    if (WriteError is not null) return Task.FromException<SessionFileContent>(WriteError);
                    return Task.FromResult(new SessionFileContent { Key = Scope.SessionKey, Root = "/conversation",
                        Source = write.ExpectedSource, Path = write.Path, Name = "notes.txt", Kind = "modified", Hash = HashB });
                case "GetSessionFileDownloadAsync":
                    DownloadGrants++;
                    return Task.FromResult(new SessionFileDownload("file.bin", "application/octet-stream",
                        "/api/artifacts/download/test/grant", DateTimeOffset.MaxValue));
                case "DownloadSessionFileAsync": return PendingDownload ?? Task.CompletedTask;
                default: throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }
}
