using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Presentation;
using Xunit.Abstractions;

namespace OpenClaw.Tray.Tests;

public sealed class ChatConversationMcpAppSessionTests(ITestOutputHelper output)
{
    private static readonly TimeSpan DeadlockDeadline = TimeSpan.FromSeconds(10);
    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private sealed class Dispatcher : IUiDispatcher
    {
        public bool HasThreadAccess { get; set; } = true;
        internal readonly System.Collections.Concurrent.ConcurrentQueue<Action> Queue = new();
        public bool TryEnqueue(Action action) { Queue.Enqueue(action); return true; }
        internal void Drain()
        {
            HasThreadAccess = true;
            while (Queue.TryDequeue(out var action)) action();
        }
    }
    public class Gateway : DispatchProxy
    {
        internal long Epoch = 7;
        internal string[] Scopes = ["operator.write"];
        internal int ViewReads;
        internal readonly List<(ConversationMcpAppScope Scope, ConversationMcpAppOperation Operation, JsonElement Parameters, long Epoch)> Calls = [];
        internal Action<ConversationMcpAppScope, ConversationMcpAppOperation, long>? ObserveCall;
        internal Func<ConversationMcpAppOperation, JsonElement, Task<JsonElement>> InvokeOperation = (operation, _) =>
            Task.FromResult(Json(operation switch
            {
                ConversationMcpAppOperation.CallTool => """{"content":[]}""",
                ConversationMcpAppOperation.ListTools => """{"tools":[]}""",
                ConversationMcpAppOperation.ListResources => """{"resources":[]}""",
                ConversationMcpAppOperation.ListResourceTemplates => """{"resourceTemplates":[]}""",
                ConversationMcpAppOperation.ReadResource => """{"contents":[]}""",
                _ => "{}"
            }));
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_SessionMutationConnectionEpoch" => (long?)Epoch,
            "get_IsConnectedToGateway" => true,
            "get_AuthenticatedSigningDeviceId" => "signing-device",
            "get_GrantedOperatorScopes" => Scopes,
            "InvokeConversationMcpAppAsync" => InvokeOperationAsync(args!),
            "ViewConversationMcpAppAsync" => ReadView(),
            _ => throw new NotSupportedException(method?.Name)
        };
        private Task<ConversationMcpAppView> ReadView()
        {
            ViewReads++;
            return Task.FromResult(new ConversationMcpAppView
            {
                Html = "<main>owned App</main>", GatewayOrigin = new("https://gateway.example"),
                SandboxUrl = new("https://sandbox.example/mcp-app-sandbox")
            });
        }
        private Task<JsonElement> InvokeOperationAsync(object?[] args)
        {
            var scope = (ConversationMcpAppScope)args[0]!;
            var operation = (ConversationMcpAppOperation)args[1]!;
            var parameters = (JsonElement)args[2]!;
            Calls.Add((scope, operation, parameters, (long)args[3]!));
            ObserveCall?.Invoke(scope, operation, (long)args[3]!);
            return InvokeOperation(operation, parameters);
        }
    }

    private sealed class Actions : IChatConversationMcpAppActions
    {
        internal readonly List<ChatConversationMcpAppConfirmation> Confirmations = [];
        internal readonly List<ConversationMcpAppMessage> Messages = [];
        internal readonly List<ChatConversationMcpAppFile> Files = [];
        internal readonly List<ConversationMcpAppLink> Links = [];
        internal Func<Task<bool>> Confirm = () => Task.FromResult(true);
        internal ChatConversationContentAdmission Admission = ChatConversationContentAdmission.Accepted;
        internal Exception? AdmissionFailure;
        internal bool Accept { get => Admission.HasCustody; set => Admission = value
            ? ChatConversationContentAdmission.Accepted : ChatConversationContentAdmission.Rejected; }
        public Task<bool> ConfirmAsync(ChatConversationMcpAppConfirmation request, CancellationToken cancellationToken)
        { Confirmations.Add(request); return Confirm(); }
        public Task<ChatConversationContentAdmission> AcceptMessageAsync(ChatConversationMcpAppAction owner, ConversationMcpAppMessage message, CancellationToken cancellationToken)
        { Messages.Add(message); return AdmissionResult(); }
        public Task<ChatConversationContentAdmission> AcceptFileAsync(ChatConversationMcpAppAction owner, ChatConversationMcpAppFile file, CancellationToken cancellationToken)
        { Files.Add(file); return AdmissionResult(); }
        public Task<ChatConversationContentAdmission> OpenLinkAsync(ChatConversationMcpAppAction owner, ConversationMcpAppLink link, CancellationToken cancellationToken)
        { Links.Add(link); return AdmissionResult(); }
        private Task<ChatConversationContentAdmission> AdmissionResult() => AdmissionFailure is { } failure
            ? Task.FromException<ChatConversationContentAdmission>(failure) : Task.FromResult(Admission);
        public bool SetDisplayMode(ChatConversationMcpAppAction owner, string mode) => Accept;
        public void ReturnToConversation(ChatConversationMcpAppAction owner) { }
    }

    private sealed class Events : IChatConversationMcpAppEvents
    {
        public bool IsAvailable => true;
        internal Action<ChatConversationMcpAppEvent>? Listener;
        internal bool Disposed;
        public IDisposable Subscribe(ChatConversationMcpAppAction owner, string viewId, Action<ChatConversationMcpAppEvent> receive)
        { Listener = receive; return new Subscription(this); }
        private sealed class Subscription(Events events) : IDisposable
        { public void Dispose() { events.Disposed = true; events.Listener = null; } }
    }

    private sealed class RootEvents : IChatConversationMcpAppEventSource
    {
        public bool IsAvailable { get; set; }
        internal Action<ChatConversationMcpAppEvent>? Listener;
        internal ChatConversationMcpAppAction? Captured;
        internal bool Disposed;
        public IDisposable Subscribe(ChatConversationMcpAppAction capturedOwner, Action<ChatConversationMcpAppEvent> receive)
        {
            Captured = capturedOwner;
            Listener = receive;
            return new Subscription(this);
        }
        private sealed class Subscription(RootEvents owner) : IDisposable
        {
            public void Dispose() { owner.Disposed = true; owner.Listener = null; }
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Gateway Gateway;
        internal readonly ChatConversationPanelState Panels;
        internal readonly ChatConversationCanvasSelection Selection;
        internal readonly ChatConversationPanelOperation Operation;
        internal readonly Actions Actions = new();
        internal readonly Events Events = new();
        internal readonly List<JsonElement> Sent = [];
        internal readonly List<ChatConversationMcpAppNotice> Notices = [];
        internal readonly ChatConversationMcpAppSession Session;
        internal readonly ChatConversationMcpAppLifetime Lifetime;
        internal readonly ChatConversationMcpAppMountLease Mount;
        internal readonly GatewayDataReadScope ReadScope;
        internal readonly GatewayDataReadSource Source;
        internal bool Focused = true;
        internal bool VisibleForeground = true;
        internal long AccountRevision = 1;
        internal bool ThrowSourceCheck;
        internal int Initialized;
        internal string Mode = "";
        internal double Height;
        internal readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-22T12:00:00Z");
        internal Fixture(bool enabled = true, Func<TimeSpan, Task>? waitForTeardown = null)
        {
            var client = DispatchProxy.Create<IOperatorGatewayClient, Gateway>();
            Gateway = (Gateway)client;
            Panels = new(key => ChatConversationContext.Capture("gateway-record", client,
                new SessionInfo { Key = key, SessionId = "session-id", AgentId = "agent" }, "profile-one", AccountRevision));
            Panels.Bind("agent:agent:session");
            Panels.Open("canvas", "view-one", "run-one");
            Selection = new(Panels.Context!.Identity,
                new(ConversationCanvasContentKind.McpApp, "view-one", null, "App", true,
                    McpApp: new("view-one", "server", "tool", null, "call-one", null, null)), "message-id", "run-one");
            Operation = Panels.BeginOperation("canvas", "run-one", expectedContentId: "view-one")!;
            ReadScope = new(client, () => Gateway.Epoch);
            Source = ReadScope.Capture()!;
            Lifetime = new(Operation.Context, Source, (context, _) => ThrowSourceCheck
                ? throw new InvalidDataException("The original source is unavailable.")
                : context.AccountRevision == AccountRevision);
            Mount = Lifetime.AcquireAsync(new(Selection, Operation), CancellationToken.None).GetAwaiter().GetResult()!;
            Session = new(new(Selection, Operation), new()
            {
                Html = "<main>App</main>", GatewayOrigin = new("https://gateway.example"),
                SandboxUrl = new("https://sandbox.example/mcp-app-sandbox"),
                MessageSupported = enabled, OpenFilesSupported = enabled, FileResourcesSupported = enabled,
                UpdateModelContextSupported = enabled, RichModelContextSupported = enabled
            }, Actions, Events, () => Focused, packet => Sent.Add(packet), Notices.Add,
                (mode, height) => { Mode = mode; Height = height; }, () => Initialized++, Mount,
                waitForTeardown ?? (_ => Task.CompletedTask), () => VisibleForeground);
        }
        internal Task Request(string method, string parameters = "{}", string id = "1") =>
            Session.ReceiveAsync(Json($$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}"""), Now);
        internal async Task Ready()
        {
            await Request("ui/initialize", """{"protocolVersion":"2026-01-26","appInfo":{"name":"App","version":"1"},"appCapabilities":{"availableDisplayModes":["inline","fullscreen"]}}""");
            await Session.ReceiveAsync(Json("""{"jsonrpc":"2.0","method":"ui/notifications/initialized"}"""), Now);
            Sent.Clear();
            Gateway.Calls.Clear();
        }
        internal JsonElement Reply => Sent.Last(p => p.TryGetProperty("id", out _));
        public void Dispose() { Session.Dispose(); Panels.Dispose(); }
    }

    private sealed record CallTrace(long Sequence, ConversationMcpAppOperation Operation,
        string ViewId, long Epoch, long Generation);

    private static System.Collections.Concurrent.ConcurrentQueue<CallTrace> TraceCalls(Fixture fixture)
    {
        var trace = new System.Collections.Concurrent.ConcurrentQueue<CallTrace>();
        long sequence = 0;
        fixture.Gateway.ObserveCall = (scope, operation, epoch) =>
            trace.Enqueue(new(Interlocked.Increment(ref sequence), operation,
                scope.ViewId, epoch, fixture.Operation.Generation));
        return trace;
    }

    private void WriteCallTrace(string stage, IEnumerable<CallTrace> trace)
    {
        output.WriteLine(stage);
        foreach (var call in trace.OrderBy(call => call.Sequence))
            output.WriteLine($"seq={call.Sequence}; leaseGeneration={call.Generation}; view={call.ViewId}; epoch={call.Epoch}; operation={call.Operation}");
    }

    [Fact]
    public async Task ProductionControllerReadsOnlyTheCanonicalSelectedContentWhenHooksExist()
    {
        using var f = new Fixture();
        // The fixture's session owns a different mount. Retire it before exercising production admission.
        await f.Session.RetireAsync();
        using var controller = new ChatConversationMcpAppController(f.Panels, f.Selection, f.Actions, f.Events, f.Lifetime);
        await controller.LoadAsync();
        Assert.Equal(ChatConversationMcpAppPhase.Loaded, controller.Snapshot.Phase);
        Assert.Equal("view-one", controller.Snapshot.Operation?.ContentId);
        Assert.Equal("profile-one", controller.Snapshot.Operation?.Context.Identity.UserId);
        Assert.Equal(1, controller.Snapshot.Operation?.Context.AccountRevision);
        Assert.Equal(1, f.Gateway.ViewReads);
        Assert.NotNull(controller.Snapshot.View);
        Assert.Empty(f.Gateway.Calls);
    }

    [Fact]
    public async Task ProductionControllerCannotReacquireAnotherViewOnTheSamePanelAndRun()
    {
        using var f = new Fixture();
        f.Panels.Open("canvas", "view-other", "run-one");
        using var controller = new ChatConversationMcpAppController(f.Panels, f.Selection, f.Actions, f.Events);
        await controller.LoadAsync();
        Assert.Equal(ChatConversationMcpAppPhase.Unavailable, controller.Snapshot.Phase);
        Assert.Null(controller.Snapshot.Operation);
        Assert.Equal(0, f.Gateway.ViewReads);
    }

    [Fact]
    public async Task ProductionControllerCannotReadWithoutRealRootActionsAndEventHooks()
    {
        using var f = new Fixture();
        using var controller = new ChatConversationMcpAppController(f.Panels, f.Selection, null, null);
        await controller.LoadAsync();
        Assert.Equal(ChatConversationMcpAppPhase.Unavailable, controller.Snapshot.Phase);
        Assert.Equal(0, f.Gateway.ViewReads);
    }

    [Fact]
    public async Task InitializePrecedesToolNotificationsAndRejectsRepeatedInitialize()
    {
        using var f = new Fixture();
        await f.Request("tools/list");
        Assert.Equal(-32000, f.Reply.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Empty(f.Gateway.Calls);
        await f.Request("ui/initialize", """{"protocolVersion":"future","appInfo":{"name":"App","version":"1"},"appCapabilities":{}}""");
        Assert.Equal("2026-01-26", f.Reply.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.DoesNotContain(f.Sent, p => p.TryGetProperty("method", out var method) && method.GetString() == "ui/notifications/tool-input");
        await f.Session.ReceiveAsync(Json("""{"jsonrpc":"2.0","method":"ui/notifications/initialized"}"""), f.Now);
        Assert.Equal(1, f.Initialized);
        Assert.Contains(f.Sent, p => p.TryGetProperty("method", out var method) && method.GetString() == "ui/notifications/tool-input");
        await f.Request("ui/initialize", """{"protocolVersion":"future","appInfo":{"name":"App","version":"1"},"appCapabilities":{}}""");
        Assert.Equal(-32600, f.Reply.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task TypedOperationUsesCapturedOwnerAndEpochNotAppRoutingArguments()
    {
        using var f = new Fixture();
        await f.Ready();
        await f.Request("tools/call", """{"name":"tool","sessionKey":"spoof","viewId":"spoof","arguments":{"sessionKey":"literal"}}""");
        var call = Assert.Single(f.Gateway.Calls);
        Assert.Equal(new("agent:agent:session", "view-one", "agent"), call.Scope);
        Assert.Equal(7, call.Epoch);
        Assert.Equal(ConversationMcpAppOperation.CallTool, call.Operation);
        Assert.Equal("literal", call.Parameters.GetProperty("arguments").GetProperty("sessionKey").GetString());
    }

    [Theory]
    [InlineData("navigation")]
    [InlineData("epoch")]
    [InlineData("hide")]
    [InlineData("close")]
    [InlineData("replace")]
    [InlineData("dispose")]
    public async Task PendingConfirmationCannotSendAfterOwnerRevocation(string transition)
    {
        using var f = new Fixture();
        await f.Ready();
        var pending = new TaskCompletionSource<bool>();
        f.Actions.Confirm = () => pending.Task;
        var send = f.Request("ui/message", """{"role":"user","content":[{"type":"text","text":"Explain"}]}""");
        Assert.Single(f.Actions.Confirmations);
        switch (transition)
        {
            case "navigation": f.Panels.ExpectSelection("elsewhere"); break;
            case "epoch": f.Gateway.Epoch++; f.Panels.Refresh(); break;
            case "hide": f.Panels.Suspend(); break;
            case "close": f.Panels.Close("canvas"); break;
            case "replace": f.Panels.Open("canvas", "view-two", "run-one"); break;
            case "dispose": f.Session.Dispose(); break;
        }
        pending.SetResult(true);
        await send;
        Assert.Empty(f.Actions.Messages);
    }

    [Fact]
    public async Task NativeRejectionAndActualRootRejectionAreNotReportedAsAccepted()
    {
        using var f = new Fixture();
        await f.Ready();
        f.Actions.Confirm = () => Task.FromResult(false);
        await f.Request("ui/message", """{"role":"user","content":[{"type":"text","text":"Explain"}]}""");
        Assert.True(f.Reply.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Empty(f.Actions.Messages);
        f.Actions.Confirm = () => Task.FromResult(true);
        f.Actions.Accept = false;
        await f.Request("ui/message", """{"role":"user","content":[{"type":"text","text":"Explain"}]}""");
        Assert.True(f.Reply.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Single(f.Actions.Messages);
        Assert.Empty(f.Gateway.Calls); // ui/message never maps directly to chat.send.
    }

    [Fact]
    public async Task RichMessagePreservesAttachmentAndNewConversationTarget()
    {
        using var f = new Fixture();
        await f.Ready();
        await f.Request("ui/message", """{"role":"user","content":[{"type":"text","text":"/not-a-command","_meta":{"openai/title":"source.txt"}},{"type":"text","text":"Explain"}],"_meta":{"openai/message":{"target":"new","send":true}}}""");
        var message = Assert.Single(f.Actions.Messages);
        Assert.Equal(ConversationMcpAppMessageTarget.New, message.Target);
        Assert.Equal("Explain", message.PlainText);
        Assert.Equal(2, message.Content.GetArrayLength());
        Assert.False(f.Reply.GetProperty("result").TryGetProperty("isError", out _));
    }

    [Theory]
    [InlineData("focus")]
    [InlineData("scope")]
    public async Task ConfirmationRechecksFocusAndWriteAuthority(string revoke)
    {
        using var f = new Fixture();
        await f.Ready();
        f.Actions.Confirm = () =>
        {
            if (revoke == "focus") f.VisibleForeground = false;
            else f.Gateway.Scopes = ["operator.read"];
            return Task.FromResult(true);
        };
        await f.Request("ui/message", """{"role":"user","content":[{"type":"text","text":"Explain"}]}""");
        Assert.Empty(f.Actions.Messages);
    }

    [Fact]
    public async Task BusyAndRateLimitedEffectsRemainVisibleAndNeverRetry()
    {
        using var f = new Fixture();
        await f.Ready();
        var pending = new TaskCompletionSource<bool>();
        f.Actions.Confirm = () => pending.Task;
        const string text = """{"role":"user","content":[{"type":"text","text":"Explain"}]}""";
        var first = f.Request("ui/message", text);
        await f.Request("ui/message", text, "2");
        Assert.Contains(ChatConversationMcpAppNotice.Busy, f.Notices);
        pending.SetResult(true);
        await first;
        f.Actions.Confirm = () => Task.FromResult(true);
        for (var i = 0; i < 10; i++) await f.Request("ui/message", text);
        Assert.Equal(10, f.Actions.Messages.Count);
        Assert.Contains(ChatConversationMcpAppNotice.RateLimited, f.Notices);
    }

    [Theory]
    [InlineData("file:///C:/secret")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:password@example.test")]
    [InlineData("openclaw://settings")]
    public async Task ExternalLinksCannotUseNativeLocalOrCredentialSchemes(string url)
    {
        using var f = new Fixture();
        await f.Ready();
        await f.Request("ui/open-link", JsonSerializer.Serialize(new { url }));
        Assert.Empty(f.Actions.Links);
        Assert.Empty(f.Actions.Confirmations);
    }

    [Fact]
    public async Task GatewayFileMustBeConfirmedAndActuallyAcceptedByFilesOwner()
    {
        using var f = new Fixture();
        await f.Ready();
        f.Gateway.InvokeOperation = (_, _) => Task.FromResult(Json("""{"path":"workspace/report.md","name":"Report"}"""));
        f.Actions.Accept = false;
        await f.Request("openai/files/open", """{"path":"workspace/report.md"}""");
        Assert.Equal(ChatConversationMcpAppActionKind.File, Assert.Single(f.Actions.Confirmations).Kind);
        Assert.Equal("workspace/report.md", Assert.Single(f.Actions.Files).Path);
        Assert.True(f.Reply.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task LostFocusDuringFileResolutionCannotInvokeFilesOwner()
    {
        using var f = new Fixture();
        await f.Ready();
        f.Gateway.InvokeOperation = (_, _) =>
        {
            f.VisibleForeground = false;
            return Task.FromResult(Json("""{"path":"workspace/report.md"}"""));
        };
        await f.Request("openai/files/open", """{"path":"workspace/report.md"}""");
        Assert.Empty(f.Actions.Files);
    }

    [Fact]
    public async Task TimeoutAfterAdmittedWriteIsUnknownAndNeverRetried()
    {
        using var f = new Fixture();
        await f.Ready();
        f.Gateway.InvokeOperation = (_, _) => Task.FromException<JsonElement>(new TimeoutException());
        await f.Request("tools/call", """{"name":"mutate"}""");
        Assert.Single(f.Gateway.Calls);
        Assert.Contains(ChatConversationMcpAppNotice.Unknown, f.Notices);
        Assert.Contains("unknown", f.Reply.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task DuplicateSemanticIdsAndUnknownResponsesCannotResolveOrDispatchTwice()
    {
        using var f = new Fixture();
        await f.Ready();
        var result = new TaskCompletionSource<JsonElement>();
        f.Gateway.InvokeOperation = (_, _) => result.Task;
        var first = f.Request("tools/list", id: "\"a\"");
        await f.Request("tools/list", id: "\"\\u0061\"");
        await f.Session.ReceiveAsync(Json("""{"jsonrpc":"2.0","id":"not-requested","result":{}}"""), f.Now);
        Assert.Single(f.Gateway.Calls);
        Assert.Empty(f.Sent);
        result.SetResult(Json("""{"tools":[]}"""));
        await first;
        Assert.Equal("a", f.Reply.GetProperty("id").GetString());
    }

    [Fact]
    public async Task CurrentFlagsAndActualScopesAreRequiredForEveryDispatch()
    {
        using var f = new Fixture(enabled: false);
        await f.Ready();
        await f.Request("resources/read", """{"uri":"gateway:one"}""");
        Assert.Empty(f.Gateway.Calls);
        f.Gateway.Scopes = [];
        await f.Request("tools/list");
        Assert.Empty(f.Gateway.Calls);
        Assert.Equal(-32001, f.Reply.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ExactEventDeliveryAndOwnedSubscriptionAreRequiredAndRetirementUnsubscribes()
    {
        using var f = new Fixture();
        await f.Ready();
        var trace = TraceCalls(f);
        await f.Request("resources/subscribe", """{"uri":"gateway:report"}""");
        f.Sent.Clear();
        var delivery = new ChatConversationMcpAppEvent(f.Selection.Owner, 7, "run-one", "view-one",
            ChatConversationMcpAppEventKind.ResourceUpdated, Json("""{"uri":"gateway:report"}"""), 1, f.Source);
        f.Events.Listener!(delivery with { ConnectionEpoch = 8 });
        f.Events.Listener!(delivery with { ViewId = "elsewhere" });
        f.Events.Listener!(delivery with { RunId = "run-two" });
        f.Events.Listener!(delivery with { AccountRevision = 2 });
        f.Events.Listener!(delivery with { Owner = f.Selection.Owner with { GatewayId = "another" } });
        Assert.Empty(f.Sent);
        f.Events.Listener!(delivery);
        Assert.Equal("notifications/resources/updated", Assert.Single(f.Sent).GetProperty("method").GetString());
        var retiredListener = f.Events.Listener!;
        var unsubscribeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unsubscribeResult = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Gateway.InvokeOperation = (operation, _) =>
        {
            if (operation != ConversationMcpAppOperation.UnsubscribeResource) return Task.FromResult(Json("{}"));
            unsubscribeStarted.TrySetResult();
            return unsubscribeResult.Task;
        };
        f.Session.Dispose();
        WriteCallTrace("Immediately after Dispose, before awaiting cleanup", trace);
        Assert.True(f.Events.Disposed);
        try
        {
            await unsubscribeStarted.Task.WaitAsync(DeadlockDeadline);
            Assert.False(f.Mount.RetireAsync().IsCompleted);
            retiredListener(delivery);
            Assert.Single(f.Sent, packet => packet.TryGetProperty("method", out var method) &&
                method.GetString() == "notifications/resources/updated");
            await f.Request("resources/subscribe", """{"uri":"gateway:late"}""", "2");
            Assert.Single(trace, call => call.Operation == ConversationMcpAppOperation.SubscribeResource);
        }
        finally
        {
            WriteCallTrace("Ordered calls while cleanup was pending", trace);
            unsubscribeResult.TrySetResult(Json("{}"));
        }
        await f.Mount.RetireAsync().WaitAsync(DeadlockDeadline);
        WriteCallTrace("After actual cleanup", trace);
        Assert.Equal(ConversationMcpAppOperation.UnsubscribeResource, f.Gateway.Calls.Last().Operation);
        await f.Request("tools/call", """{"name":"no-new-effects"}""");
        WriteCallTrace("After the retired-session request", trace);
        Assert.Equal(2, f.Gateway.Calls.Count);
        var ordered = trace.OrderBy(call => call.Sequence).ToArray();
        var firstUnsubscribe = Array.FindIndex(ordered,
            call => call.Operation == ConversationMcpAppOperation.UnsubscribeResource);
        Assert.True(firstUnsubscribe >= 0);
        Assert.DoesNotContain(ordered.Skip(firstUnsubscribe + 1),
            call => call.Operation == ConversationMcpAppOperation.SubscribeResource);
    }

    [Fact]
    public async Task LateSubscribeCompletionCannotReattachOrDeliverToRetiredLease()
    {
        using var f = new Fixture();
        await f.Ready();
        var trace = TraceCalls(f);
        var subscribeResult = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unsubscribeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unsubscribeResult = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Gateway.InvokeOperation = (operation, _) =>
        {
            if (operation == ConversationMcpAppOperation.SubscribeResource) return subscribeResult.Task;
            Assert.Equal(ConversationMcpAppOperation.UnsubscribeResource, operation);
            unsubscribeStarted.TrySetResult();
            return unsubscribeResult.Task;
        };
        var delivery = new ChatConversationMcpAppEvent(f.Selection.Owner, 7, "run-one", "view-one",
            ChatConversationMcpAppEventKind.ResourceUpdated, Json("""{"uri":"gateway:report"}"""), 1, f.Source);
        var retiredListener = f.Events.Listener!;
        var subscribing = f.Request("resources/subscribe", """{"uri":"gateway:report"}""");
        var retirement = f.Lifetime.RetireAsync();
        try
        {
            f.Panels.Close("canvas");
            Assert.False(f.Session.IsCurrent);
            Assert.True(f.Events.Disposed);
            Assert.Null(f.Events.Listener);
            Assert.DoesNotContain(trace, call => call.Operation == ConversationMcpAppOperation.UnsubscribeResource);
            await f.Request("resources/subscribe", """{"uri":"gateway:late"}""", "2");
            Assert.Single(trace, call => call.Operation == ConversationMcpAppOperation.SubscribeResource);
            retiredListener(delivery);
            Assert.DoesNotContain(f.Sent, packet => packet.TryGetProperty("method", out var method) &&
                method.GetString() == "notifications/resources/updated");

            subscribeResult.SetResult(Json("{}"));
            await subscribing.WaitAsync(DeadlockDeadline);
            await unsubscribeStarted.Task.WaitAsync(DeadlockDeadline);
            Assert.False(retirement.IsCompleted);
            Assert.False(f.Mount.Contains("gateway:report"));
            Assert.Null(f.Events.Listener);
            retiredListener(delivery);
            Assert.DoesNotContain(f.Sent, packet => packet.TryGetProperty("method", out var method) &&
                method.GetString() == "notifications/resources/updated");
        }
        finally
        {
            WriteCallTrace("Ordered calls before releasing cleanup responses", trace);
            subscribeResult.TrySetResult(Json("{}"));
            unsubscribeResult.TrySetResult(Json("{}"));
        }
        var result = await retirement.WaitAsync(DeadlockDeadline);
        WriteCallTrace("Late subscribe drained before its sole compensating unsubscribe", trace);
        Assert.Equal(ChatConversationMcpAppRetirement.Drained, result);
        Assert.Equal(new[] { ConversationMcpAppOperation.SubscribeResource, ConversationMcpAppOperation.UnsubscribeResource },
            trace.OrderBy(call => call.Sequence).Select(call => call.Operation).ToArray());
        Assert.All(trace, call => Assert.Equal(f.Operation.Generation, call.Generation));
    }

    [Fact]
    public async Task LateSubscriptionCompletionAfterRetirementReleasesOnlyCapturedScope()
    {
        using var f = new Fixture();
        await f.Ready();
        var subscribed = new TaskCompletionSource<JsonElement>();
        f.Gateway.InvokeOperation = (op, _) => op == ConversationMcpAppOperation.SubscribeResource
            ? subscribed.Task : Task.FromResult(Json("{}"));
        var request = f.Request("resources/subscribe", """{"uri":"gateway:report"}""");
        f.Session.Dispose();
        subscribed.SetResult(Json("{}"));
        await request;
        Assert.All(f.Gateway.Calls, c => Assert.Equal(new("agent:agent:session", "view-one", "agent"), c.Scope));
        await f.Mount.RetireAsync();
        Assert.Single(f.Gateway.Calls, c => c.Operation == ConversationMcpAppOperation.UnsubscribeResource);
        Assert.DoesNotContain(f.Sent, p => p.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number);
    }

    [Fact]
    public async Task DisplayModeAndSizeFollowNegotiatedNativeHostLimits()
    {
        using var f = new Fixture();
        await f.Ready();
        await f.Request("ui/request-display-mode", """{"mode":"pip"}""");
        Assert.Equal("inline", f.Reply.GetProperty("result").GetProperty("mode").GetString());
        await f.Session.ReceiveAsync(Json("""{"jsonrpc":"2.0","method":"ui/notifications/size-changed","params":{"height":4000}}"""), f.Now);
        Assert.Equal(1200, f.Height);
        await f.Request("ui/request-display-mode", """{"mode":"fullscreen"}""");
        Assert.Equal("fullscreen", f.Mode);
    }

    [Fact]
    public async Task NativeConfirmationStateCancelsOnOwnerChangeAndRejectsLateDialogDecisions()
    {
        using var f = new Fixture();
        using var state = new ChatConversationMcpAppConfirmationState(new Dispatcher());
        var confirmation = new ChatConversationMcpAppConfirmation(new(f.Selection, f.Operation),
            ChatConversationMcpAppActionKind.File, Path: "workspace/report.md");
        var wait = state.ConfirmAsync(confirmation, CancellationToken.None);
        var oldId = state.Pending!.Id;
        f.Panels.Open("canvas", "view-two", "run-one");
        Assert.False(await wait);
        Assert.Null(state.Pending);
        state.Respond(oldId, true);
        Assert.Null(state.Pending);
    }

    [Fact]
    public async Task NativeConfirmationRequiresActualDecisionAndDisposalCannotAccept()
    {
        using var f = new Fixture();
        using var state = new ChatConversationMcpAppConfirmationState(new Dispatcher());
        var confirmation = new ChatConversationMcpAppConfirmation(new(f.Selection, f.Operation),
            ChatConversationMcpAppActionKind.Link, Link: new("https://example.test"));
        var wait = state.ConfirmAsync(confirmation, CancellationToken.None);
        Assert.False(await state.ConfirmAsync(confirmation, CancellationToken.None));
        Assert.False(wait.IsCompleted);
        state.Respond(state.Pending!.Id, true);
        Assert.True(await wait);
        var second = state.ConfirmAsync(confirmation, CancellationToken.None);
        var id = state.Pending!.Id;
        state.Dispose();
        state.Respond(id, true);
        Assert.False(await second);
    }

    [Fact]
    public async Task ConfirmedPluginNavigationIsTypedAndRequiresActualOwnerAcceptance()
    {
        using var f = new Fixture();
        await f.Ready();
        await f.Request("ui/open-link", """{"url":"openclaw://plugins/reports@vendor/app/show?path=%2Freport"}""");
        var link = Assert.Single(f.Actions.Links);
        Assert.Equal(new("reports", "vendor", "show", "/report"), link.Plugin);
        Assert.Single(f.Actions.Confirmations);
        Assert.False(f.Reply.GetProperty("result").TryGetProperty("isError", out _));
    }

    [Fact]
    public async Task ExpiredViewIsDistinctFromRetryableFailure()
    {
        using var f = new Fixture();
        await f.Ready();
        var expired = false;
        f.Session.Expired += () => expired = true;
        f.Gateway.InvokeOperation = (_, _) => Task.FromException<JsonElement>(
            new GatewayRequestException("expired", "UNAVAILABLE", Json("""{"code":"MCP_APP_VIEW_EXPIRED"}""")));
        await f.Request("tools/call", """{"name":"mutate"}""");
        Assert.True(expired);
        Assert.Equal(-32004, f.Reply.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Single(f.Gateway.Calls);
        Assert.DoesNotContain(ChatConversationMcpAppNotice.Unknown, f.Notices);
    }

    [Fact]
    public async Task CurrentModelContextClearFencesAStaleRefreshWithoutClearingAnotherUpdate()
    {
        using var f = new Fixture();
        await f.Ready();
        var baseline = new ChatConversationMcpAppEvent(f.Selection.Owner, 7, "run-one", "view-one",
            ChatConversationMcpAppEventKind.ModelContextChanged, Json("{}"), 1, f.Source);
        f.Gateway.InvokeOperation = (_, _) => Task.FromResult(Json("""{"state":{"updateId":"new","value":"current"}}"""));
        f.Events.Listener!(baseline);
        Assert.Contains(f.Sent, p => p.TryGetProperty("method", out var method) &&
            method.GetString() == "ui/notifications/host-context-changed" &&
            p.GetProperty("params").GetProperty("openai/modelContext").GetProperty("updateId").GetString() == "new");
        f.Sent.Clear();
        f.Events.Listener!(baseline with { Parameters = Json("""{"modelContext":null,"updateId":"old"}""") });
        Assert.Empty(f.Sent);
        var pending = new TaskCompletionSource<JsonElement>();
        f.Gateway.InvokeOperation = (operation, _) => operation == ConversationMcpAppOperation.GetModelContext
            ? pending.Task : Task.FromResult(Json("{}"));
        var updating = f.Request("ui/update-model-context", """{"content":[{"type":"text","text":"new context"}]}""");
        f.Events.Listener!(baseline with { Parameters = Json("""{"modelContext":null,"updateId":"new"}""") });
        Assert.Equal(JsonValueKind.Null, Assert.Single(f.Sent).GetProperty("params").GetProperty("openai/modelContext").ValueKind);
        pending.SetResult(Json("""{"state":{"updateId":"stale"}}"""));
        await updating;
        var changes = f.Sent.Where(p => p.TryGetProperty("method", out var method) &&
            method.GetString() == "ui/notifications/host-context-changed");
        Assert.Equal(JsonValueKind.Null, Assert.Single(changes).GetProperty("params").GetProperty("openai/modelContext").ValueKind);
    }

    [Fact]
    public async Task CancellationAfterAdmittedMutationIsUnknownAndDoesNotRetry()
    {
        using var f = new Fixture();
        await f.Ready();
        f.Gateway.InvokeOperation = (_, _) => Task.FromException<JsonElement>(new OperationCanceledException());
        await f.Request("tools/call", """{"name":"mutate"}""");
        Assert.Single(f.Gateway.Calls);
        Assert.Contains(ChatConversationMcpAppNotice.Unknown, f.Notices);
    }

    [Fact]
    public void CapturedActionCannotSubstituteAnotherRun()
    {
        using var f = new Fixture();
        Assert.True(new ChatConversationMcpAppAction(f.Selection, f.Operation).IsCurrent);
        Assert.False(new ChatConversationMcpAppAction(f.Selection with { RunId = "another-run" }, f.Operation).IsCurrent);
        Assert.False(new ChatConversationMcpAppAction(f.Selection with { RunId = null }, f.Operation).IsCurrent);
    }

    [Fact]
    public async Task NativeAllowDoesNotDependOnWebViewFocusRestoringBeforeDialogClosed()
    {
        using var f = new Fixture();
        await f.Ready();
        f.Actions.Confirm = () =>
        {
            f.Focused = false;
            return Task.FromResult(true);
        };
        await f.Request("ui/message", """{"role":"user","content":[{"type":"text","text":"Explain"}]}""");
        Assert.Single(f.Actions.Messages);
        Assert.False(f.Reply.GetProperty("result").TryGetProperty("isError", out _));
    }

    [Theory]
    [InlineData("Accepted", true, false, "None")]
    [InlineData("Queued", true, false, "Queued")]
    [InlineData("Unconfirmed", false, true, "Unknown")]
    [InlineData("Unconfirmed", true, true, "Unknown")]
    [InlineData("Rejected", false, true, "Rejected")]
    [InlineData("Stale", false, true, "Rejected")]
    [InlineData("Accepted", false, true, "Rejected")]
    public async Task ActualAdmissionPreservesFiveOutcomesAndCustody(
        string kindName, bool ownsPayload, bool isError, string noticeName)
    {
        using var f = new Fixture();
        await f.Ready();
        var kind = Enum.Parse<ChatConversationContentAdmissionKind>(kindName);
        var notice = Enum.Parse<ChatConversationMcpAppNotice>(noticeName);
        f.Actions.Admission = new(kind, "parent detail", ownsPayload);
        await f.Request("ui/message", """{"role":"user","content":[{"type":"text","text":"Explain"}]}""");
        var result = f.Reply.GetProperty("result");
        Assert.Equal(isError, result.TryGetProperty("isError", out var error) && error.GetBoolean());
        var receipt = result.GetProperty("_meta").GetProperty("openclaw/acceptance");
        Assert.Equal(kind.ToString().ToLowerInvariant(), receipt.GetProperty("status").GetString());
        Assert.Equal(ownsPayload, receipt.GetProperty("ownsPayload").GetBoolean());
        Assert.Equal("parent detail", receipt.GetProperty("error").GetString());
        Assert.Contains(notice, f.Notices);
        Assert.Single(f.Actions.Messages);
        Assert.Empty(f.Gateway.Calls);
    }

    [Fact]
    public async Task UnconfirmedCustodyKeepsOriginalNullErrorWithoutRetry()
    {
        using var f = new Fixture();
        await f.Ready();
        f.Actions.Admission = new(ChatConversationContentAdmissionKind.Unconfirmed, OwnsPayload: true);

        await f.Request("ui/message", """{"role":"user","content":[{"type":"text","text":"Explain"}]}""");

        var result = f.Reply.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        var receipt = result.GetProperty("_meta").GetProperty("openclaw/acceptance");
        Assert.Equal("unconfirmed", receipt.GetProperty("status").GetString());
        Assert.True(receipt.GetProperty("ownsPayload").GetBoolean());
        Assert.Equal(JsonValueKind.Null, receipt.GetProperty("error").ValueKind);
        Assert.Contains(ChatConversationMcpAppNotice.Unknown, f.Notices);
        Assert.Single(f.Actions.Messages);
        Assert.Empty(f.Gateway.Calls);
    }

    [Fact]
    public async Task RetirementCancelsLocalEffectsThenWaitsOnlyForItsExactTeardownResponse()
    {
        var deadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = new Fixture(waitForTeardown: _ => deadline.Task);
        await f.Ready();
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Actions.Confirm = () => confirmation.Task;
        var message = f.Request("ui/message", """{"role":"user","content":[{"type":"text","text":"Explain"}]}""");
        var retirement = f.Session.RetireAsync();
        Assert.Equal(ConversationMcpAppHandshakeState.Retiring, f.Session.Phase);
        Assert.True(f.Events.Disposed);
        Assert.False(retirement.IsCompleted);
        Assert.Contains(f.Sent, p => p.TryGetProperty("method", out var m) && m.GetString() == "notifications/cancelled");
        var teardown = f.Sent.Single(p => p.TryGetProperty("method", out var m) && m.GetString() == "ui/resource-teardown");
        f.Session.ReceiveRetirementResponse(Json("""{"jsonrpc":"2.0","id":"unknown","result":{}}"""));
        Assert.False(retirement.IsCompleted);
        await f.Request("tools/call", """{"name":"denied"}""");
        Assert.Empty(f.Gateway.Calls);
        confirmation.SetResult(true);
        await message;
        Assert.Empty(f.Actions.Messages);
        f.Session.ReceiveRetirementResponse(JsonSerializer.SerializeToElement(new
        { jsonrpc = "2.0", id = teardown.GetProperty("id").GetString(), result = new { } }));
        await retirement;
        Assert.False(deadline.Task.IsCompleted);
    }

    [Fact]
    public async Task RetirementDeadlineAllowsBrowserCloseWithoutGuestCooperation()
    {
        var deadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan actualBudget = default;
        using var f = new Fixture(waitForTeardown: budget => { actualBudget = budget; return deadline.Task; });
        await f.Ready();
        var retiring = f.Session.RetireAsync();
        Assert.False(retiring.IsCompleted);
        Assert.InRange(actualBudget.TotalMilliseconds, 50, 5000);
        deadline.SetResult();
        await retiring;
        Assert.False(f.Session.IsCurrent);
    }

    [Fact]
    public async Task SameViewCannotReadmitBeforeLateSubscribeAndItsSingleUnsubscribeComplete()
    {
        using var f = new Fixture();
        await f.Ready();
        var subscribed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unsubscribed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Gateway.InvokeOperation = (op, _) =>
        {
            if (op == ConversationMcpAppOperation.SubscribeResource) return subscribed.Task;
            cleanupStarted.TrySetResult();
            return unsubscribed.Task;
        };
        var request = f.Request("resources/subscribe", """{"uri":"gateway:report"}""");
        await f.Session.RetireAsync();
        var next = f.Lifetime.AcquireAsync(new(f.Selection, f.Operation), CancellationToken.None);
        Assert.False(next.IsCompleted);
        Assert.Single(f.Gateway.Calls);
        subscribed.SetResult(Json("{}"));
        await request;
        await cleanupStarted.Task;
        Assert.False(next.IsCompleted);
        unsubscribed.SetResult(Json("{}"));
        var mount = await next;
        Assert.NotNull(mount);
        Assert.Single(f.Gateway.Calls, c => c.Operation == ConversationMcpAppOperation.UnsubscribeResource);
        await mount.RetireAsync();
    }

    [Fact]
    public async Task UnconfirmedCleanupPoisonsSameSocketReadmissionInsteadOfTouchingANewAccount()
    {
        using var f = new Fixture();
        await f.Ready();
        await f.Request("resources/subscribe", """{"uri":"gateway:report"}""");
        f.AccountRevision++;
        f.Panels.Refresh();
        await f.Session.RetireAsync();
        Assert.False(await f.Mount.RetireAsync());
        Assert.DoesNotContain(f.Gateway.Calls, c => c.Operation == ConversationMcpAppOperation.UnsubscribeResource);
        var selection = f.Selection with { Owner = f.Panels.Context!.Identity };
        f.Panels.Open("canvas", "view-one", "run-one");
        var operation = f.Panels.BeginOperation("canvas", "run-one", expectedContentId: "view-one")!;
        Assert.Null(await f.Lifetime.AcquireAsync(new(selection, operation), CancellationToken.None));
        f.Gateway.Epoch++;
        f.Panels.Refresh();
        f.Panels.Open("canvas", "view-one", "run-one");
        operation = f.Panels.BeginOperation("canvas", "run-one", expectedContentId: "view-one")!;
        Assert.Null(await f.Lifetime.AcquireAsync(new(selection, operation), CancellationToken.None));
        var freshLifetime = new ChatConversationMcpAppLifetime(operation.Context, f.ReadScope.Capture()!,
            (context, _) => context.AccountRevision == f.AccountRevision);
        var fresh = await freshLifetime.AcquireAsync(new(selection, operation), CancellationToken.None);
        Assert.NotNull(fresh);
        await fresh.RetireAsync();
    }

    [Fact]
    public async Task FailedSubscriptionNeitherAdmitsUpdatesNorConsumesTheSubscriptionLimit()
    {
        using var f = new Fixture();
        await f.Ready();
        f.Gateway.InvokeOperation = (_, _) => Task.FromException<JsonElement>(new GatewayRequestException("denied", "INVALID_REQUEST"));
        for (var i = 0; i < 129; i++) await f.Request("resources/subscribe", $$"""{"uri":"gateway:{{i}}"}""");
        Assert.Equal(129, f.Gateway.Calls.Count);
        f.Sent.Clear();
        f.Events.Listener!(new(f.Selection.Owner, 7, "run-one", "view-one",
            ChatConversationMcpAppEventKind.ResourceUpdated, Json("""{"uri":"gateway:0"}"""), 1, f.Source));
        Assert.Empty(f.Sent);
        await f.Session.RetireAsync();
        Assert.DoesNotContain(f.Gateway.Calls, c => c.Operation == ConversationMcpAppOperation.UnsubscribeResource);
    }

    [Fact]
    public async Task OffThreadConfirmationCancellationPublishesOnlyThroughInjectedDispatcher()
    {
        using var f = new Fixture();
        var dispatcher = new Dispatcher();
        using var state = new ChatConversationMcpAppConfirmationState(dispatcher);
        var changes = 0;
        state.Changed += () => { Assert.True(dispatcher.HasThreadAccess); changes++; };
        using var cancel = new CancellationTokenSource();
        var wait = state.ConfirmAsync(new(new(f.Selection, f.Operation), ChatConversationMcpAppActionKind.Link), cancel.Token);
        Assert.Equal(1, changes);
        dispatcher.HasThreadAccess = false;
        await Task.Run(cancel.Cancel);
        Assert.False(await wait);
        Assert.Equal(1, changes);
        dispatcher.Drain();
        Assert.Equal(2, changes);
        Assert.Null(state.Pending);
    }

    [Fact]
    public async Task InitializedNotificationRetainsItsTokenUntilCancelledRefreshActuallyDrains()
    {
        using var f = new Fixture();
        await f.Request("ui/initialize", """{"protocolVersion":"2026-01-26","appInfo":{"name":"App","version":"1"},"appCapabilities":{}}""");
        var refresh = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Gateway.InvokeOperation = (_, _) => refresh.Task;
        var initialized = f.Session.ReceiveAsync(Json("""{"jsonrpc":"2.0","method":"ui/notifications/initialized"}"""), f.Now);
        await f.Session.RetireAsync();
        refresh.SetResult(Json("""{"state":{"updateId":"late"}}"""));
        await initialized;
        Assert.False(f.Session.IsCurrent);
        Assert.DoesNotContain(f.Sent, p => p.TryGetProperty("params", out var pms) &&
            pms.TryGetProperty("openai/modelContext", out var context) && context.ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task RootEventAdapterCapturesOwnerAndMarshalsOffThreadDeliveryBeforeAndAfterChecks()
    {
        using var f = new Fixture();
        var source = new RootEvents { IsAvailable = true };
        var dispatcher = new Dispatcher { HasThreadAccess = false };
        var adapter = new ChatConversationMcpAppEvents(source, dispatcher, f.Source,
            (owner, _) => owner.Operation.Context.AccountRevision == f.AccountRevision);
        var received = new List<ChatConversationMcpAppEvent>();
        using var subscription = adapter.Subscribe(new(f.Selection, f.Operation), "view-one", delivery =>
        { Assert.True(dispatcher.HasThreadAccess); received.Add(delivery); });
        Assert.Equal(f.Selection.Owner, source.Captured!.Selection.Owner);
        var original = new ChatConversationMcpAppEvent(f.Selection.Owner, 7, "run-one", "view-one",
            ChatConversationMcpAppEventKind.ResourceUpdated, Json("""{"uri":"gateway:owned","unknown":{"literal":"data"}}"""), 1, f.Source);
        await Task.Run(() =>
        {
            source.Listener!(original with { AccountRevision = 2 });
            source.Listener!(original with { ViewId = "other" });
            source.Listener!(original with { ConnectionEpoch = 8 });
            source.Listener!(original);
        });
        Assert.Empty(received);
        Assert.Single(dispatcher.Queue);
        dispatcher.Drain();
        Assert.Equal("data", Assert.Single(received).Parameters.GetProperty("unknown").GetProperty("literal").GetString());
        received.Clear();
        dispatcher.HasThreadAccess = false;
        source.Listener!(original);
        f.AccountRevision++;
        f.Panels.Refresh();
        dispatcher.Drain();
        Assert.Empty(received);
        subscription.Dispose();
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task MissingOriginalSourceProvenanceDoesNotSubscribeOrAdmitProductionRead()
    {
        using var f = new Fixture();
        await f.Session.RetireAsync();
        var source = new RootEvents();
        var adapter = new ChatConversationMcpAppEvents(source, new Dispatcher(), f.Source,
            (owner, _) => owner.Operation.Context.AccountRevision == f.AccountRevision);
        using var subscription = adapter.Subscribe(new(f.Selection, f.Operation), "view-one", _ => Assert.Fail("No source authority."));
        Assert.Null(source.Listener);
        using var controller = new ChatConversationMcpAppController(f.Panels, f.Selection, f.Actions, adapter, f.Lifetime);
        await controller.LoadAsync();
        Assert.Equal(ChatConversationMcpAppPhase.Unavailable, controller.Snapshot.Phase);
        Assert.Equal(0, f.Gateway.ViewReads);
    }

    [Fact]
    public async Task OriginalSourceRetirementDropsQueuedEventsWithoutRebindingTheCurrentRevision()
    {
        using var f = new Fixture();
        var source = new RootEvents { IsAvailable = true };
        var dispatcher = new Dispatcher { HasThreadAccess = false };
        var adapter = new ChatConversationMcpAppEvents(source, dispatcher, f.Source,
            (owner, original) => owner.Operation.Context.AccountRevision == 1 &&
                original.ProfileDataRevision == f.Source.ProfileDataRevision);
        var deliveries = 0;
        using var subscription = adapter.Subscribe(new(f.Selection, f.Operation), "view-one", _ => deliveries++);
        var delivery = new ChatConversationMcpAppEvent(f.Selection.Owner, 7, "run-one", "view-one",
            ChatConversationMcpAppEventKind.ResourceUpdated, Json("""{"uri":"gateway:report"}"""), 1, f.Source);
        await Task.Run(() => source.Listener!(delivery));
        Assert.Single(dispatcher.Queue);
        f.ReadScope.Invalidate();
        var latest = f.ReadScope.Capture()!;
        Assert.True(latest.IsCurrent);
        source.Listener!(delivery with { Source = latest });
        dispatcher.Drain();
        Assert.Equal(0, deliveries);
        Assert.False(adapter.IsAvailable);
        Assert.False(f.Session.IsCurrent);
    }

    [Fact]
    public async Task LifetimeRetirementRevokesEffectsAndConfirmationBeforeReportingItsTypedDrain()
    {
        using var f = new Fixture();
        await f.Ready();
        var allow = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Actions.Confirm = () => allow.Task;
        var message = f.Request("ui/message", """{"role":"user","content":[{"type":"text","text":"Explain"}]}""");
        var retirement = f.Lifetime.RetireAsync();
        Assert.False(f.Session.IsCurrent);
        Assert.Null(await f.Lifetime.AcquireAsync(new(f.Selection, f.Operation), CancellationToken.None));
        allow.SetResult(true);
        await message;
        Assert.Empty(f.Actions.Messages);
        Assert.Equal(ChatConversationMcpAppRetirement.Drained, await retirement);
    }

    [Fact]
    public async Task SourceRetiredExceptionIsNotReportedAsUserCancellationOrRetryableFailure()
    {
        using var f = new Fixture();
        await f.Ready();
        f.Gateway.InvokeOperation = (_, _) => Task.FromException<JsonElement>(new GatewayDataReadRetiredException());
        await f.Request("tools/list");
        Assert.False(f.Session.IsCurrent);
        Assert.Single(f.Gateway.Calls);
        Assert.DoesNotContain(ChatConversationMcpAppNotice.Error, f.Notices);
        Assert.DoesNotContain(ChatConversationMcpAppNotice.Unknown, f.Notices);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("source-error")]
    public async Task CleanupFailureAlwaysPoisonsAndCompletesSameViewReadmissionInsteadOfHanging(string failure)
    {
        using var f = new Fixture();
        await f.Ready();
        await f.Request("resources/subscribe", """{"uri":"gateway:report"}""");
        if (failure == "malformed")
            f.Gateway.InvokeOperation = (_, _) => Task.FromResult(Json("[]"));
        else f.ThrowSourceCheck = true;
        await f.Session.RetireAsync().WaitAsync(DeadlockDeadline);
        Assert.False(await f.Mount.RetireAsync().WaitAsync(DeadlockDeadline));
        f.ThrowSourceCheck = false;
        Assert.Null(await f.Lifetime.AcquireAsync(new(f.Selection, f.Operation), CancellationToken.None)
            .WaitAsync(DeadlockDeadline));
        Assert.Equal(ChatConversationMcpAppRetirement.RequiresFreshConnection,
            await f.Lifetime.RetireAsync().WaitAsync(DeadlockDeadline));
        Assert.Equal(failure == "malformed" ? 1 : 0,
            f.Gateway.Calls.Count(call => call.Operation == ConversationMcpAppOperation.UnsubscribeResource));
    }

    [Fact]
    public async Task MalformedMutationResultRepliesUnknownAndNeverRetries()
    {
        using var f = new Fixture();
        await f.Ready();
        f.Gateway.InvokeOperation = (_, _) => Task.FromResult(Json("[]"));
        await f.Request("tools/call", """{"name":"mutate"}""");
        Assert.Single(f.Gateway.Calls);
        Assert.Contains(ChatConversationMcpAppNotice.Unknown, f.Notices);
        Assert.Equal(-32000, f.Reply.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("unknown", f.Reply.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task RetirementCleanupStartsOutsideMountGate()
    {
        using var f = new Fixture();
        await f.Ready();
        await f.Request("resources/subscribe", """{"uri":"gateway:report"}""");
        var inspected = false;
        f.Gateway.InvokeOperation = (operation, _) =>
        {
            Assert.Equal(ConversationMcpAppOperation.UnsubscribeResource, operation);
            Assert.False(Task.Run(() => f.Mount.Contains("gateway:report"))
                .WaitAsync(DeadlockDeadline).GetAwaiter().GetResult());
            inspected = true;
            return Task.FromResult(Json("{}"));
        };
        Assert.True(await f.Mount.RetireAsync().WaitAsync(DeadlockDeadline));
        Assert.True(inspected);
        Assert.Single(f.Gateway.Calls, call => call.Operation == ConversationMcpAppOperation.UnsubscribeResource);
    }

    [Theory]
    [InlineData("tools/list", "{}", false)]
    [InlineData("tools/call", """{"name":"mutate"}""", true)]
    [InlineData("ui/message", """{"role":"user","content":[{"type":"text","text":"Explain"}]}""", true)]
    [InlineData("openai/files/open", """{"path":"report.txt"}""", true)]
    [InlineData("ui/open-link", """{"url":"https://example.com/"}""", true)]
    public async Task UnexpectedOperationFailureAlwaysRepliesWithoutRetryOrLeakingDetails(
        string method, string parameters, bool unknown)
    {
        using var f = new Fixture();
        await f.Ready();
        var failure = new KeyNotFoundException("private-delegate-detail");
        if (method.StartsWith("tools/", StringComparison.Ordinal))
            f.Gateway.InvokeOperation = (_, _) => Task.FromException<JsonElement>(failure);
        else
        {
            f.Actions.AdmissionFailure = failure;
            if (method == "openai/files/open")
                f.Gateway.InvokeOperation = (_, _) => Task.FromResult(Json("""{"path":"report.txt"}"""));
        }

        await f.Request(method, parameters);

        Assert.Single(f.Sent, packet => packet.TryGetProperty("id", out _));
        Assert.Equal(-32000, f.Reply.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(unknown ? "The action outcome is unknown. It was not retried." :
            "The App operation could not complete.", f.Reply.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain("private-delegate-detail", f.Reply.GetRawText());
        Assert.Contains(unknown ? ChatConversationMcpAppNotice.Unknown : ChatConversationMcpAppNotice.Error, f.Notices);
        Assert.Equal(method is "ui/message" or "ui/open-link" ? 0 : 1, f.Gateway.Calls.Count);
        Assert.Equal(method == "ui/message" ? 1 : 0, f.Actions.Messages.Count);
        Assert.Equal(method == "openai/files/open" ? 1 : 0, f.Actions.Files.Count);
        Assert.Equal(method == "ui/open-link" ? 1 : 0, f.Actions.Links.Count);
    }

    [Theory]
    [InlineData("qualifier")]
    [InlineData("receiver")]
    public async Task EventCallbacksCanReentrantlyDisposeFromAnotherThreadWithoutHoldingAdapterGate(string stage)
    {
        using var f = new Fixture();
        var source = new RootEvents { IsAvailable = true };
        var dispatcher = new Dispatcher { HasThreadAccess = false };
        IDisposable? owned = null;
        var adapter = new ChatConversationMcpAppEvents(source, dispatcher, f.Source, (_, _) =>
        {
            if (stage == "qualifier" && owned is not null)
                Task.Run(owned.Dispose).WaitAsync(DeadlockDeadline).GetAwaiter().GetResult();
            return true;
        });
        var received = 0;
        using var subscription = adapter.Subscribe(new(f.Selection, f.Operation), "view-one", _ =>
        {
            Task.Run(owned!.Dispose).WaitAsync(DeadlockDeadline).GetAwaiter().GetResult();
            received++;
        });
        owned = subscription;
        var delivery = new ChatConversationMcpAppEvent(f.Selection.Owner, 7, "run-one", "view-one",
            ChatConversationMcpAppEventKind.ResourceUpdated, Json("""{"uri":"gateway:report"}"""), 1, f.Source);
        var listener = source.Listener!;
        await Task.Run(() => listener(delivery)).WaitAsync(DeadlockDeadline);
        dispatcher.Drain();
        Assert.True(source.Disposed);
        Assert.Equal(stage == "receiver" ? 1 : 0, received);
    }

    [Fact]
    public void UnavailableSubscriptionNeverSilentlyAttachesWhenTheSourceBecomesAvailable()
    {
        using var f = new Fixture();
        var source = new RootEvents();
        var adapter = new ChatConversationMcpAppEvents(source, new Dispatcher(), f.Source, (_, _) => true);
        using var unavailable = adapter.Subscribe(new(f.Selection, f.Operation), "view-one", _ => { });
        Assert.Null(source.Listener);
        source.IsAvailable = true;
        Assert.Null(source.Listener);
        using var admitted = adapter.Subscribe(new(f.Selection, f.Operation), "view-one", _ => { });
        Assert.NotNull(source.Listener);
    }

    [Fact]
    public async Task PoisonedScopeBoundRefusesThe129thKeyWithoutEvictingUnsafeHistory()
    {
        using var f = new Fixture();
        await f.Session.RetireAsync();
        f.Gateway.InvokeOperation = (operation, _) => Task.FromResult(
            operation == ConversationMcpAppOperation.UnsubscribeResource ? Json("[]") : Json("{}"));
        ChatConversationMcpAppAction Select(int index)
        {
            var id = "view-" + index;
            f.Panels.Open("canvas", id, "run-one");
            var content = f.Selection.Content with { DocumentId = id, McpApp = f.Selection.Content.McpApp! with { ViewId = id } };
            var selection = f.Selection with { Content = content };
            return new(selection, f.Panels.BeginOperation("canvas", "run-one", expectedContentId: id)!);
        }
        for (var index = 0; index < 128; index++)
        {
            var mount = await f.Lifetime.AcquireAsync(Select(index), CancellationToken.None).WaitAsync(DeadlockDeadline);
            Assert.NotNull(mount);
            await mount.SubscriptionAsync(ConversationMcpAppOperation.SubscribeResource,
                Json("""{"uri":"gateway:report"}"""), CancellationToken.None);
            Assert.False(await mount.RetireAsync().WaitAsync(DeadlockDeadline));
        }
        Assert.Null(await f.Lifetime.AcquireAsync(Select(128), CancellationToken.None).WaitAsync(DeadlockDeadline));
        Assert.Null(await f.Lifetime.AcquireAsync(Select(0), CancellationToken.None).WaitAsync(DeadlockDeadline));
        Assert.Equal(256, f.Gateway.Calls.Count);
    }
}
