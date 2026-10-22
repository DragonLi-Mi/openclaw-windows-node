using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatConversationMcpAppSessionTests
{
    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    public class Gateway : DispatchProxy
    {
        internal long Epoch = 7;
        internal string[] Scopes = ["operator.write"];
        internal int ViewReads;
        internal readonly List<(ConversationMcpAppScope Scope, ConversationMcpAppOperation Operation, JsonElement Parameters, long Epoch)> Calls = [];
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
        internal bool Accept = true;
        public Task<bool> ConfirmAsync(ChatConversationMcpAppConfirmation request, CancellationToken cancellationToken)
        { Confirmations.Add(request); return Confirm(); }
        public Task<bool> AcceptMessageAsync(ChatConversationMcpAppAction owner, ConversationMcpAppMessage message, CancellationToken cancellationToken)
        { Messages.Add(message); return Task.FromResult(Accept); }
        public Task<bool> AcceptFileAsync(ChatConversationMcpAppAction owner, ChatConversationMcpAppFile file, CancellationToken cancellationToken)
        { Files.Add(file); return Task.FromResult(Accept); }
        public Task<bool> OpenLinkAsync(ChatConversationMcpAppAction owner, ConversationMcpAppLink link, CancellationToken cancellationToken)
        { Links.Add(link); return Task.FromResult(Accept); }
        public bool SetDisplayMode(ChatConversationMcpAppAction owner, string mode) => Accept;
        public void ReturnToConversation(ChatConversationMcpAppAction owner) { }
    }

    private sealed class Events : IChatConversationMcpAppEvents
    {
        internal Action<ChatConversationMcpAppEvent>? Listener;
        internal bool Disposed;
        public IDisposable Subscribe(ChatConversationMcpAppAction owner, string viewId, Action<ChatConversationMcpAppEvent> receive)
        { Listener = receive; return new Subscription(this); }
        private sealed class Subscription(Events events) : IDisposable
        { public void Dispose() { events.Disposed = true; events.Listener = null; } }
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
        internal bool Focused = true;
        internal int Initialized;
        internal string Mode = "";
        internal double Height;
        internal readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-22T12:00:00Z");
        internal Fixture(bool enabled = true)
        {
            var client = DispatchProxy.Create<IOperatorGatewayClient, Gateway>();
            Gateway = (Gateway)client;
            Panels = new(key => ChatConversationContext.Capture("gateway-record", client,
                new SessionInfo { Key = key, SessionId = "session-id", AgentId = "agent" }, "profile-one", 1));
            Panels.Bind("agent:agent:session");
            Panels.Open("canvas", "view-one", "run-one");
            Selection = new(Panels.Context!.Identity,
                new(ConversationCanvasContentKind.McpApp, "view-one", null, "App", true,
                    McpApp: new("view-one", "server", "tool", null, "call-one", null, null)), "message-id", "run-one");
            Operation = Panels.BeginOperation("canvas", "run-one", expectedContentId: "view-one")!;
            Session = new(new(Selection, Operation), new()
            {
                Html = "<main>App</main>", GatewayOrigin = new("https://gateway.example"),
                SandboxUrl = new("https://sandbox.example/mcp-app-sandbox"),
                MessageSupported = enabled, OpenFilesSupported = enabled, FileResourcesSupported = enabled,
                UpdateModelContextSupported = enabled, RichModelContextSupported = enabled
            }, Actions, Events, () => Focused, packet => Sent.Add(packet), Notices.Add,
                (mode, height) => { Mode = mode; Height = height; }, () => Initialized++);
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

    [Fact]
    public async Task ProductionControllerReadsOnlyTheCanonicalSelectedContentWhenHooksExist()
    {
        using var f = new Fixture();
        using var controller = new ChatConversationMcpAppController(f.Panels, f.Selection, f.Actions, f.Events);
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
            if (revoke == "focus") f.Focused = false;
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
            f.Focused = false;
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
        await f.Request("resources/subscribe", """{"uri":"gateway:report"}""");
        f.Sent.Clear();
        var delivery = new ChatConversationMcpAppEvent(f.Selection.Owner, 7, "run-one", "view-one",
            ChatConversationMcpAppEventKind.ResourceUpdated, Json("""{"uri":"gateway:report"}"""));
        f.Events.Listener!(delivery with { ConnectionEpoch = 8 });
        f.Events.Listener!(delivery with { ViewId = "elsewhere" });
        f.Events.Listener!(delivery with { RunId = "run-two" });
        f.Events.Listener!(delivery with { Owner = f.Selection.Owner with { GatewayId = "another" } });
        Assert.Empty(f.Sent);
        f.Events.Listener!(delivery);
        Assert.Equal("notifications/resources/updated", Assert.Single(f.Sent).GetProperty("method").GetString());
        f.Session.Dispose();
        Assert.True(f.Events.Disposed);
        Assert.Equal(ConversationMcpAppOperation.UnsubscribeResource, f.Gateway.Calls.Last().Operation);
        await f.Request("tools/call", """{"name":"no-new-effects"}""");
        Assert.Equal(2, f.Gateway.Calls.Count);
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
        Assert.Equal(2, f.Gateway.Calls.Count(c => c.Operation == ConversationMcpAppOperation.UnsubscribeResource));
        Assert.Empty(f.Sent);
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
        using var state = new ChatConversationMcpAppConfirmationState();
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
        using var state = new ChatConversationMcpAppConfirmationState();
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
            ChatConversationMcpAppEventKind.ModelContextChanged, Json("{}"));
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
}
