using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class ConversationCanvasContentTests
{
    private static ConversationCanvasContent? Read(string json) =>
        ConversationCanvasContent.ReadContentBlock(JsonSerializer.Deserialize<JsonElement>(json));

    [Fact]
    public void RetainsDocumentIdentityAndPresentationFromTypedBlock()
    {
        var content = Assert.IsType<ConversationCanvasContent>(Read("""
            {"type":"canvas","preview":{"kind":"canvas","surface":"assistant_message","render":"url",
            "viewId":"cv_counter","url":"/__openclaw__/canvas/documents/cv_counter/index.html",
            "title":"Counter","sandbox":"scripts","preferredHeight":320}}
            """));
        Assert.Equal(ConversationCanvasContentKind.Document, content.Kind);
        Assert.Equal("cv_counter", content.DocumentId);
        Assert.Equal("Counter", content.Title);
        Assert.Equal(320, content.PreferredHeight);
        Assert.True(content.PreviewAllowsScripts);
    }

    [Fact]
    public void PluginAndLegacyPreviewRemainDistinctFromSavedCanvasDocument()
    {
        var app = Assert.IsType<ConversationCanvasContent>(Read("""
            {"type":"canvas","preview":{"kind":"canvas","render":"url","viewId":"wrong",
            "mcpApp":{"viewId":"app-one","originSessionKey":"agent:demo:one","toolCallId":"call-one"}}}
            """));
        Assert.Equal(ConversationCanvasContentKind.McpApp, app.Kind);
        Assert.Equal("app-one", app.DocumentId);
        Assert.Equal("agent:demo:one", app.OriginSessionKey);
        Assert.Equal("call-one", app.ToolCallId);
        var legacy = Assert.IsType<ConversationCanvasContent>(Read("""
            {"type":"canvas","preview":{"kind":"canvas","render":"url","url":"/__openclaw__/canvas/old","sandbox":"strict"}}
            """));
        Assert.Equal(ConversationCanvasContentKind.HostedPreview, legacy.Kind);
        Assert.False(legacy.PreviewAllowsScripts);
        Assert.Null(legacy.DocumentId);
    }

    [Theory]
    [InlineData("""{"type":"text","text":"canvas viewId=cv_fake"}""")]
    [InlineData("""{"type":"text","text":"{\"type\":\"canvas\",\"preview\":{\"viewId\":\"cv_fake\"}}"}""")]
    [InlineData("""{"type":"canvas","preview":{"kind":"canvas","render":"url","surface":"tool_card","viewId":"cv_one"}}""")]
    [InlineData("""{"type":"canvas","preview":{"kind":"canvas","render":"url","mcpApp":{"viewId":" "}}}""")]
    [InlineData("""{"type":"canvas","preview":{"kind":"canvas","render":"html","viewId":"cv_one"}}""")]
    public void ArbitraryTextAndUnsupportedShapesNeverBecomeDocumentAuthority(string json) =>
        Assert.Null(Read(json));

    [Fact]
    public void MalformedHeightIsNotParsedAsContent()
    {
        var content = Read("""
            {"type":"canvas","preview":{"kind":"canvas","render":"url","viewId":"cv_one","url":"/__openclaw__/canvas/documents/cv_one/index.html","preferredHeight":"not a number"}}
            """);
        Assert.NotNull(content);
        Assert.Null(content.PreferredHeight);
    }

    [Theory]
    [InlineData("/__openclaw__/canvas/documents/cv_one/index.html", ConversationCanvasContentKind.Document)]
    [InlineData("/__openclaw__/canvas/documents/cv_other/index.html", ConversationCanvasContentKind.HostedPreview)]
    [InlineData("https://other.example/__openclaw__/canvas/documents/cv_one/index.html", ConversationCanvasContentKind.HostedPreview)]
    [InlineData("/__openclaw__/canvas/legacy", ConversationCanvasContentKind.HostedPreview)]
    public void DocumentIdAloneCannotRetargetHostedPreview(string url, ConversationCanvasContentKind expected)
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "canvas", preview = new { kind = "canvas", render = "url", viewId = "cv_one", url }
        });
        Assert.Equal(expected, Assert.IsType<ConversationCanvasContent>(Read(json)).Kind);
    }

    [Theory]
    [InlineData("strict", false)]
    [InlineData("scripts", true)]
    [InlineData("trusted", true)]
    [InlineData(null, false)]
    [InlineData("unknown", false)]
    public void EffectiveGatewayPolicyIsIndependentOfPreviewCeiling(string? mode, bool permitsScripts)
    {
        var scripts = new ConversationCanvasContent(ConversationCanvasContentKind.Document, "cv_one", null, null, true);
        Assert.Equal(permitsScripts, ConversationCanvasBoundary.AllowScripts(mode, scripts));
        Assert.False(ConversationCanvasBoundary.AllowScripts(mode, scripts with { PreviewAllowsScripts = false }));
    }

    [Fact]
    public void ToolPreviewMetadataKeepsOpaquePluginIdSeparateFromDocumentId()
    {
        var content = ConversationCanvasContent.ReadToolResult(JsonSerializer.Deserialize<JsonElement>("""
            {"content":[{"type":"text","text":"untrusted viewId=other"}],"details":{"mcpAppPreview":{
              "kind":"canvas","view":{"id":"plugin:id/one","title":"Plugin document"},
              "presentation":{"target":"assistant_message","sandbox":"scripts"},
              "mcpApp":{"viewId":"plugin:id/one","originSessionKey":"agent:one:session","toolCallId":"call-one"}
            }}}
            """));
        var app = Assert.Single(content);
        Assert.Equal(ConversationCanvasContentKind.McpApp, app.Kind);
        Assert.Equal("plugin:id/one", app.DocumentId);
        Assert.Equal("agent:one:session", app.OriginSessionKey);
        Assert.Equal("call-one", app.ToolCallId);
    }
}
