using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class ConversationCanvasBoundaryTests
{
    private const string Nonce = "a9cdf70c461b4b52a2fdfd788f478e95";
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static CanvasDocumentView Document(string origin = "https://gateway.example", string sandbox = "https://sandbox.example/mcp-app-sandbox?v=abc") =>
        new("<p>document</p>", new Uri(origin), new Uri(sandbox));

    [Theory]
    [InlineData("https://gateway.example", "https://gateway.example:443/mcp-app-sandbox")]
    [InlineData("http://localhost:18789", "http://localhost:18789/mcp-app-sandbox")]
    [InlineData("http://[::1]:18789", "http://[::1]:18789/mcp-app-sandbox")]
    public void SameOriginSandboxIsRejectedIncludingDefaultPorts(string gateway, string sandbox) =>
        Assert.Throws<InvalidDataException>(() => new ConversationCanvasBoundary(Document(gateway, sandbox), Nonce));

    [Theory]
    [InlineData("http://localhost:18789", "http://127.0.0.1:18789/mcp-app-sandbox")]
    [InlineData("http://[::1]:18789", "http://[::1]:18790/mcp-app-sandbox")]
    [InlineData("https://gateway.example:443", "https://sandbox.example:443/mcp-app-sandbox")]
    public void OriginComparisonUsesBrowserSchemeHostPortNotDns(string gateway, string sandbox)
    {
        var boundary = new ConversationCanvasBoundary(Document(gateway, sandbox), Nonce);
        Assert.False(ConversationCanvasBoundary.SameOrigin(boundary.GatewayOrigin, boundary.SandboxUrl));
    }

    [Theory]
    [InlineData("https://user:password@sandbox.example/mcp-app-sandbox")]
    [InlineData("https://sandbox.example/mcp-app-sandbox/child")]
    [InlineData("https://sandbox.example/mcp-app-sandbox?token=secret")]
    [InlineData("https://sandbox.example/mcp-app-sandbox?access_token=secret")]
    [InlineData("https://sandbox.example/mcp-app-sandbox?v=one&v=two")]
    [InlineData("https://sandbox.example/mcp-app-sandbox#token")]
    [InlineData("file:///C:/private.html")]
    public void InvalidPublicShellIsRejected(string sandbox) =>
        Assert.Throws<InvalidDataException>(() => new ConversationCanvasBoundary(Document(sandbox: sandbox), Nonce));

    [Fact]
    public void ParentInterceptionAdmitsOnlyOneExactPendingGet()
    {
        var boundary = new ConversationCanvasBoundary(Document(), Nonce);
        Assert.Equal($"https://gateway.example/__companion_canvas__/{Nonce}/index.html", boundary.ParentUrl.AbsoluteUri);
        Assert.True(boundary.MayServeParent(boundary.ParentUrl.AbsoluteUri, "GET", true));
        Assert.False(boundary.MayServeParent(boundary.ParentUrl.AbsoluteUri, "POST", true));
        Assert.False(boundary.MayServeParent(boundary.ParentUrl.AbsoluteUri, "GET", false));
        Assert.False(boundary.MayServeParent("https://gateway.example/", "GET", true));
        Assert.False(boundary.MayServeParent(boundary.ParentUrl.AbsoluteUri + "?token=x", "GET", true));
        Assert.False(boundary.MayServeParent(boundary.ParentUrl.AbsoluteUri.Replace(Nonce, new string('b', 32)), "GET", true));
    }

    [Fact]
    public void SandboxReplyDerivesPortButNeverRetargetsAnExplicitOrigin()
    {
        var derived = ConversationCanvasBoundary.ParseDocument(Json("""
            {"html":"<p>owned</p>","sandboxUrl":"/mcp-app-sandbox?v=a","sandboxPort":18790}
            """), "ws://127.0.0.1:18789/control?ignored=true");
        Assert.Equal("http://127.0.0.1:18789/", derived.GatewayOrigin.AbsoluteUri);
        Assert.Equal("http://127.0.0.1:18790/mcp-app-sandbox?v=a", derived.SandboxUrl.AbsoluteUri);
        var remote = ConversationCanvasBoundary.ParseDocument(Json("""
            {"html":"remote","sandboxUrl":"/mcp-app-sandbox?csp=abc&v=def","sandboxPort":1234,"sandboxOrigin":"https://sandbox.example"}
            """), "wss://gateway.example/control");
        Assert.Equal("https://sandbox.example/mcp-app-sandbox?csp=abc&v=def", remote.SandboxUrl.AbsoluteUri);
        Assert.Throws<InvalidDataException>(() => ConversationCanvasBoundary.ParseDocument(Json("""
            {"html":"x","sandboxUrl":"https://other.example/mcp-app-sandbox","sandboxPort":1234,"sandboxOrigin":"https://sandbox.example"}
            """), "wss://gateway.example"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void InvalidSandboxPortFailsClosed(int port) =>
        Assert.Throws<InvalidDataException>(() => ConversationCanvasBoundary.ParseDocument(
            Json($$"""{"html":"x","sandboxUrl":"/mcp-app-sandbox","sandboxPort":{{port}}}"""), "ws://localhost:18789"));

    [Theory]
    [InlineData("strict", "strict")]
    [InlineData("scripts", "scripts")]
    [InlineData("trusted", "trusted")]
    [InlineData("unknown", null)]
    public void EffectivePolicyIsReadOnlyFromAuthenticatedDocumentResult(string mode, string? expected)
    {
        var document = ConversationCanvasBoundary.ParseDocument(JsonSerializer.SerializeToElement(new
        {
            html = "<p>owned document</p>", sandboxUrl = "/mcp-app-sandbox", sandboxPort = 18790, embedSandbox = mode
        }), "ws://localhost:18789");
        Assert.Equal(expected, document.EmbedSandboxMode);
    }

    [Fact]
    public void DocumentSizeUsesUtf8BytesNotCharacterCount()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            html = new string('\u4e00', ConversationCanvasBoundary.MaximumHtmlUtf8Bytes / 3 + 1),
            sandboxUrl = "/mcp-app-sandbox",
            sandboxPort = 18790
        });
        Assert.Throws<InvalidDataException>(() => ConversationCanvasBoundary.ParseDocument(json, "ws://localhost:18789"));
    }

    [Fact]
    public void OnlyExactNativeSourceAndNonceAreAdmitted()
    {
        var boundary = new ConversationCanvasBoundary(Document(), Nonce);
        var message = Json($$"""{"nonce":"{{Nonce}}","type":"prompt","text":"hello"}""");
        Assert.True(boundary.IsNativeMessage(boundary.ParentUrl.AbsoluteUri, message));
        Assert.False(boundary.IsNativeMessage(boundary.SandboxUrl.AbsoluteUri, message));
        Assert.False(boundary.IsNativeMessage(boundary.ParentUrl.AbsoluteUri + "#forged", message));
        Assert.False(boundary.IsNativeMessage(boundary.ParentUrl.AbsoluteUri, Json("""{"nonce":"old"}""")));
    }

    [Theory]
    [InlineData("/reset")]
    [InlineData(" !run")]
    [InlineData("hello\u202eevil")]
    [InlineData("hello\u0000")]
    [InlineData(" ")]
    public void PromptRejectsCommandsControlsAndEmptyText(string text) =>
        Assert.Null(ConversationCanvasBoundary.ConversationalPrompt(text));

    [Fact]
    public void PromptLimitIsExactAndMultilineIsPreserved()
    {
        Assert.Equal(new string('a', 4000), ConversationCanvasBoundary.ConversationalPrompt(new string('a', 4000)));
        Assert.Null(ConversationCanvasBoundary.ConversationalPrompt(new string('a', 4001)));
        Assert.Equal("first\nsecond", ConversationCanvasBoundary.ConversationalPrompt(" first\nsecond "));
    }

    [Fact]
    public void TrustedWrapperNeverContainsAuthoredHtmlOrAddsGeneralBridge()
    {
        var document = Document();
        var boundary = new ConversationCanvasBoundary(document, Nonce);
        var html = ConversationCanvasHostDocument.Build(boundary, Nonce, "</script>title", "render-one");
        Assert.DoesNotContain(document.Html, html);
        Assert.DoesNotContain("</script>title", html);
        Assert.Contains("event.source !== frame.contentWindow", html);
        Assert.Contains("event.origin !== config.sandboxOrigin", html);
        Assert.Contains("}, config.sandboxOrigin)", html);
        Assert.Contains("allow-scripts allow-same-origin allow-forms", html);
        Assert.Contains("port.close()", html);
        var csp = ConversationCanvasHostDocument.ContentSecurityPolicy(boundary, Nonce);
        Assert.Contains("connect-src 'none'", csp);
        Assert.Contains("base-uri 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.DoesNotContain("'unsafe-inline'", csp);
    }

    [Fact]
    public void TrustedGatewayModeNeverWidensTheTransportFrameOrAddsNativeGrants()
    {
        var document = Document() with { EmbedSandboxMode = "trusted" };
        var content = new ConversationCanvasContent(ConversationCanvasContentKind.Document, "cv_one", null, null, true);
        Assert.True(ConversationCanvasBoundary.AllowScripts(document.EmbedSandboxMode, content));
        var boundary = new ConversationCanvasBoundary(document, Nonce);
        var html = ConversationCanvasHostDocument.Build(boundary, Nonce, "Canvas", "render-one");
        Assert.Contains("""frame.setAttribute("sandbox", "allow-scripts allow-same-origin allow-forms")""", html);
        Assert.DoesNotContain("allow-popups", html);
        Assert.DoesNotContain("allow-downloads", html);
        Assert.DoesNotContain("allow-top-navigation", html);
        Assert.DoesNotContain("AddHostObject", html);
    }
}
