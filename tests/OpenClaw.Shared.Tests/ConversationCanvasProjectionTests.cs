using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

public sealed class ConversationCanvasProjectionTests
{
    private static ChatHistoryInfo History(string json)
    {
        using var document = JsonDocument.Parse(json);
        var parser = typeof(OpenClawGatewayClient).GetMethod("ParseChatHistory", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (ChatHistoryInfo)parser.Invoke(null, [document.RootElement, "agent:research:one"])!;
    }

    [Fact]
    public void CanvasOnlyHistoryMessageIsNotDiscardedAsEmptyText()
    {
        var history = History("""
            {"messages":[{"role":"assistant","__openclaw":{"id":"message-one"},"content":[
              {"type":"canvas","preview":{"kind":"canvas","render":"url","surface":"assistant_message",
              "viewId":"cv_one","url":"/__openclaw__/canvas/documents/cv_one/index.html"}}
            ]}]}
            """);
        var message = Assert.Single(history.Messages);
        Assert.Equal("message-one", message.OpenClawId);
        Assert.Equal("agent:research:one", message.SessionKey);
        Assert.Equal(string.Empty, message.Text);
        var part = Assert.Single(message.ContentParts);
        Assert.Equal(ChatMessageContentPartKind.Canvas, part.Kind);
        Assert.Equal("cv_one", part.Canvas?.DocumentId);
        Assert.Null(part.Media);
    }

    [Fact]
    public void TypedCanvasPreservesSourceOrderWithoutParsingVisibleText()
    {
        var message = Assert.Single(History("""
            {"messages":[{"role":"assistant","content":[
              {"type":"text","text":"Before viewId=cv_fake"},
              {"type":"canvas","preview":{"kind":"canvas","render":"url","viewId":"cv_owned",
                "url":"/__openclaw__/canvas/documents/cv_owned/index.html"}},
              {"type":"text","text":"After"}
            ]}]}
            """).Messages);
        Assert.Collection(message.ContentParts,
            part => Assert.Equal("Before viewId=cv_fake", part.Text),
            part => Assert.Equal("cv_owned", part.Canvas?.DocumentId),
            part => Assert.Equal("After", part.Text));
    }

    [Fact]
    public void HistoricalPluginToolResultRetainsOnlyStructuredOwnerMetadata()
    {
        var message = Assert.Single(History("""
            {"messages":[{"role":"toolResult","toolCallId":"call-one","toolName":"demo__show",
              "content":[{"type":"text","text":"result"}],"details":{"mcpAppPreview":{
                "kind":"canvas","view":{"id":"app-one","title":"Demo"},
                "presentation":{"target":"assistant_message","sandbox":"scripts"},
                "mcpApp":{"viewId":"app-one","originSessionKey":"agent:research:one","toolCallId":"call-one"}
              }}}]}
            """).Messages);
        var tool = Assert.Single(message.ToolContent);
        Assert.Equal("call-one", tool.CallId);
        Assert.Equal("app-one", Assert.Single(tool.CanvasContent).DocumentId);
        Assert.Equal("agent:research:one", Assert.Single(tool.CanvasContent).OriginSessionKey);
    }

    [Fact]
    public void LiveCanvasOnlyFrameRetainsExactSessionRunAndMessage()
    {
        using var identity = new TempDirectory();
        using var client = new OpenClawGatewayClient("ws://localhost:18789", "synthetic-test-token",
            identityPath: identity.Path);
        ChatMessageInfo? received = null;
        client.ChatMessageReceived += (_, message) => received = message;
        var parser = typeof(OpenClawGatewayClient).GetMethod("ProcessMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        parser.Invoke(client, ["""
            {"type":"event","event":"chat","payload":{"sessionKey":"agent:research:one","runId":"run-one","state":"final",
              "message":{"role":"assistant","__openclaw":{"id":"message-one"},"content":[
                {"type":"canvas","preview":{"kind":"canvas","render":"url","viewId":"cv_one",
                  "url":"/__openclaw__/canvas/documents/cv_one/index.html"}}
              ]}}}
            """]);
        Assert.NotNull(received);
        Assert.Equal("agent:research:one", received.SessionKey);
        Assert.Equal("run-one", received.RunId);
        Assert.Equal("message-one", received.OpenClawId);
        Assert.Equal("cv_one", Assert.Single(received.ContentParts).Canvas?.DocumentId);
    }
}
