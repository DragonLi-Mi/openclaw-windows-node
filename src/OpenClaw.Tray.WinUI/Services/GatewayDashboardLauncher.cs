using OpenClaw.Connection;
using OpenClawTray.Helpers;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenClawTray.Services;

internal enum GatewayDashboardLaunchOutcome
{
    Opened, OpenedSignInMayBeRequired, NoGateway, TunnelUnavailable, CredentialUnavailable, BrowserUnavailable
}

/// <summary>Uses the normal Dashboard credential path without setup completion authority.</summary>
internal sealed class GatewayDashboardLauncher(
    Func<bool> ensureTunnel,
    Func<InteractiveGatewayCredential?> resolveCredential,
    Func<string, Task<bool>> launchBrowser,
    Action<GatewayDashboardLaunchOutcome> reportFailure,
    Action? reportOpened = null,
    Func<bool>? hasGateway = null)
{
    internal const string FailureNotificationId = "setup-dashboard-launch";
    public async Task<GatewayDashboardLaunchOutcome> OpenAsync(string? path = null)
    {
        var failure = GatewayDashboardLaunchOutcome.NoGateway;
        var opened = GatewayDashboardLaunchOutcome.Opened;
        try
        {
            if (hasGateway?.Invoke() == false)
                throw new InvalidOperationException("No Gateway selected.");
            failure = GatewayDashboardLaunchOutcome.TunnelUnavailable;
            if (!ensureTunnel())
                throw new InvalidOperationException("The Gateway tunnel is unavailable.");
            failure = GatewayDashboardLaunchOutcome.CredentialUnavailable;
            var credential = resolveCredential()
                ?? throw new InvalidOperationException("The Gateway credential is unavailable.");
            var canExport = !credential.IsBootstrapToken && credential.Source == CredentialResolver.SourceSharedGatewayToken;
            var url = GatewayDashboardUrlBuilder.Build(credential.GatewayUrl, path, credential.Token,
                canExport);
            failure = GatewayDashboardLaunchOutcome.BrowserUnavailable;
            if (!await launchBrowser(url))
                throw new InvalidOperationException("Windows did not open the Dashboard.");
            if (!canExport)
                opened = GatewayDashboardLaunchOutcome.OpenedSignInMayBeRequired;
        }
        catch (Exception ex) when (IsExpectedLaunchFailure(ex))
        {
            // Browser failures can include the full credential-bearing URL. Do not log them.
            reportFailure(failure);
            return failure;
        }
        reportOpened?.Invoke();
        return opened;
    }

    private static bool IsExpectedLaunchFailure(Exception error) =>
        error is InvalidOperationException or IOException or UnauthorizedAccessException or
            ArgumentException or Win32Exception or COMException;
}
