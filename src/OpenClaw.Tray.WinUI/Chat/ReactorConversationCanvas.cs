using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Wrappers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ReactorConversationCanvasProps(
    ChatConversationPanelState State,
    ChatConversationCanvasSelection Selection,
    Func<ChatConversationCanvasPrompt, CancellationToken, Task<ChatConversationCanvasPromptResult>> SubmitPrompt,
    Action ReturnToConversation);

[GenerateReactorWrapper(typeof(ConversationCanvasMount), AutoDiscover = false, RegisterAssembly = false)]
internal partial record ConversationCanvasMountElement;

internal sealed class ReactorConversationCanvas : Component<ReactorConversationCanvasProps>
{
    public override Element Render()
    {
        var props = Props;
        var owner = UseRef<ChatConversationCanvasController?>(null);
        var (_, setRevision) = UseState(0L, threadSafe: true);
        UseEffect((Func<Action>)(() =>
        {
            var controller = new ChatConversationCanvasController(props.State, props.Selection, props.SubmitPrompt);
            owner.Current = controller;
            long revision = 0;
            void Changed() => setRevision(Interlocked.Increment(ref revision));
            controller.Changed += Changed;
            _ = controller.LoadAsync();
            return () =>
            {
                controller.Changed -= Changed;
                controller.Dispose();
                if (ReferenceEquals(owner.Current, controller)) owner.Current = null;
            };
        }), props.State, props.Selection);

        var controller = owner.Current;
        var snapshot = controller?.Selection == props.Selection
            ? controller.Snapshot : new(ChatConversationCanvasPhase.Waiting);
        var current = snapshot.Operation?.IsCurrent == true;
        var hasDocument = current && snapshot.Document is not null;
        var status = snapshot.Phase switch
        {
            ChatConversationCanvasPhase.Loading or ChatConversationCanvasPhase.Loaded => "Loading",
            ChatConversationCanvasPhase.Unsupported => props.Selection.Content.Kind switch
            {
                ConversationCanvasContentKind.McpApp => "PluginUnsupported",
                ConversationCanvasContentKind.HostedPreview => "PreviewUnsupported",
                _ => "Unsupported"
            },
            ChatConversationCanvasPhase.Blocked => "Blocked",
            ChatConversationCanvasPhase.Error => "Error",
            ChatConversationCanvasPhase.Ready => "Ready",
            _ => "Waiting"
        };
        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Star()],
            HStack(8,
                Button(TextBlock(LocalizationHelper.GetString("Chat_Placement_Retry")),
                    () => { if (owner.Current is { } active) _ = active.LoadAsync(); })
                    .AutomationName(LocalizationHelper.GetString("Chat_Placement_Retry"))
                    .AutomationId("ConversationCanvasRetry")
                    .Resources(ChatVisuals.ToolbarButtonResources).MinWidth(40).MinHeight(40),
                TextBlock(Text(status)).TextWrapping(TextWrapping.Wrap)
                    .VAlign(VerticalAlignment.Center)
                    .Set(text =>
                    {
                        text.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
                        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
                    })
                    .AutomationId("ConversationCanvasStatus"))
                .Margin(12, 8).Grid(row: 0),
            TextBlock(Text("ScriptsRestricted")).TextWrapping(TextWrapping.Wrap)
                .Margin(16, 0, 16, 8)
                .IsVisible(hasDocument && !ConversationCanvasBoundary.AllowScripts(
                    snapshot.EmbedSandboxMode, props.Selection.Content))
                .AutomationId("ConversationCanvasScriptsRestricted")
                .Grid(row: 1),
            TextBlock(Text("InteractionBlocked")).TextWrapping(TextWrapping.Wrap)
                .Margin(16, 0, 16, 8).IsVisible(snapshot.InteractionBlocked)
                .AutomationId("ConversationCanvasInteractionBlocked")
                .Grid(row: 2),
            (hasDocument && controller is not null
                ? ConversationCanvasMountElement.ConversationCanvasMount()
                    .Set(mount => mount.Update(controller, snapshot.Operation!, snapshot.Document!, props.ReturnToConversation))
                    .OnUnmount(control => ((ConversationCanvasMount)control).Dispose())
                    .WithKey($"canvas-document-{snapshot.Operation!.Generation}")
                : Empty())
                .Grid(row: 3))
            .AutomationId("ConversationCanvas")
            .AutomationName(LocalizationHelper.GetString("WindowTitle_Canvas"));
    }

    private static string Text(string key) => LocalizationHelper.GetString("Chat_Canvas_" + key);
}

internal sealed class ConversationCanvasMount : Control, IDisposable
{
    private ConversationCanvasWebView? _surface;
    private ContentPresenter? _presenter;
    private ChatConversationCanvasController? _owner;
    private ChatConversationPanelOperation? _operation;
    private CanvasDocumentView? _document;
    private bool _started;

    public ConversationCanvasMount()
    {
        Template = (ControlTemplate)Application.Current.Resources["ChatConversationCanvasTemplate"];
        IsTabStop = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _presenter = (ContentPresenter)GetTemplateChild("CanvasContentRoot");
        _presenter.Content = _surface;
    }

    internal void Update(ChatConversationCanvasController owner, ChatConversationPanelOperation operation,
        CanvasDocumentView document, Action returnToConversation)
    {
        if (ReferenceEquals(_operation, operation) && ReferenceEquals(_document, document)) return;
        Retire();
        if (!operation.IsCurrent) return;
        _operation = operation;
        _document = document;
        _owner = owner;
        _returnToConversation = returnToConversation;
        if (IsLoaded) Start();
    }

    private Action? _returnToConversation;
    private void OnLoaded(object sender, RoutedEventArgs args) => Start();
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        RetireSurface();
        if (_operation is { } operation) _owner?.SurfaceSuspended(operation);
    }
    private void Start()
    {
        if (_started || _owner is null || _operation?.IsCurrent != true ||
            _document is null || _returnToConversation is null) return;
        _surface = new(_owner, _operation, _document, _returnToConversation);
        if (_presenter is not null) _presenter.Content = _surface;
        _started = true;
        _ = _surface.InitializeAsync();
    }

    private void RetireSurface()
    {
        _surface?.Dispose();
        _surface = null;
        if (_presenter is not null) _presenter.Content = null;
        _started = false;
    }

    private void Retire()
    {
        RetireSurface();
        _owner = null;
        _operation = null;
        _document = null;
        _returnToConversation = null;
    }

    public void Dispose()
    {
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        Retire();
    }
}
