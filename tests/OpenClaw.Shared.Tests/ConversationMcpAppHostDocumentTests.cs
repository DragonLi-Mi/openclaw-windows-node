using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

/// <summary>Concrete transport bans, not a rendered WebView or JavaScript execution assertion.</summary>
public sealed class ConversationMcpAppHostDocumentTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private const string ScriptNonce = "fedcba9876543210fedcba9876543210";
    private static ConversationCanvasBoundary Boundary() => new(
        new("", new("https://gateway.example"), new("https://sandbox.example/mcp-app-sandbox?v=1")), Nonce);

    [Fact]
    public void ConfigurationEscapesAuthoredLabelsAndPinsTheExactProxySourceOriginAndRender()
    {
        var html = ConversationMcpAppHostDocument.Build(Boundary(), Nonce, "</script><script>attack()</script>",
            "render-one", ScriptNonce, "dark");
        Assert.DoesNotContain("<script>attack()", html);
        Assert.Contains("""event.source !== frame.contentWindow || event.origin !== config.sandboxOrigin""", html);
        Assert.Contains("data?.nonce !== config.nonce || data?.renderId !== config.renderId", html);
        Assert.Contains("data.params?.renderId === config.renderId", html);
        Assert.Contains("data.params?.sandboxUrl === config.sandboxUrl", html);
        Assert.Contains($"""<script nonce="{ScriptNonce}">""", html);
        Assert.DoesNotContain($"""<script nonce="{Nonce}">""", html);
    }

    [Fact]
    public void AppTransportBoundsPacketsBeforeIpcAndNeverAcceptsCanvasPrivatePortsOrNativeAuthority()
    {
        var html = ConversationMcpAppHostDocument.Build(Boundary(), Nonce, "App", "render", ScriptNonce, "light");
        Assert.Contains("serialized.length > config.maximumPacketCharacters", html);
        Assert.True(html.IndexOf("serialized.length >", StringComparison.Ordinal) <
            html.IndexOf("""send("rpc", { packet: data })""", StringComparison.Ordinal));
        Assert.Contains("for (const port of event.ports) port.close()", html);
        Assert.DoesNotContain("port.onmessage", html);
        Assert.DoesNotContain("promptPort", html);
        Assert.DoesNotContain("allowScripts:", html);
        Assert.DoesNotContain("localStorage", html);
        Assert.DoesNotContain("fetch(", html);
        Assert.DoesNotContain("window.open", html);
        Assert.DoesNotContain("allow-popups", html);
        Assert.DoesNotContain("allow-downloads", html);
    }

    [Fact]
    public void SharedBoundaryRevalidatesAppAddressesWithoutGrantingScriptOrBridgePermissions()
    {
        var boundary = Boundary();
        Assert.False(boundary.IsParent(boundary.ParentUrl.AbsoluteUri + "?token=forbidden"));
        Assert.False(boundary.IsNativeMessage(boundary.SandboxUrl.AbsoluteUri,
            JsonSerializer.SerializeToElement(new { nonce = Nonce })));
        Assert.False(boundary.MayNavigateFrame("file:///C:/secret"));
        Assert.Throws<InvalidDataException>(() => new ConversationCanvasBoundary(new("",
            new("https://gateway.example"), new("https://sandbox.example/mcp-app-sandbox?ticket=forbidden")), Nonce));
    }
}
