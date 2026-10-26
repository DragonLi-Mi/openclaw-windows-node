namespace OpenClawTray.Helpers;

public static class GatewayDashboardUrlBuilder
{
    // The Gateway Control UI owns update confirmation, authorization, progress,
    // and restart reconciliation. Native clients only open that flow.
    public const string UpdatesPath = "settings/updates";

    public static string Build(
        string gatewayUrl,
        string? path,
        string? sharedGatewayToken,
        bool appendSharedGatewayToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayUrl);

        var baseUrl = gatewayUrl
            .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
            .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        var url = string.IsNullOrWhiteSpace(path)
            ? baseUrl
            : $"{baseUrl}/{path.TrimStart('/')}";

        if (appendSharedGatewayToken && !string.IsNullOrEmpty(sharedGatewayToken))
        {
            var separator = url.Contains('#') ? "&" : "#";
            url = $"{url}{separator}token={Uri.EscapeDataString(sharedGatewayToken)}";
        }

        return url;
    }
}
