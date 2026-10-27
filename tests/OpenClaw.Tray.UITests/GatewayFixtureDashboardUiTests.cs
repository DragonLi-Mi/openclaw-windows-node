using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task DashboardsRenderNativeReportsTabsAndFailureStates()
    {
        var previousTheme = Environment.GetEnvironmentVariable("OPENCLAW_FIXTURE_APP_THEME");
        try
        {
            foreach (var theme in new[] { "Light", "Dark" })
            {
                Environment.SetEnvironmentVariable("OPENCLAW_FIXTURE_APP_THEME", theme);
                await WithAppAsync(async run =>
                {
                    await run.InvokeAsync("app.navigate", new { page = "dashboards" });
                    await WaitUiAsync(run, () => FindById(run, $"DashboardCard_{FixtureBoards.OpsSessionKey}") is not null,
                        "dashboard gallery populated");
                    var hub = AppWindows(run).Single(window => window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "DashboardsTitle")) is not null);
                    ((WindowPattern)hub.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Maximized);
                    await WaitUiAsync(run, () => ((WindowPattern)hub.GetCurrentPattern(WindowPattern.Pattern))
                            .Current.WindowVisualState == WindowVisualState.Maximized,
                        "maximized dashboard layout");
                    System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(DwmFlush());
                    AxeHelper.AssertNoAccessibilityErrors(new IntPtr(hub.Current.NativeWindowHandle),
                        context: $"Dashboards gallery ({theme})", targetProcessId: run.AppProcessId);
                    OwnedWindowCapture.Save(run, hub, $"dashboards-{theme}-gallery.png");

                    Invoke(FindById(run, $"DashboardCard_{FixtureBoards.OpsSessionKey}")!);
                    // This host's UIA provider can stop at the WebView pane.
                    // Verify the visible HTML and native report through owned-window pixels.
                    await WaitForDashboardPixelsAsync(run, hub, $"dashboards-{theme}-overview.png",
                        "99.97", "Uptime", "Tickets closed");
                    AxeHelper.AssertNoAccessibilityErrors(new IntPtr(hub.Current.NativeWindowHandle),
                        context: $"Dashboards widgets ({theme})", targetProcessId: run.AppProcessId);
                    ScrollAncestorToBottom(FindById(run, "BoardWidget_broken-widget")!);
                    await WaitForDashboardPixelsAsync(run, hub, $"dashboards-{theme}-approval-error.png",
                        "Needs approval", "contained to this card");

                    var metrics = FindInApp(run, new AndCondition(
                        new PropertyCondition(AutomationElement.NameProperty, "Metrics"),
                        new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true)));
                    Assert.NotNull(metrics);
                    Invoke(metrics!);
                    await WaitForDashboardPixelsAsync(run, hub, $"dashboards-{theme}-tabs.png",
                        "MCP app", "Access rejected", "disabled plugin");

                    // Reopening the same hub route must return from an in-page board to the gallery.
                    await run.InvokeAsync("app.navigate", new { page = "dashboards" });
                    await WaitUiAsync(run, () => FindById(run, $"DashboardCard_{FixtureBoards.EmptySessionKey}") is not null,
                        "gallery after same-route navigation");
                    Invoke(FindById(run, $"DashboardCard_{FixtureBoards.EmptySessionKey}")!);
                    await WaitUiAsync(run, () => FindById(run, "DashboardEmptyOpenChat") is { Current.IsOffscreen: false },
                        "empty dashboard action");
                    OwnedWindowCapture.Save(run, hub, $"dashboards-{theme}-empty.png");

                    await run.InvokeAsync("app.navigate", new { page = "dashboards" });
                    await WaitUiAsync(run, () => FindById(run, "DashboardsSearch") is not null, "gallery search");
                    ((ValuePattern)FindById(run, "DashboardsSearch")!.GetCurrentPattern(ValuePattern.Pattern))
                        .SetValue("no matching synthetic dashboard");
                    await WaitUiAsync(run, () => FindById(run, "DashboardsNoResultsTitle") is not null, "no-results state");
                    await WaitForDashboardPixelsAsync(run, hub, $"dashboards-{theme}-no-results.png", "No matching dashboards");

                    // Stop only this synthetic Gateway, then observe the real connection/UI boundary.
                    await run.Gateway.DisposeAsync();
                    await WaitUiAsync(run, () => FindById(run, "DashboardsOpenConnection") is { Current.IsOffscreen: false },
                        "disconnected dashboard recovery action");
                    OwnedWindowCapture.Save(run, hub, $"dashboards-{theme}-disconnected.png");

                    Assert.DoesNotContain(run.Gateway.Requests, request =>
                        request.Method is "board.widget.grant" or "board.update" or "board.action");
                    await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "dashboard-contract.json"),
                        JsonSerializer.Serialize(new
                        {
                            theme,
                            proof = "Real isolated app, Gateway WebSocket, native UIA navigation and Windows OCR over owned-window pixels",
                            html = "99.97%",
                            report = "Tickets closed",
                            mutations = 0,
                        }));
                }, scenario: GatewayScenario.CreateDashboards());
            }

        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_FIXTURE_APP_THEME", previousTheme);
        }
    }

    private static async Task WaitForDashboardPixelsAsync(GatewayFixtureRun run, AutomationElement hub,
        string imageName, params string[] expectedText)
    {
        var engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
            ?? throw new InvalidOperationException("English Windows OCR is required to verify the sandbox's rendered pixels.");
        await run.WaitForAsync(async () =>
        {
            OwnedWindowCapture.Save(run, hub, imageName);
            var file = await StorageFile.GetFileFromPathAsync(Path.Combine(run.ArtifactsDirectory, imageName));
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap);
            return expectedText.All(text => result.Text.Contains(text, StringComparison.OrdinalIgnoreCase));
        }, $"visible dashboard pixels: {string.Join(", ", expectedText)}", TimeSpan.FromSeconds(30));
    }
}
