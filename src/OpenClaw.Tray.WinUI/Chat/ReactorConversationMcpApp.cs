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

internal sealed record ReactorConversationMcpAppProps(ChatConversationPanelState State,
    ChatConversationCanvasSelection Selection, IChatConversationMcpAppActions? Actions,
    IChatConversationMcpAppEvents? Events, Action ReturnToConversation);

[GenerateReactorWrapper(typeof(ConversationMcpAppMount), AutoDiscover = false, RegisterAssembly = false)]
internal partial record ConversationMcpAppMountElement;

internal sealed class ReactorConversationMcpApp : Component<ReactorConversationMcpAppProps>
{
    public override Element Render()
    {
        var props = Props;
        var owner = UseRef<ChatConversationMcpAppController?>(null);
        var (_, setRevision) = UseState(0L, threadSafe: true);
        UseEffect((Func<Action>)(() =>
        {
            var controller = new ChatConversationMcpAppController(props.State, props.Selection, props.Actions, props.Events);
            owner.Current = controller;
            long revision = 0;
            void Changed() => setRevision(Interlocked.Increment(ref revision));
            controller.Changed += Changed;
            Load(controller);
            return () =>
            {
                controller.Changed -= Changed;
                controller.Dispose();
                if (ReferenceEquals(owner.Current, controller)) owner.Current = null;
            };
        }), props);
        var controller = owner.Current;
        var snapshot = controller?.Selection == props.Selection ? controller.Snapshot :
            new(ChatConversationMcpAppPhase.Waiting);
        var mount = snapshot.Operation?.IsCurrent == true && snapshot.View is not null;
        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Star()],
            HStack(8,
                Button(TextBlock(LocalizationHelper.GetString("Chat_Placement_Retry")),
                    () => { if (owner.Current is { } active) Load(active); })
                    .AutomationId("ConversationMcpAppRetry")
                    .AutomationName(LocalizationHelper.GetString("Chat_Placement_Retry"))
                    .Resources(ChatVisuals.ToolbarButtonResources).MinWidth(40).MinHeight(40),
                TextBlock(Text(snapshot.Phase.ToString())).TextWrapping(TextWrapping.Wrap)
                    .Set(text =>
                    {
                        text.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
                        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
                    })
                    .AutomationId("ConversationMcpAppStatus"))
                .Margin(12, 8).Grid(row: 0),
            TextBlock(snapshot.Notice == ChatConversationMcpAppNotice.None ? "" : Text(snapshot.Notice.ToString())).TextWrapping(TextWrapping.Wrap)
                .Margin(16, 0, 16, 8).IsVisible(snapshot.Notice != ChatConversationMcpAppNotice.None)
                .AutomationId("ConversationMcpAppNotice").Grid(row: 1),
            (mount && controller is not null
                ? ConversationMcpAppMountElement.ConversationMcpAppMount()
                    .Set(control => control.Update(controller, snapshot.Operation!, snapshot.View!, props.ReturnToConversation))
                    .Height(snapshot.DisplayMode == "inline" ? snapshot.Height : double.NaN)
                    .OnUnmount(control => ((ConversationMcpAppMount)control).Dispose())
                    .WithKey($"mcp-app-{snapshot.Operation!.Generation}")
                : Empty()).Grid(row: 2))
            .AutomationId("ConversationMcpApp")
            .AutomationName(LocalizationHelper.GetString("WindowTitle_Canvas"));
    }

    private static string Text(string key) => LocalizationHelper.GetString("Chat_McpApp_" + key);
    private static void Load(ChatConversationMcpAppController owner) => AsyncEventHandlerGuard.Run(
        owner.LoadAsync, operationName: "ConversationMcpAppRead", onError: owner.UnexpectedFailure);
}

internal sealed class ConversationMcpAppMount : Control, IDisposable
{
    private ConversationMcpAppWebView? _surface;
    private ContentPresenter? _presenter;
    private ChatConversationMcpAppController? _owner;
    private ChatConversationPanelOperation? _operation;
    private ConversationMcpAppView? _document;
    private Action? _return;
    public ConversationMcpAppMount()
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
    internal void Update(ChatConversationMcpAppController owner, ChatConversationPanelOperation operation,
        ConversationMcpAppView document, Action returnToConversation)
    {
        if (ReferenceEquals(operation, _operation) && ReferenceEquals(document, _document)) return;
        RetireSurface();
        _owner = owner;
        _operation = operation;
        _document = document;
        _return = returnToConversation;
        if (IsLoaded) Start();
    }
    private void Start()
    {
        if (_surface is not null || _owner is null || _operation?.IsCurrent != true ||
            _document is null || _return is null) return;
        var surface = new ConversationMcpAppWebView(_owner, _operation, _document, _return);
        _surface = surface;
        if (_presenter is not null) _presenter.Content = surface;
        AsyncEventHandlerGuard.Run(surface.InitializeAsync,
            operationName: "ConversationMcpAppInitialize", onError: surface.ReportFailure);
    }
    private void OnLoaded(object sender, RoutedEventArgs args) => Start();
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        RetireSurface();
        if (_operation is { } operation) _owner?.SurfaceSuspended(operation);
    }
    private void RetireSurface()
    {
        _surface?.Dispose();
        _surface = null;
        if (_presenter is not null) _presenter.Content = null;
    }
    public void Dispose()
    {
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        RetireSurface();
        _owner = null;
        _operation = null;
        _document = null;
        _return = null;
    }
}
