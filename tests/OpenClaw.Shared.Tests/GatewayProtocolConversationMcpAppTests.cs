using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayProtocolLiveRoundTripTests
{
    [Fact]
    public async Task ConversationMcpApp_ViewUsesTheAuthenticatedSessionViewAndAgent()
    {
        using var server = new LoopbackGatewayServer();
        server.OnMethod("connect", _ => new
        {
            type = "hello-ok", protocol = GatewayProtocolContract.CurrentVersion,
            features = new { methods = new[] { "mcp.app.view" } }
        });
        server.OnMethod("mcp.app.view", parameters =>
        {
            Assert.Equal("agent:research:one", parameters.GetProperty("sessionKey").GetString());
            Assert.Equal("opaque:view/one", parameters.GetProperty("viewId").GetString());
            Assert.Equal("research", parameters.GetProperty("agentId").GetString());
            return new
            {
                html = "<main>authenticated App</main>", sandboxUrl = "/mcp-app-sandbox?v=fixture",
                sandboxPort = 18790, messageSupported = true, fileResourcesSupported = false,
                toolInput = new { query = "selected input" },
                toolResult = new { content = new[] { new { type = "text", text = "selected result" } } }
            };
        });
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server, configureHandshake: false);
        var view = await client.ViewConversationMcpAppAsync(new("agent:research:one", "opaque:view/one", "research"),
            client.SessionMutationConnectionEpoch!.Value);
        Assert.True(view.MessageSupported);
        Assert.False(view.FileResourcesSupported);
        Assert.Equal("selected input", view.ToolInput!.Value.GetProperty("query").GetString());
        Assert.False(server.HasFrame("canvas.document.view"));
        await client.DisconnectAsync();
    }

    [Theory]
    [InlineData(ConversationMcpAppOperation.CallTool, "mcp.app.callTool", """{"toolName":"show","arguments":{"sessionKey":"tool data"}}""", """{"content":[{"type":"text","text":"result"}]}""")]
    [InlineData(ConversationMcpAppOperation.ListTools, "mcp.app.listTools", """{"cursor":"next"}""", """{"tools":[]}""")]
    [InlineData(ConversationMcpAppOperation.ListResources, "mcp.app.listResources", "{}", """{"resources":[]}""")]
    [InlineData(ConversationMcpAppOperation.ListResourceTemplates, "mcp.app.listResourceTemplates", "{}", """{"resourceTemplates":[]}""")]
    [InlineData(ConversationMcpAppOperation.ReadResource, "mcp.app.readResource", """{"uri":"openclaw-file://owned","_meta":{"writable":true}}""", """{"contents":[]}""")]
    [InlineData(ConversationMcpAppOperation.WriteResource, "mcp.app.writeResource", """{"uri":"openclaw-file://owned","text":"changed","ifMatch":"original-hash"}""", """{"outcome":"saved","etag":"new-hash"}""")]
    [InlineData(ConversationMcpAppOperation.SubscribeResource, "mcp.app.subscribeResource", """{"uri":"openclaw-file://owned"}""", "{}")]
    [InlineData(ConversationMcpAppOperation.UnsubscribeResource, "mcp.app.unsubscribeResource", """{"uri":"openclaw-file://owned"}""", "{}")]
    [InlineData(ConversationMcpAppOperation.OpenFile, "mcp.app.openFile", """{"path":"notes.txt"}""", """{"path":"notes.txt","name":"notes.txt"}""")]
    [InlineData(ConversationMcpAppOperation.GetModelContext, "mcp.app.modelContext", "{}", """{"state":null}""")]
    [InlineData(ConversationMcpAppOperation.UpdateModelContext, "mcp.app.updateModelContext", """{"content":[{"type":"text","text":"context"}]}""", "{}")]
    [InlineData(ConversationMcpAppOperation.RemoveModelContext, "mcp.app.removeModelContext", """{"updateId":"update-one","index":0}""", """{"state":null}""")]
    public async Task ConversationMcpApp_OperationsOverwriteUntrustedRouting(
        ConversationMcpAppOperation operation, string method, string json, string response)
    {
        using var server = new LoopbackGatewayServer();
        server.OnMethod("connect", _ => new
        {
            type = "hello-ok", protocol = GatewayProtocolContract.CurrentVersion,
            features = new { methods = new[] { method } }
        });
        server.OnMethod(method, parameters =>
        {
            Assert.Equal("agent:research:one", parameters.GetProperty("sessionKey").GetString());
            Assert.Equal("opaque:view/one", parameters.GetProperty("viewId").GetString());
            Assert.Equal("research", parameters.GetProperty("agentId").GetString());
            return JsonSerializer.Deserialize<JsonElement>(response);
        });
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server, configureHandshake: false);
        var parameters = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        parameters["sessionKey"] = "attacker-session";
        parameters["viewId"] = "attacker-view";
        parameters["agentId"] = "attacker-agent";
        var result = await client.InvokeConversationMcpAppAsync(
            new("agent:research:one", "opaque:view/one", "research"), operation,
            JsonSerializer.SerializeToElement(parameters), client.SessionMutationConnectionEpoch!.Value);
        Assert.Equal(JsonSerializer.Deserialize<JsonElement>(response).GetRawText(), result.GetRawText());
        Assert.False(server.HasFrame("chat.send"));
        await client.DisconnectAsync();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("unsupported")]
    [InlineData("stale")]
    [InlineData("cancelled")]
    public async Task ConversationMcpApp_UnavailableOrSupersededWorkNeverReachesWire(string reason)
    {
        using var server = new LoopbackGatewayServer();
        server.OnMethod("connect", _ => new
        {
            type = "hello-ok", protocol = GatewayProtocolContract.CurrentVersion,
            features = new { methods = reason == "unknown" ? null :
                reason == "unsupported" ? Array.Empty<string>() : new[] { "mcp.app.view" } }
        });
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server, configureHandshake: false);
        using var cancellation = new CancellationTokenSource();
        if (reason == "cancelled") cancellation.Cancel();
        var epoch = client.SessionMutationConnectionEpoch!.Value + (reason == "stale" ? 1 : 0);
        var scope = new ConversationMcpAppScope("agent:research:one", "opaque:view/one");
        if (reason == "unknown")
            await Assert.ThrowsAsync<ConversationMcpAppPendingException>(() => client.ViewConversationMcpAppAsync(scope, epoch));
        else if (reason == "unsupported")
            await Assert.ThrowsAsync<NotSupportedException>(() => client.ViewConversationMcpAppAsync(scope, epoch));
        else if (reason == "stale")
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.ViewConversationMcpAppAsync(scope, epoch));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ViewConversationMcpAppAsync(scope, epoch, cancellation.Token));
        Assert.False(server.HasFrame("mcp.app.view"));
        await client.DisconnectAsync();
    }

    [Fact]
    public async Task ConversationMcpApp_CancelledWriteMayAlreadyHaveExecutedAndIsNeverRetried()
    {
        using var server = new LoopbackGatewayServer();
        using var cancellation = new CancellationTokenSource();
        var executions = 0;
        server.OnMethod("connect", _ => new
        {
            type = "hello-ok", protocol = GatewayProtocolContract.CurrentVersion,
            features = new { methods = new[] { "mcp.app.callTool" } }
        });
        server.OnMethod("mcp.app.callTool", _ =>
        {
            Interlocked.Increment(ref executions);
            cancellation.Cancel();
            return new { content = new[] { new { type = "text", text = "executed" } } };
        });
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server, configureHandshake: false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.InvokeConversationMcpAppAsync(
            new("agent:research:one", "opaque:view/one", "research"), ConversationMcpAppOperation.CallTool,
            JsonSerializer.Deserialize<JsonElement>("""{"toolName":"change","arguments":{}}"""),
            client.SessionMutationConnectionEpoch!.Value, cancellation.Token));
        Assert.Equal(1, Volatile.Read(ref executions));
        await client.DisconnectAsync();
    }

    [Fact]
    public async Task ConversationMcpApp_ViewExpiryRetainsTheGatewayDetailCode()
    {
        using var server = new LoopbackGatewayServer();
        server.OnMethod("connect", _ => new
        {
            type = "hello-ok", protocol = GatewayProtocolContract.CurrentVersion,
            features = new { methods = new[] { "mcp.app.view" } }
        });
        server.OnMethod("mcp.app.view", _ => new LoopbackResponse(false, Error: new
        {
            code = "UNAVAILABLE", message = "MCP App view expired.",
            details = new { code = "MCP_APP_VIEW_EXPIRED" }
        }));
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server, configureHandshake: false);
        var error = await Assert.ThrowsAsync<GatewayRequestException>(() => client.ViewConversationMcpAppAsync(
            new("agent:research:one", "opaque:view/one", "research"), client.SessionMutationConnectionEpoch!.Value));
        Assert.Equal("UNAVAILABLE", error.Code);
        Assert.Equal("MCP_APP_VIEW_EXPIRED", error.Details!.Value.GetProperty("code").GetString());
        await client.DisconnectAsync();
    }
}
