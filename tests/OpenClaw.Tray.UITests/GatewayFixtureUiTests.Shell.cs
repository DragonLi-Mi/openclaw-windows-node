using System.Windows.Automation;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task ShellPages_NodesExcludeOperatorOnlyPairingBanner()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "workspace:nodes" });
            await WaitUiAsync(run, () => FindShellText(run, "Fixture connected node") is not null, "connected Nodes");
            var approvals = await run.InvokeAsync("app.connection.pendingApprovals");
            Assert.True(approvals.GetProperty("connected").GetBoolean());
            Assert.Equal("fixture-operator", Assert.Single(approvals.GetProperty("devicePending").EnumerateArray()).GetProperty("deviceId").GetString());
            Assert.Empty(approvals.GetProperty("nodePending").EnumerateArray());
            await WaitUiAsync(run, () => FindShellText(run, "Fixture connected node") is { Current.IsOffscreen: false }
                && FindById(run, "InstancesPageMarker") is { Current.IsOffscreen: false }, "rendered Nodes after authoritative pair responses");
            Assert.Null(FindShellText(run, "Pairing approval required"));
            await run.InvokeAsync("app.navigate", new { page = "connection" });
            await WaitUiAsync(run, () => FindById(run, "ConnectionPageMarker") is { Current.IsOffscreen: false }, "rendered Connection");
            if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
                OwnedWindowCapture.Save(run, AppWindows(run).Single(window => window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "ConnectionPageMarker")) is not null),
                    "shell-operator-pending-connection.png");
            await WaitUiAsync(run, () => FindText(run, "1 approval waiting on you") is not null, "existing approvals header");
            AutomationElement? expander = FindText(run, "1 approval waiting on you");
            while (expander is not null && !expander.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _))
                expander = TreeWalker.ControlViewWalker.GetParent(expander);
            Assert.NotNull(expander);
            ((ExpandCollapsePattern)expander.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            await WaitUiAsync(run, () => FindText(run, "Fixture paired operator") is not null, "operator pending in its existing Connection owner");
            Assert.Empty(run.Gateway.UnexpectedRequests);
        }, scenario: GatewayScenario.CreateShellBrowse(pendingNode: false));
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task ShellPages_RetainMountedDraftFocusAndPersistentHeader()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.MainTitle, GatewayScenario.MainSessionKey);
            var workspace = FindHub(run);
            var windowHandle = workspace.Current.NativeWindowHandle;
            Assert.Contains("Fixture Gateway", FindById(run, "WorkspaceGatewayStatus")!.Current.Name);
            Assert.Contains("Connected", FindById(run, "WorkspaceGatewayStatus")!.Current.Name);
            Assert.Contains("sign in", FindById(run, "WorkspaceDashboard")!.Current.HelpText);
            await run.Gateway.CloseConnectionsAsync();
            await WaitUiAsync(run, () => FindById(run, "WorkspaceGatewayStatus")?.Current.Name.Contains("Reconnecting") == true,
                "same-Gateway reconnecting pill");
            await CaptureIfRequestedAsync(run, "shell-reconnecting.png");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceGatewayStatus")?.Current.Name.Contains("Connected") == true,
                "reconnected Gateway");
            await SelectSessionAsync(run, GatewayScenario.MainTitle, GatewayScenario.MainSessionKey);
            await WaitUiAsync(run, () => ((ValuePattern)FindById(run, "ChatComposerInput")!
                .GetCurrentPattern(ValuePattern.Pattern)).Current.Value.Length == 0 &&
                FindById(run, "ChatComposerPrimaryAction") is { Current.IsEnabled: false, Current.Name: "Send" },
                "idle empty composer before accepting a new draft");
            var composer = FindById(run, "ChatComposerInput")!;
            var runtimeId = composer.GetRuntimeId();
            const string draft = "Retained shell proof draft. Do not send.";
            ((ValuePattern)composer.GetCurrentPattern(ValuePattern.Pattern)).SetValue(draft);
            await WaitUiAsync(run, () => ((ValuePattern)FindById(run, "ChatComposerInput")!
                .GetCurrentPattern(ValuePattern.Pattern)).Current.Value == draft, "exact draft before shell navigation");
            await WaitUiAsync(run, () => FindById(run, "ChatComposerPrimaryAction") is
                { Current.IsEnabled: true, Current.Name: "Send" }, "accepted draft with enabled native Send, without sending");
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "chat.send");
            composer.SetFocus();
            await WaitUiAsync(run, () => composer.Current.HasKeyboardFocus, "current composer focus before shell navigation");

            foreach (var (route, marker, populated) in new[]
            {
                ("automations", "CronPageMarker", "Fixture cron shell-browse"),
                ("usage", "UsagePageMarker", "Synthetic fixture provider"),
                ("nodes", "InstancesPageMarker", "Fixture connected node")
            })
            {
                Invoke(FindById(run, $"WorkspaceNav{char.ToUpperInvariant(route[0])}{route[1..]}")!);
                await WaitUiAsync(run, () => FindById(run, marker) is { Current.IsOffscreen: false }
                    && FindText(run, populated) is not null, $"populated native {route}");
                Assert.NotNull(FindById(run, "WorkspaceGatewayStatus"));
                Assert.True(FindById(run, "WorkspaceDashboard")!.Current.IsEnabled);
                Assert.DoesNotContain(AppWindows(run), window => window.Current.NativeWindowHandle != windowHandle
                    && window.Current.Name == "OpenClaw Settings");
                if (route == "nodes")
                {
                    Assert.Null(FindText(run, "Fixture offline paired node"));
                    Assert.Null(FindText(run, "Fixture operator browser"));
                    Assert.Null(FindText(run, "Fixture paired operator"));
                    Assert.NotNull(FindText(run, "capabilities won't activate until you approve"));
                }
                await CaptureIfRequestedAsync(run, $"shell-{route}-populated.png");
                Invoke(FindById(run, "WorkspaceBack")!);
                await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is { Current.HasKeyboardFocus: true },
                    "history restored composer focus");
                var restored = FindById(run, "ChatComposerInput")!;
                Assert.Equal(runtimeId, restored.GetRuntimeId());
                Assert.Equal(draft, ((ValuePattern)restored.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
                Assert.True(SessionSelected(run, GatewayScenario.MainTitle));
                Invoke(FindById(run, "WorkspaceForward")!);
                await WaitUiAsync(run, () => FindById(run, marker) is { Current.IsOffscreen: false }, $"forward restored {route}");
                Invoke(FindById(run, "WorkspaceNavHome")!);
                await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is not null, "retained chat");
                Assert.True(SessionSelected(run, GatewayScenario.MainTitle));
                Assert.Equal(draft, ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
            }

            Invoke(FindById(run, "WorkspaceTogglePane")!);
            await WaitUiAsync(run, () => FindById(run, "WorkspaceReopenPane") is { Current.IsOffscreen: false }, "collapsed pane");
            Assert.False(FindById(run, "WorkspaceGatewayStatus")!.Current.IsOffscreen);
            Assert.False(FindById(run, "WorkspaceDashboard")!.Current.IsOffscreen);
            await CaptureIfRequestedAsync(run, "shell-header-collapsed.png");
            ((WindowPattern)workspace.GetCurrentPattern(WindowPattern.Pattern)).Close();
            await WaitUiAsync(run, () => !AppWindows(run).Any(window => window.Current.NativeWindowHandle == windowHandle),
                "Workspace close hides the window");
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is not null, "reopened retained Workspace");
            Assert.Equal(windowHandle, FindHub(run).Current.NativeWindowHandle);
            Assert.Equal(runtimeId, FindById(run, "ChatComposerInput")!.GetRuntimeId());
            Assert.Equal(draft, ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
            await CaptureIfRequestedAsync(run, "shell-reopened-draft.png");
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method is "chat.send" or "sessions.create");
            Assert.Empty(run.Gateway.UnexpectedRequests);
        }, scenario: GatewayScenario.CreateShellBrowse());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task ShellPages_ReinitializeAfterRemountAndObserveDisconnect()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            foreach (var (route, marker, message) in new[]
            {
                ("usage", "UsagePageMarker", "Connect to a gateway to load usage data."),
                ("automations", "CronPageMarker", "Connect to a gateway to load cron jobs."),
                ("nodes", "InstancesPageMarker", "Connect to a gateway to see live instance data.")
            })
            {
                await run.InvokeAsync("app.navigate", new { page = $"workspace:{route}" });
                await WaitUiAsync(run, () => FindById(run, marker) is not null, $"first mount {route}");
                await run.InvokeAsync("app.navigate", new { page = "workspace:home" });
                await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is { Current.IsOffscreen: false },
                    "rendered Home between page mounts");
                await run.InvokeAsync("app.navigate", new { page = $"workspace:{route}" });
                await WaitUiAsync(run, () => FindById(run, marker) is not null, $"remounted {route}");
                var pageIdentity = FindById(run, marker)!.GetRuntimeId();
                await run.InvokeAsync("app.navigate", new { page = "connection" });
                await WaitUiAsync(run, () => FindById(run, "ConnectionToggle") is { Current.IsOffscreen: false }, "connection toggle");
                var toggle = (TogglePattern)FindById(run, "ConnectionToggle")!.GetCurrentPattern(TogglePattern.Pattern);
                Assert.Equal(ToggleState.On, toggle.Current.ToggleState);
                toggle.Toggle();
                await WaitUiAsync(run, () => FindById(run, "WorkspaceGatewayStatus")?.Current.Name.Contains("Disconnected") == true,
                    "authoritative disconnected pill");
                await WaitUiAsync(run, () => FindShellText(run, message) is { Current.IsOffscreen: false }, $"actionable offline {route}");
                Assert.Equal(pageIdentity, FindById(run, marker)!.GetRuntimeId());
                if (route == "nodes") Assert.Null(FindShellText(run, "Fixture connected node"));
                await run.InvokeAsync("app.navigate", new { page = $"workspace:{route}" });
                await CaptureIfRequestedAsync(run, $"shell-{route}-disconnected.png");
                toggle.Toggle();
                await WaitUiAsync(run, () => FindById(run, "WorkspaceGatewayStatus")?.Current.Name.Contains("Connected") == true,
                    "explicit reconnect completed");
            }
            Assert.Empty(run.Gateway.UnexpectedRequests);
        }, scenario: GatewayScenario.CreateShellBrowse());
    }

    private static AutomationElement? FindShellText(OpenClaw.GatewayFixtureHost.GatewayFixtureRun run, string text) =>
        AppWindows(run).Where(window => window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspaceGatewayStatus")) is not null)
        .SelectMany(window => window.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>())
        .FirstOrDefault(element => element.Current.Name.Contains(text, StringComparison.Ordinal));
}
