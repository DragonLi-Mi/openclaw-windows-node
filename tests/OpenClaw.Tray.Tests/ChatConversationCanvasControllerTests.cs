using System.Reflection;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatConversationCanvasControllerTests
{
    public class GatewayProxy : DispatchProxy
    {
        public long Epoch = 12;
        public bool Connected = true;
        public int Reads;
        public long PolicyRevision;
        public EventHandler<CanvasPolicyInvalidation>? PolicyChanged;
        public Func<CanvasDocumentRequest, long, CancellationToken, Task<CanvasDocumentView>> Read = (_, _, _) =>
            Task.FromResult(Document("default"));

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case "get_SessionMutationConnectionEpoch": return (long?)Epoch;
                case "get_IsConnectedToGateway": return Connected;
                case "get_AuthenticatedSigningDeviceId": return "signing-one";
                case "get_CanvasPolicyRevision": return PolicyRevision;
                case "add_CanvasPolicyInvalidated": PolicyChanged += (EventHandler<CanvasPolicyInvalidation>)args![0]!; return null;
                case "remove_CanvasPolicyInvalidated": PolicyChanged -= (EventHandler<CanvasPolicyInvalidation>)args![0]!; return null;
                case "ViewCanvasDocumentAsync":
                    Reads++;
                    return Read((CanvasDocumentRequest)args![0]!, (long)args[1]!, (CancellationToken)args[2]!);
                default: throw new NotSupportedException(method?.Name);
            }
        }
    }

    private static CanvasDocumentView Document(string html) =>
        new(html, new Uri("https://gateway.example"), new Uri("https://sandbox.example/mcp-app-sandbox?v=one"));

    private sealed class Fixture : IDisposable
    {
        internal readonly GatewayProxy Gateway;
        internal readonly ChatConversationPanelState Panels;
        internal readonly ChatConversationCanvasSelection Selection;
        internal readonly ChatConversationCanvasController Controller;
        internal string ProfileId = "profile-one";
        internal long AccountRevision = 1;

        internal Fixture(Func<ChatConversationCanvasPrompt, CancellationToken, Task<ChatConversationCanvasPromptResult>>? submit = null,
            Func<TimeSpan, CancellationToken, Task>? waitForRetry = null)
        {
            var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
            Gateway = (GatewayProxy)client;
            Panels = new(key => ChatConversationContext.Capture("gateway-one", client,
                new SessionInfo { Key = key, SessionId = key + "-id", AgentId = "research" },
                ProfileId, AccountRevision));
            Panels.Bind("agent:research:one");
            Panels.Open("canvas", "cv_one", "run-one");
            Selection = new(Panels.Context!.Identity,
                new(ConversationCanvasContentKind.Document, "cv_one",
                    "/__openclaw__/canvas/documents/cv_one/index.html", "Owned document", true),
                "message-one", "run-one");
            Controller = new(Panels, Selection, submit ?? ((_, _) => Task.FromResult(ChatConversationCanvasPromptResult.Accepted)),
                waitForRetry);
        }

        internal async Task Ready()
        {
            await Controller.LoadAsync();
            Controller.SurfaceReady(Controller.Snapshot.Operation!);
            Assert.Equal(ChatConversationCanvasPhase.Ready, Controller.Snapshot.Phase);
        }

        public void Dispose() { Controller.Dispose(); Panels.Dispose(); }
    }

    [Fact]
    public async Task ReadUsesCapturedSessionAndEpochWithoutMainSessionFallback()
    {
        using var fixture = new Fixture();
        fixture.Gateway.Read = (request, epoch, _) =>
        {
            Assert.Equal("agent:research:one", request.SessionKey);
            Assert.Equal("cv_one", request.DocumentId);
            Assert.Equal(12, epoch);
            return Task.FromResult(Document("exact document"));
        };
        await fixture.Controller.LoadAsync();
        Assert.Equal("exact document", fixture.Controller.Snapshot.Document?.Html);
        Assert.Null(fixture.Controller.Snapshot.EmbedSandboxMode);
        Assert.False(ConversationCanvasBoundary.AllowScripts(
            fixture.Controller.Snapshot.EmbedSandboxMode, fixture.Selection.Content));
    }

    [Theory]
    [InlineData("navigation")]
    [InlineData("epoch")]
    [InlineData("panel")]
    [InlineData("close")]
    [InlineData("dispose")]
    [InlineData("account")]
    [InlineData("account-revision")]
    public async Task StaleReadCompletionCannotPaintAfterOwnerChanges(string change)
    {
        using var fixture = new Fixture();
        var result = new TaskCompletionSource<CanvasDocumentView>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Gateway.Read = (_, _, _) => result.Task;
        var read = fixture.Controller.LoadAsync();
        Assert.Equal(ChatConversationCanvasPhase.Loading, fixture.Controller.Snapshot.Phase);
        switch (change)
        {
            case "navigation": fixture.Panels.ExpectSelection("agent:research:two"); break;
            case "epoch": fixture.Gateway.Epoch++; fixture.Panels.Refresh(); break;
            case "panel": fixture.Panels.Open("files"); break;
            case "close": fixture.Panels.Close("canvas"); break;
            case "dispose": fixture.Controller.Dispose(); break;
            case "account": fixture.ProfileId = "profile-two"; fixture.Panels.Refresh(); break;
            case "account-revision": fixture.AccountRevision++; fixture.Panels.Refresh(); break;
        }
        result.SetResult(Document("stale bytes"));
        await read;
        Assert.Null(fixture.Controller.Snapshot.Document);
        Assert.Equal(ChatConversationCanvasPhase.Waiting, fixture.Controller.Snapshot.Phase);
    }

    [Fact]
    public async Task NewRetryWinsEvenWhenOldTransportIgnoresCancellation()
    {
        using var fixture = new Fixture();
        var first = new TaskCompletionSource<CanvasDocumentView>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Gateway.Read = (_, _, _) => first.Task;
        var old = fixture.Controller.LoadAsync();
        fixture.Gateway.Read = (_, _, _) => Task.FromResult(Document("new bytes"));
        await fixture.Controller.LoadAsync();
        first.SetResult(Document("old bytes"));
        await old;
        Assert.Equal("new bytes", fixture.Controller.Snapshot.Document?.Html);
    }

    [Fact]
    public async Task UnexpectedTransportCancellationHasVisibleErrorAndCanRetry()
    {
        using var fixture = new Fixture();
        fixture.Gateway.Read = (_, _, _) => Task.FromException<CanvasDocumentView>(new OperationCanceledException("transport"));
        await fixture.Controller.LoadAsync();
        Assert.Equal(ChatConversationCanvasPhase.Error, fixture.Controller.Snapshot.Phase);
        fixture.Gateway.Read = (_, _, _) => Task.FromResult(Document("recovered"));
        await fixture.Ready();
        Assert.Equal("recovered", fixture.Controller.Snapshot.Document?.Html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingAvailabilityOrPersistedReceiptIsNotPermanentUnsupported(bool receipt)
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(waitForRetry: (_, token) =>
        {
            waiting.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        Exception error = receipt
            ? new GatewayRequestException("Waiting for the widget result.", "UNAVAILABLE",
                System.Text.Json.JsonSerializer.SerializeToElement(new { type = "canvas_widget_pending" }))
            : new CanvasDocumentPendingException();
        fixture.Gateway.Read = (_, _, _) => Task.FromException<CanvasDocumentView>(error);
        var read = fixture.Controller.LoadAsync();
        await waiting.Task;
        Assert.Equal(ChatConversationCanvasPhase.Waiting, fixture.Controller.Snapshot.Phase);
        Assert.Null(fixture.Controller.Snapshot.Document);
        fixture.Panels.Close("canvas");
        await read;
    }

    [Fact]
    public async Task PendingDocumentRetriesAndBecomesAvailableWithoutUserAction()
    {
        var delays = new List<TimeSpan>();
        using var fixture = new Fixture(waitForRetry: (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        var calls = 0;
        fixture.Gateway.Read = (_, _, _) => ++calls == 1
            ? Task.FromException<CanvasDocumentView>(new GatewayRequestException("Pending", "UNAVAILABLE",
                System.Text.Json.JsonSerializer.SerializeToElement(new { type = "canvas_widget_pending", retryAfterMs = 2500 })))
            : Task.FromResult(Document("persisted document"));
        await fixture.Controller.LoadAsync();
        Assert.Equal(2, calls);
        Assert.True(Assert.Single(delays) >= TimeSpan.FromMilliseconds(2500));
        Assert.Equal("persisted document", fixture.Controller.Snapshot.Document?.Html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingRetryIsCancelledBySelectionOrDisposal(bool dispose)
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(waitForRetry: (_, token) =>
        {
            waiting.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        fixture.Gateway.Read = (_, _, _) => Task.FromException<CanvasDocumentView>(new CanvasDocumentPendingException());
        var read = fixture.Controller.LoadAsync();
        await waiting.Task;
        if (dispose) fixture.Controller.Dispose();
        else fixture.Panels.ExpectSelection("agent:research:two");
        await read;
        Assert.Equal(1, fixture.Gateway.Reads);
        Assert.Null(fixture.Controller.Snapshot.Document);
    }

    [Fact]
    public async Task AutomaticRetriesHaveAnAttemptAndBackoffCapAndDoNotRetryDefinitiveErrors()
    {
        var delays = new List<TimeSpan>();
        using var fixture = new Fixture(waitForRetry: (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        fixture.Gateway.Read = (_, _, _) => Task.FromException<CanvasDocumentView>(new TimeoutException());
        await fixture.Controller.LoadAsync();
        Assert.Equal(8, fixture.Gateway.Reads);
        Assert.Equal(7, delays.Count);
        Assert.All(delays, delay => Assert.InRange(delay.TotalMilliseconds, 100, 30000));
        Assert.Equal(ChatConversationCanvasPhase.Error, fixture.Controller.Snapshot.Phase);
        delays.Clear();
        fixture.Gateway.Read = (_, _, _) => Task.FromException<CanvasDocumentView>(new GatewayRequestException("Denied", "FORBIDDEN"));
        await fixture.Controller.LoadAsync();
        Assert.Equal(9, fixture.Gateway.Reads);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task ScriptPolicyCannotSurviveTheDocumentConnectionLease()
    {
        using var fixture = new Fixture();
        fixture.Gateway.Read = (_, _, _) => Task.FromResult(Document("scripts") with { EmbedSandboxMode = "scripts" });
        await fixture.Ready();
        Assert.Equal("scripts", fixture.Controller.Snapshot.EmbedSandboxMode);
        fixture.Gateway.Epoch++;
        fixture.Panels.Refresh();
        Assert.Null(fixture.Controller.Snapshot.Document);
        Assert.Null(fixture.Controller.Snapshot.EmbedSandboxMode);
    }

    [Fact]
    public async Task RepeatedBlockedMessagesDoNotRepublishTheSameNotice()
    {
        using var fixture = new Fixture();
        await fixture.Ready();
        var notifications = 0;
        fixture.Controller.Changed += () => notifications++;
        fixture.Controller.InteractionBlocked();
        fixture.Controller.InteractionBlocked();
        Assert.Equal(1, notifications);
        Assert.True(fixture.Controller.Snapshot.InteractionBlocked);
    }

    [Fact]
    public async Task CurrentPolicySignalRetiresDocumentAndOnlyAnExplicitRetryRevalidatesIt()
    {
        using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Gateway.PolicyRevision++;
        fixture.Gateway.PolicyChanged!(fixture.Gateway, new(12, fixture.Gateway.PolicyRevision, "signing-one"));
        Assert.Equal(ChatConversationCanvasPhase.Waiting, fixture.Controller.Snapshot.Phase);
        Assert.Null(fixture.Controller.Snapshot.Document);
        Assert.Null(fixture.Controller.Snapshot.EmbedSandboxMode);
        Assert.Equal(1, fixture.Gateway.Reads);
        await fixture.Ready();
        Assert.Equal(2, fixture.Gateway.Reads);
    }

    [Theory]
    [InlineData("old-epoch")]
    [InlineData("old-principal")]
    [InlineData("old-revision")]
    public async Task StalePolicyDeliveryCannotRetireTheCurrentDocument(string stale)
    {
        using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Gateway.PolicyChanged!(fixture.Gateway, new(
            stale == "old-epoch" ? 11 : 12,
            stale == "old-revision" ? 0 : 1,
            stale == "old-principal" ? "another-signing-device" : "signing-one"));
        Assert.Equal(ChatConversationCanvasPhase.Ready, fixture.Controller.Snapshot.Phase);
        Assert.NotNull(fixture.Controller.Snapshot.Document);
    }

    [Fact]
    public async Task PolicyChangeDuringReadRejectsLateBytesAndDetachesOnRetirement()
    {
        using var fixture = new Fixture();
        var result = new TaskCompletionSource<CanvasDocumentView>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Gateway.Read = (_, _, _) => result.Task;
        var pending = fixture.Controller.LoadAsync();
        fixture.Gateway.PolicyRevision++;
        fixture.Gateway.PolicyChanged!(fixture.Gateway, new(12, fixture.Gateway.PolicyRevision, "signing-one"));
        result.SetResult(Document("old policy bytes"));
        await pending;
        Assert.Null(fixture.Controller.Snapshot.Document);
        Assert.Null(fixture.Gateway.PolicyChanged);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task GenericUnavailableRetriesOnlyAnExplicitEnvelopeGrant(bool? retryable, bool expectedRetry)
    {
        var waits = new List<TimeSpan>();
        using var fixture = new Fixture(waitForRetry: (delay, _) => { waits.Add(delay); return Task.CompletedTask; });
        var calls = 0;
        fixture.Gateway.Read = (_, _, _) => ++calls == 1
            ? Task.FromException<CanvasDocumentView>(new GatewayRequestException(
                "Unavailable", "UNAVAILABLE", retryable: retryable, retryAfterMs: 2500))
            : Task.FromResult(Document("recovered"));
        await fixture.Controller.LoadAsync();
        Assert.Equal(expectedRetry ? 2 : 1, calls);
        Assert.Equal(expectedRetry ? ChatConversationCanvasPhase.Loaded : ChatConversationCanvasPhase.Error,
            fixture.Controller.Snapshot.Phase);
        if (expectedRetry) Assert.True(Assert.Single(waits) >= TimeSpan.FromMilliseconds(2500));
        else Assert.Empty(waits);
    }

    [Fact]
    public async Task SuspendedSurfaceRetainsDocumentButCannotAcceptPromptsUntilRenderedAgain()
    {
        using var fixture = new Fixture();
        await fixture.Ready();
        var operation = fixture.Controller.Snapshot.Operation!;
        fixture.Controller.SurfaceSuspended(operation);
        Assert.Equal(ChatConversationCanvasPhase.Loaded, fixture.Controller.Snapshot.Phase);
        Assert.NotNull(fixture.Controller.Snapshot.Document);
        Assert.Equal(ChatConversationCanvasPromptResult.Rejected,
            await fixture.Controller.SubmitPromptAsync("hello", () => true, DateTimeOffset.UtcNow));
        fixture.Controller.SurfaceReady(operation);
        Assert.Equal(ChatConversationCanvasPhase.Ready, fixture.Controller.Snapshot.Phase);
    }

    [Fact]
    public async Task WrongSessionOwnerCannotDispatchEvenOnSameClient()
    {
        using var fixture = new Fixture();
        using var wrong = new ChatConversationCanvasController(fixture.Panels,
            fixture.Selection with { Owner = fixture.Selection.Owner with { SessionId = "other-session" } },
            (_, _) => Task.FromResult(ChatConversationCanvasPromptResult.Accepted));
        await wrong.LoadAsync();
        Assert.Equal(0, fixture.Gateway.Reads);
        Assert.Equal(ChatConversationCanvasPhase.Waiting, wrong.Snapshot.Phase);
    }

    [Fact]
    public async Task OldDocumentCannotReacquireTheSamePanelAndRunAfterAnotherDocumentOpens()
    {
        using var fixture = new Fixture();
        fixture.Panels.Open("canvas", "cv_other", "run-one");
        await fixture.Controller.LoadAsync();
        Assert.Equal(0, fixture.Gateway.Reads);
        Assert.Equal(ChatConversationCanvasPhase.Waiting, fixture.Controller.Snapshot.Phase);
    }

    [Fact]
    public async Task PluginDescriptorNeverUsesCanvasDocumentRpc()
    {
        using var fixture = new Fixture();
        using var plugin = new ChatConversationCanvasController(fixture.Panels,
            fixture.Selection with { Content = fixture.Selection.Content with { Kind = ConversationCanvasContentKind.McpApp } },
            (_, _) => Task.FromResult(ChatConversationCanvasPromptResult.Accepted));
        await plugin.LoadAsync();
        Assert.Equal(ChatConversationCanvasPhase.Unsupported, plugin.Snapshot.Phase);
        Assert.Equal(0, fixture.Gateway.Reads);
    }

    [Theory]
    [InlineData(false, "ordinary text")]
    [InlineData(true, "/reset")]
    [InlineData(true, "!command")]
    public async Task PromptRequiresFocusAndConversationalText(bool focused, string text)
    {
        var submitted = 0;
        using var fixture = new Fixture((_, _) =>
        {
            submitted++;
            return Task.FromResult(ChatConversationCanvasPromptResult.Accepted);
        });
        await fixture.Ready();
        Assert.Equal(ChatConversationCanvasPromptResult.Rejected,
            await fixture.Controller.SubmitPromptAsync(text, () => focused, DateTimeOffset.UtcNow));
        Assert.Equal(0, submitted);
    }

    [Fact]
    public async Task PromptIsBoundedAndWaitsForActualParentAcceptance()
    {
        var started = new TaskCompletionSource<ChatConversationCanvasPrompt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new TaskCompletionSource<ChatConversationCanvasPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture((prompt, _) => { started.SetResult(prompt); return accepted.Task; });
        await fixture.Ready();
        var pending = fixture.Controller.SubmitPromptAsync("explain this", () => true, DateTimeOffset.UtcNow);
        var captured = await started.Task;
        Assert.Equal(fixture.Selection, captured.Selection);
        Assert.Equal("explain this", captured.Text);
        Assert.False(pending.IsCompleted);
        Assert.Equal(ChatConversationCanvasPromptResult.Rejected,
            await fixture.Controller.SubmitPromptAsync("second", () => true, DateTimeOffset.UtcNow));
        fixture.Panels.Bind("agent:research:two");
        accepted.SetResult(ChatConversationCanvasPromptResult.Accepted);
        var retired = await pending;
        Assert.Equal(ChatConversationContentAdmissionKind.Unconfirmed, retired.Admission.Kind);
        Assert.True(retired.Admission.OwnsPayload);
    }

    [Fact]
    public async Task PromptRateLimitIsTenPerMinuteForOneCapturedDocumentLifetime()
    {
        using var fixture = new Fixture();
        await fixture.Ready();
        var now = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
        for (var count = 0; count < 10; count++)
            Assert.Equal(ChatConversationCanvasPromptResult.Accepted,
                await fixture.Controller.SubmitPromptAsync("hello", () => true, now));
        Assert.Equal(ChatConversationCanvasPromptResult.Rejected,
            await fixture.Controller.SubmitPromptAsync("eleventh", () => true, now.AddSeconds(59)));
        Assert.Equal(ChatConversationCanvasPromptResult.Accepted,
            await fixture.Controller.SubmitPromptAsync("next minute", () => true, now.AddMinutes(1)));
    }

    [Theory]
    [InlineData("Accepted", true)]
    [InlineData("Queued", true)]
    [InlineData("Unconfirmed", false)]
    [InlineData("Rejected", false)]
    [InlineData("Stale", false)]
    public async Task PromptRetainsActualParentCustodyAndErrorWithoutBooleanCollapse(string kind, bool custody)
    {
        var receipt = new ChatConversationCanvasPromptResult(
            new(Enum.Parse<ChatConversationContentAdmissionKind>(kind), "parent detail", custody));
        using var fixture = new Fixture((_, _) => Task.FromResult(receipt));
        await fixture.Ready();
        var result = await fixture.Controller.SubmitPromptAsync("explain", () => true, DateTimeOffset.UtcNow);
        Assert.Equal(receipt, result);
        Assert.Equal(receipt, fixture.Controller.Snapshot.PromptAdmission);
        Assert.Equal("parent detail", result.Admission.Error);
        Assert.Equal(custody, result.Admission.HasCustody);
    }

    [Fact]
    public async Task ReloadReplacesThePolicySubscriptionAndDisposalDetachesIt()
    {
        using var fixture = new Fixture();
        for (var reload = 0; reload < 20; reload++)
        {
            await fixture.Controller.LoadAsync();
            Assert.Single(fixture.Gateway.PolicyChanged!.GetInvocationList());
        }
        fixture.Controller.Dispose();
        Assert.Null(fixture.Gateway.PolicyChanged);
        Assert.Equal(20, fixture.Gateway.Reads);
    }

    [Fact]
    public async Task RetiredOriginalReadSourceClearsTheSurfaceWithoutFailureOrAutomaticRetry()
    {
        using var fixture = new Fixture();
        await fixture.Ready();
        fixture.Gateway.Read = (_, _, _) => Task.FromException<CanvasDocumentView>(new GatewayDataReadRetiredException());
        await fixture.Controller.LoadAsync();
        Assert.Equal(2, fixture.Gateway.Reads);
        Assert.Equal(ChatConversationCanvasPhase.Waiting, fixture.Controller.Snapshot.Phase);
        Assert.Null(fixture.Controller.Snapshot.Document);
        Assert.Null(fixture.Gateway.PolicyChanged);
    }

    [Fact]
    public void ProfilePathsAreEphemeralUnderSuppliedRootAndContainNoConversationIdentity()
    {
        using var root = new TempDirectory();
        var first = new ConversationCanvasProfile(root.Path);
        var second = new ConversationCanvasProfile(root.Path);
        Assert.NotEqual(first.DirectoryPath, second.DirectoryPath);
        Assert.StartsWith(root.Path + Path.DirectorySeparatorChar + "ConversationCanvas" + Path.DirectorySeparatorChar,
            first.DirectoryPath, StringComparison.OrdinalIgnoreCase);
        first.Create();
        File.WriteAllText(Path.Combine(first.DirectoryPath, "owned-fixture.txt"), "fixture");
        first.DeleteAfterBrowserExit();
        Assert.False(Directory.Exists(first.DirectoryPath));
        Assert.True(Directory.Exists(root.Path));
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", FileAttributes.Directory, true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", FileAttributes.Directory | FileAttributes.ReparsePoint, false)]
    [InlineData("another-session", FileAttributes.Directory, false)]
    public void OrphanSweepRefusesUnknownFoldersAndRedirectedProfiles(string name, FileAttributes attributes, bool eligible) =>
        Assert.Equal(eligible, ConversationCanvasProfile.IsSweepCandidate(name, attributes));

    [Fact]
    public void OrphanSweepPreservesLiveAndUnknownProfilesAndDeletesOnlyRecordedDeadOwners()
    {
        using var root = new TempDirectory();
        var active = new ConversationCanvasProfile(root.Path);
        active.Create();
        active.RecordBrowser(Environment.ProcessId);
        var profiles = Path.GetDirectoryName(active.DirectoryPath)!;
        var stale = Path.Combine(profiles, new string('a', 32));
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "companion-owner.json"), """
            {"Format":"openclaw-conversation-canvas-profile/v1","ProcessId":2147483647,
             "StartTimeUtcTicks":1,"BrowserProcessId":2147483646,"BrowserStartTimeUtcTicks":1}
            """);
        var unknown = Path.Combine(profiles, new string('b', 32));
        Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "unknown.txt"), "leave intact");
        ConversationCanvasProfile.SweepStaleProfiles(profiles);
        Assert.True(Directory.Exists(active.DirectoryPath));
        Assert.True(Directory.Exists(unknown));
        Assert.False(Directory.Exists(stale));
        active.DeleteAfterBrowserExit();
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("browser-unrecorded")]
    public void OrphanSweepPreservesUnprovenOwnership(string kind)
    {
        using var root = new TempDirectory();
        var folder = root.Combine(new string('c', 32));
        Directory.CreateDirectory(folder);
        var marker = kind switch
        {
            "malformed" => "{not valid JSON",
            "oversized" => new string(' ', 2049),
            _ => """
                {"Format":"openclaw-conversation-canvas-profile/v1","ProcessId":2147483647,
                 "StartTimeUtcTicks":1,"BrowserProcessId":0,"BrowserStartTimeUtcTicks":0}
                """
        };
        File.WriteAllText(Path.Combine(folder, "companion-owner.json"), marker);
        ConversationCanvasProfile.SweepStaleProfiles(root.Path);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void ReusedProcessIdDoesNotPreserveAnOldOwnedProfile()
    {
        using var root = new TempDirectory();
        var folder = root.Combine(new string('d', 32));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "companion-owner.json"), $$"""
            {"Format":"openclaw-conversation-canvas-profile/v1","ProcessId":{{Environment.ProcessId}},
             "StartTimeUtcTicks":1,"BrowserProcessId":{{Environment.ProcessId}},"BrowserStartTimeUtcTicks":1}
            """);
        ConversationCanvasProfile.SweepStaleProfiles(root.Path);
        Assert.False(Directory.Exists(folder));
    }
}
