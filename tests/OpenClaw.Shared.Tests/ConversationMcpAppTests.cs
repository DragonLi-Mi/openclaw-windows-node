using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class ConversationMcpAppTests
{
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);

    [Fact]
    public void ViewPreservesSdkDataAndNegotiatedCapabilitiesWithoutBorrowingCanvasPolicy()
    {
        var payload = Json("""
            {"html":"<main>App document</main>","sandboxUrl":"/mcp-app-sandbox?csp=abc&v=def",
             "sandboxPort":18790,"sandboxOrigin":"https://apps.example","embedSandbox":"strict",
             "csp":{"connectDomains":["https://api.example"],"resourceDomains":["https://static.example"]},
             "toolInput":{"query":"selected source"},"toolResult":{"content":[{"type":"text","text":"result"}],"_meta":{"kept":true}},
             "messageSupported":true,"updateModelContextSupported":true,"richModelContextSupported":true,
             "fileResourcesSupported":true,"openFilesSupported":true,
             "hostContext":{"openai/modelContext":{"updateId":"update-one","content":[{"type":"text","text":"context"}]}},
             "displayMode":"inline","displayModes":{"availableDisplayModes":["inline","fullscreen"]}}
            """);
        var view = ConversationMcpAppView.Parse(payload, "wss://gateway.example/ws");
        Assert.Equal("https://apps.example/mcp-app-sandbox?csp=abc&v=def", view.SandboxUrl.AbsoluteUri);
        Assert.Equal("selected source", view.ToolInput!.Value.GetProperty("query").GetString());
        Assert.True(view.ToolResult!.Value.GetProperty("_meta").GetProperty("kept").GetBoolean());
        Assert.Equal("update-one", view.HostContext!.Value.GetProperty("openai/modelContext").GetProperty("updateId").GetString());
        Assert.Equal(2, view.DisplayModes!.Value.GetProperty("availableDisplayModes").GetArrayLength());
        Assert.True(view.MessageSupported);
        Assert.True(view.UpdateModelContextSupported);
        Assert.True(view.RichModelContextSupported);
        Assert.True(view.FileResourcesSupported);
        Assert.True(view.OpenFilesSupported);
    }

    [Fact]
    public void MissingCapabilitiesDoNotGrantInteractiveOperations()
    {
        var view = ConversationMcpAppView.Parse(Json("""
            {"html":"read-only restored document","sandboxUrl":"/mcp-app-sandbox","sandboxPort":18790}
            """), "ws://localhost:18789");
        Assert.False(view.MessageSupported);
        Assert.False(view.UpdateModelContextSupported);
        Assert.False(view.RichModelContextSupported);
        Assert.False(view.FileResourcesSupported);
        Assert.False(view.OpenFilesSupported);
        Assert.Null(view.ToolResult);
    }

    [Theory]
    [InlineData("messageSupported", "\"yes\"")]
    [InlineData("openFilesSupported", "null")]
    [InlineData("csp", "[]")]
    [InlineData("toolResult", "\"result text is not a tool result\"")]
    [InlineData("displayMode", "\"native-window\"")]
    public void MalformedKnownFieldsAreNotSuccessfulDefaults(string name, string value) =>
        Assert.Throws<InvalidDataException>(() => ConversationMcpAppView.Parse(Json($$"""
            {"html":"document","sandboxUrl":"/mcp-app-sandbox","sandboxPort":18790,"{{name}}":{{value}}}
            """), "ws://localhost:18789"));

    [Theory]
    [InlineData(ConversationMcpAppOperation.CallTool, """{"toolName":"show","arguments":[]}""")]
    [InlineData(ConversationMcpAppOperation.CallTool, """{"toolName":" "}""")]
    [InlineData(ConversationMcpAppOperation.WriteResource, """{"uri":"openclaw-file://owned","text":"a","blob":"Yg=="}""")]
    [InlineData(ConversationMcpAppOperation.WriteResource, """{"uri":"openclaw-file://owned"}""")]
    [InlineData(ConversationMcpAppOperation.WriteResource, """{"uri":"openclaw-file://owned","text":"a","ifMatch":""}""")]
    [InlineData(ConversationMcpAppOperation.ReadResource, """{"uri":12}""")]
    [InlineData(ConversationMcpAppOperation.OpenFile, """{"path":null}""")]
    [InlineData(ConversationMcpAppOperation.UpdateModelContext, """{"content":"not a content array"}""")]
    public void OperationShapesAreValidatedBeforeDispatch(ConversationMcpAppOperation operation, string parameters) =>
        Assert.Throws<ArgumentException>(() => ConversationMcpAppRequests.Parameters(
            new("agent:research:one", "opaque:view/one", "research"), operation, Json(parameters)));

    [Fact]
    public void CapturedOwnerOverwritesTopLevelRoutingButPreservesToolArgumentsAndMetadata()
    {
        var result = ConversationMcpAppRequests.Parameters(
            new("agent:research:one", "opaque:view/one"), ConversationMcpAppOperation.CallTool, Json("""
            {"sessionKey":"other-session","viewId":"other-view","agentId":"other-agent","toolName":"show",
             "arguments":{"sessionKey":"literal tool input"},"_meta":{"extension":{"kept":true}}}
            """));
        Assert.Equal("agent:research:one", result["sessionKey"]);
        Assert.Equal("opaque:view/one", result["viewId"]);
        Assert.False(result.ContainsKey("agentId"));
        Assert.Equal("literal tool input", ((JsonElement)result["arguments"]!).GetProperty("sessionKey").GetString());
        Assert.True(((JsonElement)result["_meta"]!).GetProperty("extension").GetProperty("kept").GetBoolean());
    }

    [Fact]
    public void UnknownOperationCannotSelectArbitraryGatewayRpc() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ConversationMcpAppRequests.Method((ConversationMcpAppOperation)int.MaxValue));
}
