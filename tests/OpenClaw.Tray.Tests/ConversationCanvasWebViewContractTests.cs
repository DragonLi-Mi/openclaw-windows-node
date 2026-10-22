namespace OpenClaw.Tray.Tests;

/// <summary>Concrete browser-bridge bans; retirement condition is recorded in the architecture ledger.</summary>
public sealed class ConversationCanvasWebViewContractTests
{
    [Fact]
    public void ContentHostCannotAcquireNodeCanvasCredentialsFilesOrNativeHostObjects()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ConversationCanvasWebView.cs"));
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
            "src", "OpenClaw.Tray.WinUI", "Chat", "ConversationCanvasWebView.cs"));
        Assert.Contains("XamlRoot is { } root && IsForeground(root)", source);
        Assert.Contains("hwnd != 0 && hwnd == GetForegroundWindow()", source);
        Assert.Contains("root.ContentIslandEnvironment is not { } environment", source);
        Assert.Contains("FocusManager.GetFocusedElement(root)", source);
        Assert.Contains("outcome != ChatConversationCanvasPromptResult.Accepted", source);
    }
}
