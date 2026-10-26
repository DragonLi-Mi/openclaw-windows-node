using OpenClawTray.Helpers;

namespace OpenClaw.Tray.Tests;

public sealed class GatewayDashboardUrlBuilderTests
{
    [Theory]
    [InlineData("ws://localhost:4317", "http://localhost:4317/settings/updates")]
    [InlineData("wss://gateway.example/", "https://gateway.example/settings/updates")]
    [InlineData("wss://gateway.example/openclaw/", "https://gateway.example/openclaw/settings/updates")]
    public void Build_UpdatesPreservesSelectedGatewayAndBasePath(string gatewayUrl, string expected)
    {
        var url = GatewayDashboardUrlBuilder.Build(
            gatewayUrl, GatewayDashboardUrlBuilder.UpdatesPath, null, appendSharedGatewayToken: false);

        Assert.Equal(expected, url);
    }

    [Fact]
    public void Build_UpdatesKeepsSharedTokenInFragment()
    {
        var url = GatewayDashboardUrlBuilder.Build(
            "wss://gateway.example/openclaw", GatewayDashboardUrlBuilder.UpdatesPath,
            "shared token&value", appendSharedGatewayToken: true);

        Assert.Equal(
            "https://gateway.example/openclaw/settings/updates#token=shared%20token%26value", url);
    }

    [Theory]
    [InlineData("device-token")]
    [InlineData("bootstrap-token")]
    public void Build_UpdatesDoesNotExposeDeviceOrBootstrapToken(string token)
    {
        var url = GatewayDashboardUrlBuilder.Build(
            "ws://localhost:4317", GatewayDashboardUrlBuilder.UpdatesPath, token,
            appendSharedGatewayToken: false);

        Assert.Equal("http://localhost:4317/settings/updates", url);
        Assert.DoesNotContain(token, url);
    }

    [Fact]
    public void Build_AppendsSharedTokenToDashboardRoot()
    {
        var url = GatewayDashboardUrlBuilder.Build("ws://localhost:4317", null, "shared token", appendSharedGatewayToken: true);

        Assert.Equal("http://localhost:4317#token=shared%20token", url);
    }

    [Fact]
    public void Build_AppendsSharedTokenToDashboardPath()
    {
        var url = GatewayDashboardUrlBuilder.Build("wss://gateway.example/", "/sessions/abc", "shared", appendSharedGatewayToken: true);

        Assert.Equal("https://gateway.example/sessions/abc#token=shared", url);
    }

    [Fact]
    public void Build_DoesNotAppendNonSharedToken()
    {
        var url = GatewayDashboardUrlBuilder.Build("ws://localhost:4317", "config", "device-token", appendSharedGatewayToken: false);

        Assert.Equal("http://localhost:4317/config", url);
    }
}
