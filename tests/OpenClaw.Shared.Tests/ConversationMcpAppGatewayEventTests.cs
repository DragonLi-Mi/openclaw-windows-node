using System.Reflection;
using System.Text.Json;

namespace OpenClaw.Shared.Tests;

public sealed class ConversationMcpAppGatewayEventTests
{
    public class ClientProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            throw new NotSupportedException(method?.Name);
    }

    private static GatewayDataReadScope Scope() =>
        new(DispatchProxy.Create<IOperatorGatewayClient, ClientProxy>(), () => 7);

    [Fact]
    public void ResourceEventKeepsOriginalSourceAndDetachedAllowedFieldsOnly()
    {
        var source = Scope().Capture()!;
        ConversationMcpAppGatewayEventArgs notification;
        using (var document = JsonDocument.Parse(
            """{"viewId":"opaque:view/one","uri":"gateway:report","sessionKey":"untrusted","agentId":"untrusted","runId":"untrusted"}"""))
            notification = ConversationMcpAppGatewayEventArgs.Parse(source,
                ConversationMcpAppGatewayEventKind.ResourceUpdated, document.RootElement);

        Assert.Same(source, notification.Source);
        Assert.Equal("opaque:view/one", notification.ViewId);
        Assert.Equal(ConversationMcpAppGatewayEventKind.ResourceUpdated, notification.Kind);
        Assert.Equal("""{"uri":"gateway:report"}""", notification.Parameters.GetRawText());
        Assert.DoesNotContain("untrusted", JsonSerializer.Serialize(notification));
        Assert.DoesNotContain("Source", JsonSerializer.Serialize(notification));
    }

    [Theory]
    [InlineData("""{"viewId":"one"}""", "{}")]
    [InlineData("""{"viewId":"one","modelContext":null}""", "{}")]
    [InlineData("""{"viewId":"one","modelContext":null,"updateId":12}""", "{}")]
    [InlineData("""{"viewId":"one","modelContext":{"untrusted":"data"},"updateId":"old"}""", "{}")]
    [InlineData("""{"viewId":"one","modelContext":null,"updateId":"old","sessionKey":"untrusted"}""",
        """{"modelContext":null,"updateId":"old"}""")]
    public void HostContextEventMirrorsPinnedRefreshOrExactClearSemantics(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);
        var notification = ConversationMcpAppGatewayEventArgs.Parse(Scope().Capture()!,
            ConversationMcpAppGatewayEventKind.HostContextChanged, document.RootElement);
        Assert.Equal("one", notification.ViewId);
        Assert.Equal(expected, notification.Parameters.GetRawText());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"viewId":null,"uri":"gateway:report"}""")]
    [InlineData("""{"viewId":12,"uri":"gateway:report"}""")]
    [InlineData("""{"viewId":" ","uri":"gateway:report"}""")]
    [InlineData("""{"viewId":"one"}""")]
    [InlineData("""{"viewId":"one","uri":null}""")]
    [InlineData("""{"viewId":"one","uri":12}""")]
    [InlineData("""{"viewId":"one","uri":" "}""")]
    public void MissingOrMalformedRoutingCannotBecomeAResourceNotification(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<JsonException>(() => ConversationMcpAppGatewayEventArgs.Parse(Scope().Capture()!,
            ConversationMcpAppGatewayEventKind.ResourceUpdated, document.RootElement));
    }

    [Fact]
    public void RevisionChangeCannotRetagAnOldNotificationWithTheCurrentSource()
    {
        var scope = Scope();
        var original = scope.Capture()!;
        using var document = JsonDocument.Parse("""{"viewId":"one"}""");
        var notification = ConversationMcpAppGatewayEventArgs.Parse(original,
            ConversationMcpAppGatewayEventKind.HostContextChanged, document.RootElement);
        scope.Invalidate();
        var current = scope.Capture()!;

        Assert.False(notification.Source.IsCurrent);
        Assert.Same(original, notification.Source);
        Assert.NotSame(current, notification.Source);
        Assert.Throws<GatewayDataReadRetiredException>(() => ConversationMcpAppGatewayEventArgs.Parse(original,
            ConversationMcpAppGatewayEventKind.HostContextChanged, document.RootElement));
        Assert.True(current.IsCurrent);
    }

    [Fact]
    public void MissingSourceCannotBecomeAnAvailableNotification()
    {
        using var document = JsonDocument.Parse("""{"viewId":"one"}""");
        Assert.Throws<ArgumentNullException>(() => ConversationMcpAppGatewayEventArgs.Parse(null!,
            ConversationMcpAppGatewayEventKind.HostContextChanged, document.RootElement));
    }
}
