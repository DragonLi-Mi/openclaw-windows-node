using System.Collections.Immutable;
using System.Reflection;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation.Adapters;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class ConversationPanelHostProofTests(UIThreadFixture ui)
{
    private static readonly TimeSpan InitialTailReadinessTimeout = TimeSpan.FromSeconds(10);
    private Window? _proofWindow;

    [Theory]
    [InlineData(960)]
    [InlineData(480)]
    public async Task PanelOpenClosePreservesProductionTimelineComposerSelectionAndScroll(int width)
    {
        await WithRootAsync(width, async (surface, panels, _, _) =>
        {
            TextBox? input = null;
            ItemsView? timeline = null;
            ScrollView? scroll = null;
            double offset = 0;
            await ui.RunOnUIAsync(() =>
            {
                input = Find<TextBox>(surface, "ChatComposerInput");
                timeline = Assert.Single(FindDescendants<ItemsView>(surface));
                scroll = timeline.ScrollView;
                Assert.True(scroll.ScrollableHeight > 160);
            });
            var readerScroll = scroll ?? throw new InvalidOperationException("The production timeline has no ScrollView.");
            var editor = input ?? throw new InvalidOperationException("The production composer has no input.");
            await WaitForInitialTailAsync(surface, readerScroll);
            await ui.RunOnUIAsync(() =>
            {
                readerScroll.ScrollTo(0, 120, new ScrollingScrollOptions(
                    ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
                editor.Select(5, 8);
                editor.Focus(FocusState.Programmatic);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                offset = scroll!.VerticalOffset;
                Assert.InRange(offset, 119, 121);
                Assert.True(panels.Open("details"));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(input, Find<TextBox>(surface, "ChatComposerInput"));
                Assert.Same(timeline, Assert.Single(FindDescendants<ItemsView>(surface)));
                Assert.Same(scroll, timeline!.ScrollView);
                Assert.Equal("Keep this conversation draft.", input!.Text);
                Assert.Equal(5, input.SelectionStart);
                Assert.Equal(8, input.SelectionLength);
                var panel = Find<Border>(surface, "ConversationPanel");
                Assert.Equal(Visibility.Visible, panel.Visibility);
                Assert.InRange(panel.ActualWidth, 280, width);
                var close = Find<Button>(surface, "ConversationPanelClose");
                Assert.Equal("Close panel", AutomationProperties.GetName(close));
            });
            await CaptureAsync(surface, $"Panel-{width}-open");
            await ui.RunOnUIAsync(() => Invoke(Find<Button>(surface, "ConversationPanelClose")));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Null(panels.Layout.SelectedPanel);
                Assert.Same(input, Find<TextBox>(surface, "ChatComposerInput"));
                Assert.Same(timeline, Assert.Single(FindDescendants<ItemsView>(surface)));
                Assert.InRange(scroll!.VerticalOffset, offset - 1, offset + 1);
                Assert.Equal("Keep this conversation draft.", input!.Text);
                Assert.Equal(5, input.SelectionStart);
                Assert.Equal(8, input.SelectionLength);
                Assert.Same(input, FocusManager.GetFocusedElement(surface.XamlRoot));
            });
            await CaptureAsync(surface, $"Panel-{width}-closed");
        });
    }

    [Fact]
    public async Task SuspendedHostReplacesPanelBodyWithWaitingWithoutDiscardingSelection()
    {
        await WithRootAsync(960, async (surface, panels, _, _) =>
        {
            await ui.RunOnUIAsync(() => Assert.True(panels.Open("details")));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Contains(FindDescendants<TextBlock>(surface),
                    text => text.Text == "These details are no longer current. Reopen details from the current tool.");
                panels.Suspend();
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("details", panels.Layout.SelectedPanel);
                Assert.Contains(FindDescendants<TextBlock>(surface),
                    text => text.Text == "Waiting for this conversation to connect. Your panel selection is retained.");
                Assert.DoesNotContain(FindDescendants<TextBlock>(surface),
                    text => text.Text == "These details are no longer current. Reopen details from the current tool.");
                Assert.Null(panels.BeginOperation("details"));
            });
        });
    }

    [Fact]
    public async Task NativeToolActionUpdatesLiveOutputAndRequiresReopenAfterHistoryCommit()
    {
        await WithRootAsync(960, async (surface, panels, provider, _) =>
        {
            await ui.RunOnUIAsync(() => Find<Expander>(surface, "ChatToolCall_tool_entry").IsExpanded = true);
            await SettleAsync();
            await ui.RunOnUIAsync(() => Invoke(Find<Button>(surface, "ChatToolOpenDetails_tool_entry")));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("details", panels.Layout.SelectedPanel);
                Assert.Equal("initial output", DetailText(surface));
                provider.PublishOutput("latest <script>literal text</script>");
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("latest <script>literal text</script>", DetailText(surface));
            });
            await CaptureAsync(surface, "Tool-details-live");
            await ui.RunOnUIAsync(() => provider.PublishOutput("committed history output", historyRevision: 6));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("details", panels.Layout.SelectedPanel);
                var panel = Find<Border>(surface, "ConversationPanel");
                Assert.Contains(FindDescendants<TextBlock>(panel), text => text.Text ==
                    "These details are no longer current. Reopen details from the current tool.");
                Assert.Equal(string.Empty, DetailText(surface));
            });
            await CaptureAsync(surface, "Tool-details-history-unavailable");
            await ui.RunOnUIAsync(() => Invoke(Find<Button>(surface, "ChatToolOpenDetails_tool_entry")));
            await SettleAsync();
            await ui.RunOnUIAsync(() => Assert.Equal("committed history output", DetailText(surface)));
            await CaptureAsync(surface, "Tool-details-reopened");
        }, dense: false);
    }

    private static string DetailText(DependencyObject surface) => string.Concat(
        FindDescendants<RichTextBlock>(Find<Border>(surface, "ConversationPanel"))
            .SelectMany(text => text.Blocks.OfType<Paragraph>())
            .SelectMany(paragraph => paragraph.Inlines.OfType<Run>())
            .Select(run => run.Text));

    [Fact]
    public async Task UnavailableAccountExplainsWhyPanelsCannotOpenAndKeepsComposerUsable()
    {
        await WithRootAsync(960, async (surface, panels, _, changeAccountStatus) =>
        {
            TextBlock? originalNotice = null;
            await ui.RunOnUIAsync(() =>
            {
                var notice = Find<TextBlock>(surface, "ConversationPanelAccountStatus");
                originalNotice = notice;
                Assert.Equal(Visibility.Visible, notice.Visibility);
                Assert.Equal("This Gateway cannot verify the operator account. Conversation panels are unavailable.", notice.Text);
                Assert.Equal(notice.Text, AutomationProperties.GetName(notice));
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(notice));
                Assert.True(Find<TextBox>(surface, "ChatComposerInput").IsEnabled);
                Assert.False(panels.Open("details"));
                Assert.Null(panels.Context);
                Assert.Empty(panels.Layout.OpenPanels);
            });
            await CaptureAsync(surface, "Account-unavailable");
            await ui.RunOnUIAsync(() => changeAccountStatus(ChatConversationAccountStatus.Loading));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var notice = Find<TextBlock>(surface, "ConversationPanelAccountStatus");
                Assert.Same(originalNotice, notice);
                Assert.Equal("Verifying the operator account for this conversation.", notice.Text);
                Assert.Equal(notice.Text, AutomationProperties.GetName(notice));
            });
            await CaptureAsync(surface, "Account-loading");
            await ui.RunOnUIAsync(() => changeAccountStatus(ChatConversationAccountStatus.RequiresReconnect));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var notice = Find<TextBlock>(surface, "ConversationPanelAccountStatus");
                Assert.Same(originalNotice, notice);
                Assert.Equal("The operator account changed. Reconnect before opening conversation panels.", notice.Text);
                Assert.Equal(notice.Text, AutomationProperties.GetName(notice));
            });
            await CaptureAsync(surface, "Account-reconnect-required");
        }, unverifiedAccount: true);
    }

    private async Task WithRootAsync(
        int width, Func<Border, ChatConversationPanelState, PanelProvider, Action<ChatConversationAccountStatus>, Task> proof, bool dense = true,
        bool unverifiedAccount = false)
    {
        await ui.ResetContainerAsync();
        var provider = new PanelProvider(dense);
        var client = DispatchProxy.Create<IOperatorGatewayClient, ReadyClient>();
        var accountStatus = unverifiedAccount ? ChatConversationAccountStatus.Unavailable : ChatConversationAccountStatus.Known;
        using var panels = new ChatConversationPanelState(_ => accountStatus != ChatConversationAccountStatus.Known ? null : ChatConversationContext.Capture(
            "gateway-proof", client, new() { Key = "one", AgentId = "writing", SessionId = "session-one" }, "profile-proof"),
            accountStatus: () => accountStatus);
        Border? surface = null;
        Window? window = null;
        ReactorHostControl? host = null;
        ChatComposerSession? composer = null;
        ChatThemeProofScope? theme = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                LocalizationHelper.SetLanguageOverride("en-us");
                theme = new("Light");
                OpenClawReactorChatRoot.SetToolCallsVisible(true);
                composer = new ChatComposerFactory(new WinUIDispatcher(ui.Dispatcher)).Create(
                    provider, new ChatComposerHostActions(null, null, null, null), initialSpeakerMuted: true);
                composer.ViewModel.SetDraft("Keep this conversation draft.");
                host = new ReactorHostControl { RequestedTheme = theme.ElementTheme };
                host.Mount(_ => Component<OpenClawReactorChatRoot, OpenClawReactorChatRootProps>(
                    new(provider, composer, InitialThreadId: "one", Panels: panels)));
                surface = new Border { Width = width, Height = 720, Child = host, RequestedTheme = theme.ElementTheme };
                window = new Window { Content = surface };
                _proofWindow = window;
                window.AppWindow.MoveAndResize(new(-32000, -32000, 1200, 900));
                window.Activate();
                RecordOwnedWindow(window);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var scale = surface!.XamlRoot.RasterizationScale;
                window!.AppWindow.ResizeClient(new(
                    (int)Math.Ceiling(surface.Width * scale), (int)Math.Ceiling(surface.Height * scale)));
                surface.UpdateLayout();
                RecordOwnedWindow(window);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => FindDescendants<TextBox>(surface!).Any(control =>
                    AutomationProperties.GetAutomationId(control) == "ChatComposerInput") &&
                    (unverifiedAccount
                        ? FindDescendants<TextBlock>(surface!).Any(text => text.Text ==
                            "This Gateway cannot verify the operator account. Conversation panels are unavailable.")
                        : panels.IsReady),
                "production conversation panel host"));
            await proof(surface!, panels, provider, status =>
            {
                accountStatus = status;
                panels.Refresh();
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                composer?.Dispose();
                if (surface is not null) surface.Child = null;
                host?.Dispose();
                window?.Close();
                _proofWindow = null;
                theme?.Dispose();
            });
            await provider.DisposeAsync();
        }
    }

    private static void RecordOwnedWindow(Window window)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_CONVERSATION_PANEL_PROOF_DIR") is not { Length: > 0 } directory)
            return;
        Directory.CreateDirectory(directory);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var metadata = new
        {
            RunId = Path.GetFileName(directory),
            ProcessId = process.Id,
            StartedUtc = process.StartTime.ToUniversalTime(),
            Executable = Environment.ProcessPath,
            CommandLine = Environment.CommandLine,
            Window = handle.ToInt64(),
            ClientWidth = window.AppWindow.ClientSize.Width,
            ClientHeight = window.AppWindow.ClientSize.Height,
            Port = (int?)null,
            Data = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR"),
            AppData = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR"),
            LocalAppData = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR"),
            LegacyLocal = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR"),
            SyntheticProvider = true,
            RealGateway = false,
            Binaries = new[]
            {
                typeof(ConversationPanelHostProofTests).Assembly.Location,
                typeof(OpenClawReactorChatRoot).Assembly.Location,
                typeof(ReactorHostControl).Assembly.Location
            }.Select(path => new
            {
                Path = path,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
            }).ToArray()
        };
        File.WriteAllText(Path.Combine(directory, $"owned-window-{handle.ToInt64()}.json"),
            System.Text.Json.JsonSerializer.Serialize(metadata));
    }

    private async Task CaptureAsync(FrameworkElement surface, string name)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") != "1") return;
        var directory = Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR")
            ?? throw new InvalidOperationException("A proof capture directory is required.");
        await ui.RunOnUIAsync(async () =>
        {
            await VisualTestCapture.CaptureAsync(surface, name);
            var path = Path.Combine(directory, name, "capture-00.png");
            Assert.True(File.Exists(path), $"Missing current-state capture: {path}");
            using var bitmap = new System.Drawing.Bitmap(path);
            var window = _proofWindow ?? throw new InvalidOperationException("No owned capture window.");
            Assert.Same(surface, window.Content);
            var scale = surface.XamlRoot.RasterizationScale;
            var expectedWidth = (int)Math.Ceiling(surface.ActualWidth * scale);
            var expectedHeight = (int)Math.Ceiling(surface.ActualHeight * scale);
            Assert.True(window.AppWindow.ClientSize.Width >= expectedWidth &&
                window.AppWindow.ClientSize.Height >= expectedHeight,
                "The owned physical client viewport must not clip the asserted component root.");
            Assert.InRange(bitmap.Width, expectedWidth - 1, expectedWidth + 1);
            Assert.InRange(bitmap.Height, expectedHeight - 1, expectedHeight + 1);
            var colors = new HashSet<int>();
            for (var y = 0; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 64))
                for (var x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 64))
                    colors.Add(bitmap.GetPixel(x, y).ToArgb());
            Assert.True(colors.Count > 8, "Blank or unrendered proof frames are not evidence.");
            File.WriteAllText(Path.Combine(directory, name, "capture.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))),
                    bitmap.Width, bitmap.Height, RootWidth = surface.ActualWidth, RootHeight = surface.ActualHeight,
                    ColorSamples = colors.Count, Theme = surface.ActualTheme.ToString(),
                    SyntheticProvider = true, CompositorCapture = false
                }));
        });
    }

    private Task WaitForInitialTailAsync(Border surface, ScrollView scroll) => ui.RunOnUIAsync(async () =>
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = true;
        var stablePasses = 0;
        var previousExtent = double.NaN;
        var previousViewport = double.NaN;
        void CheckLayout()
        {
            if (!active) return;
            surface.UpdateLayout();
            var atTail = scroll.IsLoaded && scroll.ScrollableHeight > 160 &&
                Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) <= 1;
            stablePasses = atTail && Math.Abs(previousExtent - scroll.ExtentHeight) < 0.5 &&
                Math.Abs(previousViewport - scroll.ViewportHeight) < 0.5 ? stablePasses + 1 : 0;
            previousExtent = scroll.ExtentHeight;
            previousViewport = scroll.ViewportHeight;
            if (stablePasses >= 2) settled.TrySetResult();
            else if (!ui.Dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, CheckLayout))
                settled.TrySetException(new InvalidOperationException("Dispatcher rejected initial-tail readiness."));
        }
        CheckLayout();
        try
        {
            await settled.Task.WaitAsync(InitialTailReadinessTimeout);
            Assert.True(stablePasses >= 2);
            Assert.InRange(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight), 0, 1);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"Initial tail never settled for two layout passes: offset={scroll.VerticalOffset}, " +
                $"maximum={scroll.ScrollableHeight}, extent={scroll.ExtentHeight}, viewport={scroll.ViewportHeight}.");
        }
        finally { active = false; }
    });

    private async Task SettleAsync()
    {
        await ui.YieldToRenderAsync();
        await ui.YieldToRenderAsync();
    }

    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Assert.Single(FindDescendants<T>(root), control => AutomationProperties.GetAutomationId(control) == id);

    private static void Invoke(Button button) =>
        Assert.IsAssignableFrom<IInvokeProvider>(
            new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    public class ReadyClient : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_IsConnectedToGateway" => true,
            "get_SessionMutationConnectionEpoch" => (long?)1,
            "get_AuthenticatedSigningDeviceId" => "operator-proof",
            _ => throw new NotSupportedException(method?.Name)
        };
    }

    private sealed class PanelProvider(bool dense) : IChatDataProvider
    {
        private ChatDataSnapshot? _snapshot;
        public string DisplayName => "Conversation panel native proof";
#pragma warning disable CS0067
        public event EventHandler<ChatDataChangedEventArgs>? Changed;
        public event EventHandler<ChatProviderNotificationEventArgs>? NotificationRequested;
#pragma warning restore CS0067
        public Task<ChatDataSnapshot> LoadAsync(CancellationToken cancellationToken = default)
        {
            if (_snapshot is not null) return Task.FromResult(_snapshot);
            var thread = new ChatThread { Id = "one", Title = "Panel proof", AgentId = "writing" };
            var timeline = ChatTimelineState.Initial() with
            {
                Entries = dense
                    ? Enumerable.Range(1, 50).Select(index => new ChatTimelineItem(
                        "entry-" + index, ChatTimelineItemKind.User, "Panel message " + index)).ToImmutableList()
                    : ImmutableList.Create(
                        new ChatTimelineItem("user", ChatTimelineItemKind.User, "Inspect this tool"),
                        new ChatTimelineItem("tool-entry", ChatTimelineItemKind.ToolCall, string.Empty,
                            ToolName: "read", ToolOutput: "initial output",
                            ToolCallId: "call-one", ToolRunId: "run-one")),
                HistoryLoaded = true
            };
            return Task.FromResult(_snapshot = new ChatDataSnapshot([thread],
                new Dictionary<string, ChatTimelineState> { ["one"] = timeline },
                "one", "Connected", [], new("one", true, "writing"),
                TimelineGenerations: new Dictionary<string, long> { ["one"] = 3 },
                HistoryRevisions: new Dictionary<string, long> { ["one"] = 5 }));
        }

        internal void PublishOutput(string output, long historyRevision = 5)
        {
            var snapshot = _snapshot ?? throw new InvalidOperationException("Proof provider is not loaded.");
            var timeline = snapshot.Timelines["one"];
            _snapshot = snapshot with
            {
                Timelines = new Dictionary<string, ChatTimelineState>
                {
                    ["one"] = timeline with
                    {
                        Entries = timeline.Entries.SetItem(1, timeline.Entries[1] with { ToolOutput = output })
                    }
                },
                HistoryRevisions = new Dictionary<string, long> { ["one"] = historyRevision }
            };
            Changed?.Invoke(this, new(_snapshot));
        }

        private static Task Unexpected() => Task.FromException(new InvalidOperationException("Unexpected proof mutation."));
        public Task SendMessageAsync(string threadId, string message, CancellationToken cancellationToken = default) => Unexpected();
        public Task StopResponseAsync(string threadId, CancellationToken cancellationToken = default) => Unexpected();
        public Task SetThreadSuspendedAsync(string threadId, bool suspended, CancellationToken cancellationToken = default) => Unexpected();
        public Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default) => Unexpected();
        public Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken = default) => Unexpected();
        public Task SetThinkingLevelAsync(string threadId, string thinkingLevel, CancellationToken cancellationToken = default) => Unexpected();
        public Task SetPermissionModeAsync(string threadId, bool allowAll, CancellationToken cancellationToken = default) => Unexpected();
        public Task RespondToPermissionAsync(string threadId, string requestId, string action, CancellationToken cancellationToken = default) => Unexpected();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
