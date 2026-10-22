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
using OpenClawTray.Presentation.Adapters;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class ConversationPanelHostProofTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData(960)]
    [InlineData(480)]
    public async Task PanelOpenClosePreservesProductionTimelineComposerSelectionAndScroll(int width)
    {
        await WithRootAsync(width, async (surface, panels, _) =>
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
                scroll.ScrollTo(0, 120, new ScrollingScrollOptions(
                    ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
                input.Select(5, 8);
                input.Focus(FocusState.Programmatic);
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
                Invoke(close);
            });
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
        });
    }

    [Fact]
    public async Task SuspendedHostReplacesPanelBodyWithWaitingWithoutDiscardingSelection()
    {
        await WithRootAsync(960, async (surface, panels, _) =>
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
        await WithRootAsync(960, async (surface, panels, provider) =>
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
                provider.PublishOutput("committed history output", historyRevision: 6);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("details", panels.Layout.SelectedPanel);
                var panel = Find<Border>(surface, "ConversationPanel");
                Assert.Contains(FindDescendants<TextBlock>(panel), text => text.Text ==
                    "These details are no longer current. Reopen details from the current tool.");
                Assert.Equal(string.Empty, DetailText(surface));
                Invoke(Find<Button>(surface, "ChatToolOpenDetails_tool_entry"));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() => Assert.Equal("committed history output", DetailText(surface)));
        }, dense: false);
    }

    private static string DetailText(DependencyObject surface) => string.Concat(
        FindDescendants<RichTextBlock>(Find<Border>(surface, "ConversationPanel"))
            .SelectMany(text => text.Blocks.OfType<Paragraph>())
            .SelectMany(paragraph => paragraph.Inlines.OfType<Run>())
            .Select(run => run.Text));

    private async Task WithRootAsync(
        int width, Func<Border, ChatConversationPanelState, PanelProvider, Task> proof, bool dense = true)
    {
        await ui.ResetContainerAsync();
        var provider = new PanelProvider(dense);
        var client = DispatchProxy.Create<IOperatorGatewayClient, ReadyClient>();
        using var panels = new ChatConversationPanelState(_ => ChatConversationContext.Capture(
            "gateway-proof", client, new() { Key = "one", AgentId = "writing", SessionId = "session-one" }));
        Border? surface = null;
        Window? window = null;
        ReactorHostControl? host = null;
        ChatComposerSession? composer = null;
        ChatThemeProofScope? theme = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                theme = new("Light");
                OpenClawReactorChatRoot.SetToolCallsVisible(true);
                composer = new ChatComposerFactory(new WinUIDispatcher(ui.Dispatcher)).Create(
                    provider, new ChatComposerHostActions(null, null, null, null), initialSpeakerMuted: true);
                composer.ViewModel.SetDraft("Keep this conversation draft.");
                host = new ReactorHostControl();
                host.Mount(_ => Component<OpenClawReactorChatRoot, OpenClawReactorChatRootProps>(
                    new(provider, composer, InitialThreadId: "one", Panels: panels)));
                surface = new Border { Width = width, Height = 720, Child = host };
                window = new Window { Content = surface };
                window.AppWindow.MoveAndResize(new(-32000, -32000, 1200, 900));
                window.Activate();
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => FindDescendants<TextBox>(surface!).Any(control =>
                    AutomationProperties.GetAutomationId(control) == "ChatComposerInput") && panels.IsReady,
                "production conversation panel host"));
            await proof(surface!, panels, provider);
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                composer?.Dispose();
                if (surface is not null) surface.Child = null;
                host?.Dispose();
                window?.Close();
                theme?.Dispose();
            });
            await provider.DisposeAsync();
        }
    }

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
