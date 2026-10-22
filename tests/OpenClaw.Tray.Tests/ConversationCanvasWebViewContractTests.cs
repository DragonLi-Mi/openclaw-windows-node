namespace OpenClaw.Tray.Tests;

/// <summary>Concrete browser-bridge bans; retirement condition is recorded in the architecture ledger.</summary>
public sealed class ConversationCanvasWebViewContractTests
{
    [Fact]
    public void ContentHostCannotAcquireNodeCanvasCredentialsFilesOrNativeHostObjects()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ConversationContentWebView.cs"));
        Assert.DoesNotContain("AddHostObjectToScript", source);
        Assert.DoesNotContain("SetVirtualHostNameToFolderMapping", source);
        Assert.DoesNotContain("ExecuteScriptAsync", source);
        Assert.DoesNotContain("Environment.SetEnvironmentVariable", source);
        Assert.DoesNotContain("Authorization", source);
        Assert.Contains("core.Settings.AreHostObjectsAllowed = false", source);
        Assert.Contains("CoreWebView2PermissionState.Deny", source);
        Assert.Contains("args.Action = CoreWebView2ServerCertificateErrorAction.Cancel", source);
    }

    [Fact]
    public void NativePromptAdmissionRequiresForegroundWindowNotOnlyRetainedXamlFocus()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ConversationContentWebView.cs"));
        Assert.Contains("XamlRoot is { } root && IsForeground(root)", source);
        Assert.Contains("hwnd != 0 && hwnd == GetForegroundWindow()", source);
        Assert.Contains("root.ContentIslandEnvironment is not { } environment", source);
        Assert.Contains("FocusManager.GetFocusedElement(root)", source);
        var canvas = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ConversationCanvasWebView.cs"));
        Assert.Contains("!outcome.Admission.HasCustody", canvas);
        Assert.Contains("ChatConversationContentAdmissionKind.Unconfirmed", canvas);
    }

    [Fact]
    public void BothHostsUseTheSingleAuditedNativeLifecycleButSeparateBridgeGrants()
    {
        var root = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Chat");
        var common = File.ReadAllText(Path.Combine(root, "ConversationContentWebView.cs"));
        var canvas = File.ReadAllText(Path.Combine(root, "ConversationCanvasWebView.cs"));
        var app = File.ReadAllText(Path.Combine(root, "ConversationMcpAppWebView.cs"));
        Assert.Contains(": ConversationContentWebView", canvas);
        Assert.Contains(": ConversationContentWebView", app);
        Assert.DoesNotContain("CoreWebView2Environment.Create", canvas);
        Assert.DoesNotContain("CoreWebView2Environment.Create", app);
        Assert.Contains("if (json.Length > MaximumBridgeMessageCharacters)", common);
        Assert.Contains("Boundary.IsNativeMessage(args.Source, message)", common);
        Assert.Contains("render.GetString() != RenderId", common);
        Assert.Contains("ExclusiveUserDataFolderAccess = true", common);
        Assert.Contains("options.IsInPrivateModeEnabled = true", common);
        Assert.Contains("RetiringEnvironments.Add", common);
        Assert.Contains("_rendered || !_initialized", app);
        Assert.DoesNotContain("SubmitPromptAsync", app);
        Assert.DoesNotContain("ReceiveAsync", canvas);
    }

    [Fact]
    public void NativeBrowserClosesOnlyAfterBoundedProtocolRetirementAndKeepsExactResponseAdmission()
    {
        var root = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Chat");
        var common = File.ReadAllText(Path.Combine(root, "ConversationContentWebView.cs"));
        var app = File.ReadAllText(Path.Combine(root, "ConversationMcpAppWebView.cs"));
        Assert.Contains("try { await RetireContentAsync(); }", common);
        Assert.Contains("try { CloseRetiredBrowser(); }", common);
        Assert.Contains("Browser retirement ended", common);
        Assert.Contains("if (_retiring) HandleRetirementMessage(message)", common);
        Assert.Contains("Boundary.IsNativeMessage(args.Source, message)", common);
        Assert.Contains("render.GetString() != RenderId", common);
        Assert.Contains("_session.ReceiveRetirementResponse(packet)", app);
        Assert.Contains("IsInteractable, IsEffectVisible", app);
    }
}
