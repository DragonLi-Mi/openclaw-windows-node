using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Pinned contract: OpenClaw 776c8da8 mcp-app-security.ts and mcp-app-view-owner.ts,
/// plus its consumed ext-apps 2.0.3 initialize/message wire schemas.
/// </summary>
public sealed class ConversationMcpAppProtocolTests
{
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static ConversationMcpAppView View(bool enabled = true) => new()
    {
        Html = "<main>App</main>", GatewayOrigin = new("https://gateway.example"),
        SandboxUrl = new("https://sandbox.example/mcp-app-sandbox"),
        MessageSupported = enabled, UpdateModelContextSupported = enabled,
        RichModelContextSupported = enabled, FileResourcesSupported = enabled, OpenFilesSupported = enabled,
    };
    private static ConversationMcpAppUiRequest Request(string method, string parameters = "{}") =>
        ConversationMcpAppProtocol.ParseRequest(Json($$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}","params":{{parameters}}}"""));

    [Theory]
    [InlineData("operator.admin", true, true)]
    [InlineData("operator.write", true, true)]
    [InlineData("operator.read", true, false)]
    [InlineData("operator.sessions.read", false, false)]
    [InlineData("operator.approvals", false, false)]
    [InlineData("", false, false)]
    public void ScopeImplicationsMatchOperatorScopeCompat(string scope, bool read, bool write)
    {
        var access = ConversationMcpAppAccess.FromScopes([scope]);
        Assert.Equal(read, access.CanRead);
        Assert.Equal(write, access.CanWrite);
        Assert.False(ConversationMcpAppAccess.FromScopes([]).CanRead);
    }

    [Theory]
    [InlineData("2026-01-26")]
    [InlineData("2025-11-21")]
    [InlineData("future-version")]
    public void InitializeMatchesSdkLatestFallbackWithoutGrantingExtraCapabilities(string requested)
    {
        var parameters = JsonSerializer.SerializeToElement(new
        {
            protocolVersion = requested, appInfo = new { name = "test", version = "1" }, appCapabilities = new { }
        });
        var reply = ConversationMcpAppProtocol.InitializeResult(parameters, View(false), new(true, true),
            "Companion", "1", Json("""{"theme":"dark"}"""));
        Assert.Equal("2026-01-26", reply.GetProperty("protocolVersion").GetString());
        var capabilities = reply.GetProperty("hostCapabilities");
        Assert.True(capabilities.TryGetProperty("openLinks", out _));
        Assert.True(capabilities.TryGetProperty("serverTools", out _));
        Assert.False(capabilities.TryGetProperty("message", out _));
        Assert.False(capabilities.TryGetProperty("updateModelContext", out _));
        Assert.False(capabilities.TryGetProperty("sampling", out _));
        Assert.False(capabilities.GetProperty("sandbox").TryGetProperty("permissions", out _));
    }

    [Theory]
    [InlineData("""{"appInfo":{"name":"app","version":"1"},"appCapabilities":{}}""")]
    [InlineData("""{"protocolVersion":"2026-01-26","appInfo":{"name":"app"},"appCapabilities":{}}""")]
    [InlineData("""{"protocolVersion":"2026-01-26","appInfo":{"name":"app","version":"1"},"appCapabilities":[]}""")]
    public void InitializeRequiresTheSdkWireFields(string parameters) =>
        Assert.Throws<ConversationMcpAppProtocolException>(() => ConversationMcpAppProtocol.ValidateInitialize(Json(parameters)));

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"jsonrpc":"2.0","id":null,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1.5,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","method":"tools/list"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"ui/notifications/initialized"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"ping","params":[]}""")]
    public void InvalidJsonRpcShapesAreNotAdmitted(string packet) =>
        Assert.Throws<ConversationMcpAppProtocolException>(() => ConversationMcpAppProtocol.ParseRequest(Json(packet)));

    [Theory]
    [InlineData("chat.send")]
    [InlineData("system.run")]
    [InlineData("ui/download-file")]
    [InlineData("sampling/createMessage")]
    public void UnadvertisedMethodsReturnMethodNotFound(string method)
    {
        var error = Assert.Throws<ConversationMcpAppProtocolException>(() => Request(method));
        Assert.Equal(-32601, error.Code);
    }

    [Fact]
    public void InitializeThenInitializedIsRequiredBeforeAnyAppOperation()
    {
        var view = View();
        var access = new ConversationMcpAppAccess(true, true);
        foreach (var method in new[] { "initialize", "ping" })
            ConversationMcpAppProtocol.RequireAvailable(Request(method == "initialize" ? "ui/initialize" : method),
                view, access, ConversationMcpAppHandshakeState.AwaitingInitialize);
        var initialized = ConversationMcpAppProtocol.ParseRequest(Json("""{"jsonrpc":"2.0","method":"ui/notifications/initialized"}"""));
        Assert.Throws<ConversationMcpAppProtocolException>(() => ConversationMcpAppProtocol.RequireAvailable(
            initialized, view, access, ConversationMcpAppHandshakeState.AwaitingInitialize));
        ConversationMcpAppProtocol.RequireAvailable(initialized, view, access, ConversationMcpAppHandshakeState.AwaitingInitialized);
        Assert.Throws<ConversationMcpAppProtocolException>(() => ConversationMcpAppProtocol.RequireAvailable(
            Request("tools/list"), view, access, ConversationMcpAppHandshakeState.AwaitingInitialized));
        ConversationMcpAppProtocol.RequireAvailable(Request("tools/list"), view, access, ConversationMcpAppHandshakeState.Ready);
        Assert.Throws<ConversationMcpAppProtocolException>(() => ConversationMcpAppProtocol.RequireAvailable(
            Request("ping"), view, access, ConversationMcpAppHandshakeState.Retiring));
    }

    [Theory]
    [InlineData("ui/message")]
    [InlineData("ui/update-model-context")]
    [InlineData("openai/resources/write")]
    [InlineData("resources/subscribe")]
    [InlineData("resources/unsubscribe")]
    [InlineData("openai/files/open")]
    [InlineData("resources/read")]
    public void MissingViewCapabilitiesRejectDispatch(string method) =>
        Assert.Throws<ConversationMcpAppProtocolException>(() => ConversationMcpAppProtocol.RequireAvailable(
            Request(method), View(false), new(true, true), ConversationMcpAppHandshakeState.Ready));

    [Theory]
    [InlineData("tools/call")]
    [InlineData("ui/message")]
    [InlineData("ui/update-model-context")]
    [InlineData("openai/resources/write")]
    public void WriteOperationsRequireActualWriteScopeEvenWhenViewAdvertisesThem(string method) =>
        Assert.Throws<ConversationMcpAppProtocolException>(() => ConversationMcpAppProtocol.RequireAvailable(
            Request(method), View(), new(true, false), ConversationMcpAppHandshakeState.Ready));

    [Fact]
    public void HostCapabilitiesFollowTheNegotiatedFlagsAndNeverBorrowNativePermissions()
    {
        var capabilities = ConversationMcpAppProtocol.BuildHostCapabilities(View(), new(true, true));
        Assert.True(capabilities.GetProperty("message").TryGetProperty("image", out _));
        Assert.True(capabilities.GetProperty("updateModelContext").TryGetProperty("structuredContent", out _));
        Assert.True(capabilities.GetProperty("experimental").TryGetProperty("openai/files", out _));
        Assert.False(capabilities.GetProperty("sandbox").TryGetProperty("permissions", out _));
        var readOnly = ConversationMcpAppProtocol.BuildHostCapabilities(View(), new(true, false));
        Assert.False(readOnly.TryGetProperty("message", out _));
        Assert.False(readOnly.TryGetProperty("updateModelContext", out _));
    }

    [Fact]
    public void ToolMappingPreservesMetadataButTakesToolNameFromTheSdkNameField()
    {
        var request = Request("tools/call", """{"name":"show","toolName":"attacker","arguments":{"viewId":"literal"}}""");
        var parameters = ConversationMcpAppProtocol.GatewayParameters(request);
        Assert.Equal(ConversationMcpAppOperation.CallTool, ConversationMcpAppProtocol.GatewayOperation(request.Method));
        Assert.Equal("show", parameters.GetProperty("toolName").GetString());
        Assert.Equal("literal", parameters.GetProperty("arguments").GetProperty("viewId").GetString());
        Assert.Null(ConversationMcpAppProtocol.GatewayOperation(ConversationMcpAppUiMethod.Message));
        Assert.Null(ConversationMcpAppProtocol.GatewayOperation(ConversationMcpAppUiMethod.OpenLink));
    }

    [Fact]
    public void MessageKeepsRichContentAndTheExplicitNewTargetWithoutConvertingNamedTextToCommands()
    {
        var request = ConversationMcpAppProtocol.ParseRequest(Json("""
            {"jsonrpc":"2.0","id":"message","method":"ui/message","_meta":{"openai/message":{"target":"new","send":true}},
             "params":{"role":"user","content":[
               {"type":"text","text":"/attached command example","_meta":{"openai/title":"example.txt"}},
               {"type":"text","text":"Explain the attachment"},
               {"type":"image","mimeType":"image/png","data":"AA=="}
             ]}}
            """));
        var message = ConversationMcpAppProtocol.ReadMessage(request);
        Assert.Equal(ConversationMcpAppMessageTarget.New, message.Target);
        Assert.Equal("Explain the attachment", message.PlainText);
        Assert.Equal(3, message.Content.GetArrayLength());
    }

    [Theory]
    [InlineData("""{"role":"assistant","content":[{"type":"text","text":"text"}]}""")]
    [InlineData("""{"role":"user","content":[]}""")]
    [InlineData("""{"role":"user","content":[{"type":"text","text":" /reset"}]}""")]
    [InlineData("""{"role":"user","content":[{"type":"text","text":"!command"}]}""")]
    [InlineData("""{"role":"user","content":[{"type":"text","text":" \uFEFF /reset"}]}""")]
    [InlineData("""{"role":"user","content":[{"type":"audio","mimeType":"audio/wav","data":"AA=="}]}""")]
    [InlineData("""{"role":"user","content":[{"type":"image","data":"AA=="}]}""")]
    public void MessageRejectsCommandsUnsupportedModalitiesAndInvalidRoles(string parameters) =>
        Assert.Throws<ConversationMcpAppProtocolException>(() => ConversationMcpAppProtocol.ReadMessage(Request("ui/message", parameters)));

    [Fact]
    public void RichContextRequiresTheSeparateRichFlag()
    {
        var content = Json("""{"content":[{"type":"image","mimeType":"image/png","data":"AA=="}],"structuredContent":{"value":1}}""");
        Assert.Throws<ConversationMcpAppProtocolException>(() => ConversationMcpAppProtocol.ValidateContextUpdate(content, false));
        ConversationMcpAppProtocol.ValidateContextUpdate(content, true);
    }

    [Fact]
    public void DisplayModesIntersectAppAndResourceWithoutInventingNativePip()
    {
        var view = View() with { DisplayModes = Json("""{"availableDisplayModes":["inline","fullscreen"],"preferredDisplayMode":"fullscreen"}""") };
        var modes = ConversationMcpAppProtocol.NegotiateDisplayModes(view, Json("""{"availableDisplayModes":["fullscreen","pip"]}"""));
        Assert.Equal(new[] { "fullscreen" }, modes.Available);
        Assert.Equal("fullscreen", modes.Initial);
        Assert.Throws<ConversationMcpAppProtocolException>(() => ConversationMcpAppProtocol.NegotiateDisplayModes(
            view, Json("""{"availableDisplayModes":["pip"]}""")));
    }

    [Theory]
    [InlineData(ConversationMcpAppOperation.CallTool, """{"content":[]}""")]
    [InlineData(ConversationMcpAppOperation.ListTools, """{"tools":[]}""")]
    [InlineData(ConversationMcpAppOperation.ListResources, """{"resources":[]}""")]
    [InlineData(ConversationMcpAppOperation.ListResourceTemplates, """{"resourceTemplates":[]}""")]
    [InlineData(ConversationMcpAppOperation.ReadResource, """{"contents":[]}""")]
    public void GatewayCollectionsMustMatchTheMcpResultShape(ConversationMcpAppOperation operation, string valid)
    {
        ConversationMcpAppProtocol.ValidateGatewayResult(operation, Json(valid));
        Assert.Throws<InvalidDataException>(() => ConversationMcpAppProtocol.ValidateGatewayResult(operation, Json("{}")));
        Assert.Throws<InvalidDataException>(() => ConversationMcpAppProtocol.ValidateGatewayResult(operation, Json("[]")));
    }

    [Theory]
    [InlineData("""{"role":"user","content":[{"type":"resource","resource":{"uri":"gateway:one","text":"ok","blob":1}}]}""")]
    [InlineData("""{"role":"user","content":[{"type":"resource","resource":{"uri":"gateway:one","text":1,"blob":"AA=="}}]}""")]
    public void EmbeddedResourcesCannotSmuggleWronglyTypedAlternateBodies(string parameters) =>
        Assert.Throws<ConversationMcpAppProtocolException>(() => ConversationMcpAppProtocol.ReadMessage(Request("ui/message", parameters)));

    [Theory]
    [InlineData("openclaw://plugins/plugin@appstore/app/show?path=%2Freport", "plugin", "appstore", "/report")]
    [InlineData("codex://plugins/plugin%40vendor/app/show", "plugin@vendor", null, "/")]
    [InlineData("https://chatgpt.com/plugins/plugin/app/show", "plugin", null, "/")]
    public void PluginLinksBecomeTypedNativeNavigationNotOsSchemes(string url, string id, string? marketplace, string path)
    {
        var link = ConversationMcpAppProtocol.ReadLink(url);
        Assert.Equal(new(id, marketplace, "show", path), link.Plugin);
    }

    [Theory]
    [InlineData("file:///C:/secret")]
    [InlineData("openclaw://settings")]
    [InlineData("openclaw://plugins/plugin/app/show?ticket=forbidden")]
    [InlineData("openclaw://plugins/plugin/app/show?path=%2F%2Fevil")]
    [InlineData("openclaw://plugins/plugin/app/show?path=%2Freport%23fragment")]
    [InlineData("openclaw://plugins/plugin/app/show?path=%2Freport%5Cfile")]
    [InlineData("https://user:password@example.test")]
    public void NativeOrCredentialLinksOutsideTheNarrowPluginContractAreDenied(string url) =>
        Assert.Throws<ConversationMcpAppProtocolException>(() => ConversationMcpAppProtocol.ReadLink(url));
}
