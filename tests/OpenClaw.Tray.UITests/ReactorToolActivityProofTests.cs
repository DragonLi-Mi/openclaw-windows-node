using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using OpenClaw.Chat;
using OpenClawTray.Chat;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>Offscreen real-runtime proof for the production Reactor tool activity renderer.</summary>
[Collection(UICollection.Name)]
public sealed class ReactorToolActivityProofTests
{
    private readonly UIThreadFixture _ui;

    public ReactorToolActivityProofTests(UIThreadFixture ui) => _ui = ui;

    [Fact]
    public async Task OpenDetails_PassesExactIdentityAndPanelReusesLiteralSections()
    {
        const string input = "command: Get-Item '<input>'";
        const string output = "<script>not HTML</script>\nhttps://example.test/result";
        var entry = Tool("tool-1", "powershell", output, ChatToolCallStatus.Success) with
        {
            ToolRunId = "run-7",
            ToolCallId = "tool-call-7",
            ToolArgs = new() { ["command"] = "Get-Item '<input>'" },
        };
        ChatToolDetailRequest? requested = null;
        var timeline = BuildTimeline([entry], generation: 9) with
        {
            OnOpenToolDetails = request => requested = request,
            HistoryRevision = 4,
        };
        ReactorHostControl? host = null;
        ChatThemeProofScope? themeScope = null;
        try
        {
            await _ui.RunOnUIAsync(() =>
            {
                themeScope = new ChatThemeProofScope("Light");
                host = new ReactorHostControl();
                var root = Assert.IsAssignableFrom<UIElement>(host.Reconciler.Mount(
                    ToolCallCardRenderer.BuildStandalone(timeline, entry), static () => { }));
                host.Content = root;
                var button = Assert.Single(FindLogical<Button>(root));
                Assert.Equal("ChatToolOpenDetails_tool_1", AutomationProperties.GetAutomationId(button));
                Assert.Equal("Open details: powershell. Done", AutomationProperties.GetName(button));
                Assert.True(button.IsTabStop);
                Assert.True(button.MinHeight >= 32);
                Assert.All(FindLogical<FrameworkElement>(root), element =>
                {
                    Assert.DoesNotContain("run-7", AutomationProperties.GetName(element), StringComparison.Ordinal);
                    Assert.DoesNotContain("run-7", AutomationProperties.GetAutomationId(element), StringComparison.Ordinal);
                });
                Assert.All(FindLogical<TextBlock>(root), text =>
                    Assert.DoesNotContain("run-7", text.Text, StringComparison.Ordinal));
                Assert.All(FindLogical<RichTextBlock>(root), text =>
                    Assert.DoesNotContain("run-7", CollectText(text), StringComparison.Ordinal));
                Assert.Null(requested);
                var peer = new ButtonAutomationPeer(button);
                Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
            });
            await _ui.YieldToRenderAsync();
            Assert.Equal(
                new ChatToolDetailRequest(
                    "reactor-tool-activity-proof", 9, "tool-1", "run-7", 4, "tool-call-7", "powershell"),
                requested);
            await _ui.RunOnUIAsync(() =>
            {
                host!.Content = null;
                host.Dispose();
                host = new ReactorHostControl();
                var root = Assert.IsAssignableFrom<UIElement>(host.Reconciler.Mount(
                    ToolCallCardRenderer.BuildPanelDetails(entry), static () => { }));
                host.Content = root;
                Assert.Equal(new[] { input, output },
                    FindLogical<RichTextBlock>(root).Select(CollectText));
                Assert.All(FindLogical<RichTextBlock>(root), text =>
                {
                    Assert.True(text.IsTextSelectionEnabled);
                    Assert.Empty(text.Blocks.OfType<Paragraph>()
                        .SelectMany(paragraph => paragraph.Inlines.OfType<Hyperlink>()));
                });
                Assert.All(FindLogical<ScrollViewer>(root), scroll =>
                {
                    Assert.Equal(240, scroll.MaxHeight);
                    Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
                });
                Assert.Empty(FindLogical<Button>(root));
            });
        }
        finally
        {
            await _ui.RunOnUIAsync(() =>
            {
                if (host is not null)
                {
                    host.Content = null;
                    host.Dispose();
                }
                themeScope?.Dispose();
            });
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("selected-session", false)]
    public async Task OpenDetails_WithoutExplicitSessionOrHostDoesNotExposeAction(
        string? sessionKey, bool hasHost)
    {
        ReactorHostControl? host = null;
        ChatThemeProofScope? themeScope = null;
        try
        {
            await _ui.RunOnUIAsync(() =>
            {
                themeScope = new ChatThemeProofScope("Light");
                host = new ReactorHostControl();
                var entry = Tool("tool-1", "read_file", "output", ChatToolCallStatus.Success);
                var timeline = BuildTimeline([entry], generation: 1) with
                {
                    SessionId = sessionKey,
                    OnOpenToolDetails = hasHost ? _ => throw new InvalidOperationException("Not invoked") : null,
                };
                var root = Assert.IsAssignableFrom<UIElement>(host.Reconciler.Mount(
                    ToolCallCardRenderer.BuildStandalone(timeline, entry), static () => { }));
                host.Content = root;
                Assert.Empty(FindLogical<Button>(root));
                Assert.Contains(FindLogical<RichTextBlock>(root), text => CollectText(text) == "output");
            });
        }
        finally
        {
            await _ui.RunOnUIAsync(() =>
            {
                if (host is not null)
                {
                    host.Content = null;
                    host.Dispose();
                }
                themeScope?.Dispose();
            });
        }
    }

    [Fact]
    public async Task ConsecutiveTools_RenderCompactActivityWithAccessibleExpansion()
    {
        await _ui.ResetContainerAsync();
        var timeline = BuildTimeline(
        [
            Tool("tool-1", "powershell", "first output", ChatToolCallStatus.Success),
            Tool("tool-2", "read_file", "second output", ChatToolCallStatus.Success),
        ], generation: 1) with { OnOpenToolDetails = _ => { } };
        var activityRow = Assert.Single(
            ChatToolActivityPresentation.Project(
                timeline.Entries,
                timeline.SessionId,
                timeline.TimelineGeneration,
                timeline.ShowToolCalls),
            row => row.IsActivityGroup);
        var expansionState = new ChatToolActivityExpansionState();
        ReactorHostControl? host = null;
        UIElement? root = null;
        Element? currentElement = null;
        var disposing = false;
        ChatThemeProofScope? themeScope = null;
        try
        {
            await _ui.RunOnUIAsync(() =>
            {
                themeScope = new ChatThemeProofScope("Light");
                // Reconcile native controls without attaching them to the VSTest visual tree,
                // where applying the unpackaged Expander template crashes Microsoft.UI.Xaml.
                host = new ReactorHostControl();
                Action? reconcile = null;
                Action requestRender = () =>
                {
                    if (!disposing)
                        _ui.Dispatcher.TryEnqueue(() => reconcile!());
                };
                reconcile = () =>
                {
                    if (disposing)
                        return;

                    var nextElement = ToolCallCardRenderer.BuildActivity(
                        timeline,
                        activityRow,
                        expansionState);
                    root = host.Reconciler.Reconcile(
                        currentElement!,
                        nextElement,
                        root!,
                        requestRender);
                    currentElement = nextElement;
                    host.Content = root;
                };
                currentElement = ToolCallCardRenderer.BuildActivity(
                    timeline,
                    activityRow,
                    expansionState);
                root = host.Reconciler.Mount(currentElement, requestRender);
                host.Content = root;
            });

            await _ui.RunOnUIAsync(() =>
            {
                var activity = Assert.Single(
                    FindLogical<Expander>(root!),
                    expander => AutomationProperties.GetAutomationId(expander) == "ChatToolActivity_tool_1");
                Assert.False(activity.IsExpanded);
                var name = AutomationProperties.GetName(activity);
                Assert.Contains("Activity:", name, StringComparison.Ordinal);
                Assert.Contains("2 tools", name, StringComparison.Ordinal);
                Assert.Contains("Collapsed", name, StringComparison.Ordinal);
                Assert.DoesNotContain(
                    FindLogical<Expander>(root!),
                    expander => AutomationProperties.GetAutomationId(expander).StartsWith(
                        "ChatToolCall_",
                        StringComparison.Ordinal));

                activity.IsExpanded = true;
            });
            await DrainReactorQueueAsync();

            await _ui.RunOnUIAsync(() =>
            {
                Assert.True(expansionState.IsExpanded(
                    activityRow.Key,
                    activityRow.Summary!,
                    timeline.ToolCallsCollapseVersion));
                var expanders = FindLogical<Expander>(root!).ToArray();
                var activity = Assert.Single(
                    expanders,
                    expander => AutomationProperties.GetAutomationId(expander) == "ChatToolActivity_tool_1");
                Assert.True(activity.IsExpanded);
                Assert.Contains("Expanded", AutomationProperties.GetName(activity), StringComparison.Ordinal);

                var nested = expanders
                    .Where(expander => AutomationProperties.GetAutomationId(expander).StartsWith(
                        "ChatToolCall_",
                        StringComparison.Ordinal))
                    .ToArray();
                Assert.Equal(2, nested.Length);
                var detailActionNames = FindLogical<Button>(root!)
                    .Select(AutomationProperties.GetName).ToArray();
                Assert.Equal(2, detailActionNames.Distinct(StringComparer.Ordinal).Count());
                Assert.Contains(detailActionNames, name => name.EndsWith("Tool 1", StringComparison.Ordinal));
                Assert.Contains(detailActionNames, name => name.EndsWith("Tool 2", StringComparison.Ordinal));
                Assert.DoesNotContain(
                    FindExpandedLogical<RichTextBlock>(root!),
                    text => text.IsTextSelectionEnabled);
                Assert.All(nested, expander =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(expander)));
                    expander.IsExpanded = true;
                });

                var selectableOutputs = FindExpandedLogical<RichTextBlock>(root!)
                    .Where(text => text.IsTextSelectionEnabled)
                    .Select(CollectText)
                    .ToArray();
                Assert.Contains("first output", selectableOutputs);
                Assert.Contains("second output", selectableOutputs);
                activity.IsExpanded = false;
            });
            await DrainReactorQueueAsync();

            await _ui.RunOnUIAsync(() =>
            {
                Assert.False(expansionState.IsExpanded(
                    activityRow.Key,
                    activityRow.Summary!,
                    timeline.ToolCallsCollapseVersion));
                var activity = Assert.Single(
                    FindLogical<Expander>(root!),
                    expander => AutomationProperties.GetAutomationId(expander) == "ChatToolActivity_tool_1");
                Assert.False(activity.IsExpanded);
                Assert.Contains("Collapsed", AutomationProperties.GetName(activity), StringComparison.Ordinal);
                Assert.DoesNotContain(
                    FindLogical<Expander>(root!),
                    expander => AutomationProperties.GetAutomationId(expander).StartsWith(
                        "ChatToolCall_",
                        StringComparison.Ordinal));
            });
        }
        finally
        {
            if (host is not null)
            {
                await _ui.RunOnUIAsync(() =>
                {
                    disposing = true;
                    if (root is not null && currentElement is not null)
                    {
                        var empty = Empty();
                        root = host.Reconciler.Reconcile(
                            currentElement,
                            empty,
                            root,
                            static () => { });
                        currentElement = empty;
                    }
                    host.Content = null;
                    host.Dispose();
                    themeScope?.Dispose();
                });
            }
        }
    }

    private static ChatTimelinePresentationContext BuildTimeline(
        IReadOnlyList<ChatTimelineItem> entries,
        long generation) =>
        new(
            SessionId: "reactor-tool-activity-proof",
            Entries: entries,
            HasMoreHistory: false,
            OnLoadMoreHistory: null,
            TimelineGeneration: generation,
            ShowToolCalls: true);

    private static ChatTimelineItem Tool(
        string id,
        string name,
        string output,
        ChatToolCallStatus result) =>
        new(
            id,
            ChatTimelineItemKind.ToolCall,
            name,
            ToolName: name,
            ToolResult: result,
            ToolOutput: output);

    private static string CollectText(RichTextBlock richTextBlock)
    {
        var text = new System.Text.StringBuilder();
        foreach (var paragraph in richTextBlock.Blocks.OfType<Paragraph>())
        {
            foreach (var inline in paragraph.Inlines)
                AppendInlineText(inline, text);
        }

        return text.ToString();
    }

    private static void AppendInlineText(Inline inline, System.Text.StringBuilder text)
    {
        switch (inline)
        {
            case Run run:
                text.Append(run.Text);
                break;
            case Span span:
                foreach (var child in span.Inlines)
                    AppendInlineText(child, text);
                break;
            case LineBreak:
                text.Append('\n');
                break;
        }
    }

    private static IEnumerable<T> FindExpandedLogical<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T self)
            yield return self;

        IEnumerable<DependencyObject> children = root switch
        {
            Panel panel => panel.Children,
            Border border when border.Child is not null => [border.Child],
            ScrollViewer scrollViewer when scrollViewer.Content is DependencyObject content => [content],
            Expander expander when expander.IsExpanded && expander.Content is DependencyObject content => [content],
            Microsoft.UI.Xaml.Controls.Expander => [],
            ContentControl contentControl when contentControl.Content is DependencyObject content => [content],
            _ => [],
        };

        foreach (var child in children)
        {
            foreach (var descendant in FindExpandedLogical<T>(child))
                yield return descendant;
        }
    }

    private async Task DrainReactorQueueAsync()
    {
        for (var pass = 0; pass < 3; pass++)
            await _ui.YieldToRenderAsync();
    }

}
