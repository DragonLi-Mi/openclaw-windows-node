using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using OpenClaw.Shared;
using OpenClawTray.Services;
using Windows.System;

namespace OpenClawTray.Chat;

/// <summary>Content-only adapter. It never creates a Gateway client, lends credentials, or maps a folder.</summary>
internal sealed class ConversationCanvasWebView : Grid, IDisposable
{
    private const int DefaultLoadTimeoutMilliseconds = 30000;
    private static readonly object RetiringGate = new();
    private static readonly HashSet<CoreWebView2Environment> RetiringEnvironments = [];
    private readonly ChatConversationCanvasController _owner;
    private readonly ChatConversationPanelOperation _operation;
    private readonly CanvasDocumentView _document;
    private readonly Action _returnToConversation;
    private readonly ConversationCanvasBoundary _boundary;
    private readonly ConversationCanvasProfile _profile;
    private readonly WebView2 _view = new();
    private readonly string _messageNonce = Guid.NewGuid().ToString("N");
    private readonly string _scriptNonce = Guid.NewGuid().ToString("N");
    private readonly string _renderId = Guid.NewGuid().ToString("N");
    private readonly KeyboardAccelerator _escape = new() { Key = VirtualKey.Escape };
    private readonly KeyboardAccelerator _cycle = new() { Key = VirtualKey.F6 };
    private CoreWebView2Environment? _environment;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _deadline;
    private ulong? _navigationId;
    private bool _parentPending = true;
    private bool _parentServed;
    private bool _disposed;
    private bool _ready;
    private bool _initializing;
    private bool _documentSent;

    internal ConversationCanvasWebView(ChatConversationCanvasController owner,
        ChatConversationPanelOperation operation, CanvasDocumentView document, Action returnToConversation)
    {
        _owner = owner;
        _operation = operation;
        _document = document;
        _returnToConversation = returnToConversation;
        _boundary = new(document, Guid.NewGuid().ToString("N"), _messageNonce);
        _profile = new(AppIdentity.ResolveSetupLocalDataDirectory());
        AutomationProperties.SetName(_view, owner.Selection.Content.Title ?? "Canvas");
        AutomationProperties.SetAutomationId(_view, "ConversationCanvasDocument");
        Children.Add(_view);
        owner.Invalidated += Invalidate;
        _escape.Invoked += Escape;
        _cycle.Invoked += Cycle;
        _view.KeyboardAccelerators.Add(_escape);
        _view.KeyboardAccelerators.Add(_cycle);
        ActualThemeChanged += ThemeChanged;
        _view.AllowDrop = false;
    }

    internal string ProfileDirectory => _profile.DirectoryPath;
    private bool IsCurrent => !_disposed && _operation.IsCurrent &&
        ReferenceEquals(_owner.Snapshot.Operation, _operation);
    private bool IsInteractable() => IsCurrent && _ready && IsLoaded &&
        Visibility == Visibility.Visible && ActualWidth > 0 && ActualHeight > 0 &&
        FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && IsWithin(focused, _view);

    internal async Task InitializeAsync()
    {
        _initializing = true;
        try
        {
            if (!IsCurrent) return;
            // A process-wide legacy override takes precedence over the explicit environment path.
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER")))
                throw new InvalidOperationException("A shared WebView2 profile override prevents isolated Canvas content.");
            await Task.Run(_profile.Create);
            if (!IsCurrent) { Dispose(); return; }
            _environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, _profile.DirectoryPath,
                new CoreWebView2EnvironmentOptions { ExclusiveUserDataFolderAccess = true });
            _environment.BrowserProcessExited += BrowserExited;
            if (!string.Equals(Path.GetFullPath(_environment.UserDataFolder),
                _profile.DirectoryPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("WebView2 did not select the isolated Canvas profile.");
            if (!IsCurrent) { Dispose(); return; }
            var options = _environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            await _view.EnsureCoreWebView2Async(_environment, options);
            if (!IsCurrent) { Dispose(); return; }
            var core = _view.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            core.NavigationStarting += NavigationStarting;
            core.NavigationCompleted += NavigationCompleted;
            core.FrameNavigationStarting += FrameNavigationStarting;
            core.WebMessageReceived += MessageReceived;
            core.PermissionRequested += PermissionRequested;
            core.DownloadStarting += DownloadStarting;
            core.NewWindowRequested += NewWindowRequested;
            core.BasicAuthenticationRequested += BasicAuthenticationRequested;
            core.ClientCertificateRequested += ClientCertificateRequested;
            core.LaunchingExternalUriScheme += LaunchingExternalUriScheme;
            core.ServerCertificateErrorDetected += CertificateError;
            core.ProcessFailed += ProcessFailed;
            core.WebResourceRequested += ResourceRequested;
            core.AddWebResourceRequestedFilter(
                _boundary.GatewayOrigin.GetLeftPart(UriPartial.Authority) + "/*", CoreWebView2WebResourceContext.All);
            _profile.RecordBrowser((int)core.BrowserProcessId);
            _deadline = DispatcherQueue.CreateTimer();
            _deadline.IsRepeating = false;
            _deadline.Interval = TimeSpan.FromMilliseconds(LoadTimeoutMilliseconds());
            _deadline.Tick += Deadline;
            _deadline.Start();
            core.Navigate(_boundary.ParentUrl.AbsoluteUri);
        }
        catch (COMException ex) { Fail(ex); }
        catch (InvalidOperationException ex) { Fail(ex); }
        catch (IOException ex) { Fail(ex); }
        catch (UnauthorizedAccessException ex) { Fail(ex); }
        catch (ArgumentException ex) { Fail(ex); }
        catch (OperationCanceledException ex) { Fail(ex); }
        finally
        {
            _initializing = false;
            if (_disposed) RetireEnvironment();
        }
    }

    private void NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        args.Cancel = !IsCurrent || !_parentPending || !_boundary.IsParent(args.Uri) || args.IsRedirected;
        if (!args.Cancel) _navigationId = args.NavigationId;
        else if (IsCurrent) _owner.InteractionBlocked();
    }

    private void NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!IsCurrent || _navigationId != args.NavigationId) return;
        if (!args.IsSuccess || !_parentServed) Fail();
    }

    private void FrameNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        args.Cancel = !IsCurrent || !_boundary.MayNavigateFrame(args.Uri);
        if (args.Cancel && IsCurrent) _owner.InteractionBlocked();
    }

    private void ResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (IsCurrent && _boundary.MayServeParent(args.Request.Uri, args.Request.Method, _parentPending))
        {
            _parentPending = false;
            _parentServed = true;
            var html = ConversationCanvasHostDocument.Build(_boundary, _messageNonce,
                _owner.Selection.Content.Title ?? "Canvas", _renderId, _scriptNonce, CurrentTheme);
            args.Response = _environment!.CreateWebResourceResponse(
                new MemoryStream(Encoding.UTF8.GetBytes(html)).AsRandomAccessStream(), 200, "OK",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n" +
                "Referrer-Policy: origin\r\nX-Content-Type-Options: nosniff\r\n" +
                "Permissions-Policy: camera=(), microphone=(), geolocation=(), clipboard-write=()\r\n" +
                $"Content-Security-Policy: {ConversationCanvasHostDocument.ContentSecurityPolicy(_boundary, _scriptNonce)}");
            return;
        }
        args.Response = _environment!.CreateWebResourceResponse(null, 403, "Forbidden", "Cache-Control: no-store");
    }

    private void MessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args) =>
        AsyncEventHandlerGuard.Run(() => HandleMessageAsync(sender, args),
            operationName: nameof(MessageReceived), onError: Fail);

    private async Task HandleMessageAsync(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!IsCurrent || !_boundary.IsParent(args.Source)) return;
        try
        {
            using var parsed = JsonDocument.Parse(args.WebMessageAsJson);
            var message = parsed.RootElement;
            if (!_boundary.IsNativeMessage(args.Source, message) ||
                !message.TryGetProperty("renderId", out var render) || render.ValueKind != JsonValueKind.String ||
                render.GetString() != _renderId ||
                !message.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                return;
            switch (type.GetString())
            {
                case "proxyReady" when !_documentSent:
                    _documentSent = true;
                    sender.PostWebMessageAsJson(JsonSerializer.Serialize(new
                    {
                        type = "document", nonce = _messageNonce, renderId = _renderId,
                        html = _document.Html,
                        allowScripts = ConversationCanvasBoundary.AllowScripts(
                            _owner.Snapshot.EmbedSandboxMode, _owner.Selection.Content)
                    }));
                    break;
                case "rendered" when !_ready && _documentSent:
                    _ready = true;
                    _deadline?.Stop();
                    _owner.SurfaceReady(_operation);
                    break;
                case "surfaceError":
                    Fail();
                    break;
                case "prompt" when _ready && message.TryGetProperty("text", out var text) &&
                                        text.ValueKind == JsonValueKind.String:
                    await _owner.SubmitPromptAsync(text.GetString(), IsInteractable, DateTimeOffset.UtcNow);
                    break;
            }
        }
        catch (JsonException ex) { Logger.Warn($"[ConversationCanvas] Invalid host message ({ex.GetType().Name})."); }
        catch (OperationCanceledException) when (!IsCurrent) { }
        catch (OperationCanceledException ex) { Fail(ex); }
        catch (InvalidOperationException ex) { Fail(ex); }
        catch (COMException ex) { Fail(ex); }
    }

    private void PermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = CoreWebView2PermissionState.Deny;
        args.Handled = true;
        if (IsCurrent) _owner.InteractionBlocked();
    }

    private void DownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
    {
        args.Cancel = true;
        if (IsCurrent) _owner.InteractionBlocked();
    }
    private void NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (IsCurrent) _owner.InteractionBlocked();
    }
    private static void BasicAuthenticationRequested(CoreWebView2 sender, CoreWebView2BasicAuthenticationRequestedEventArgs args) => args.Cancel = true;
    private static void ClientCertificateRequested(CoreWebView2 sender, CoreWebView2ClientCertificateRequestedEventArgs args)
    {
        args.Cancel = true;
        args.Handled = true;
    }
    private void LaunchingExternalUriScheme(CoreWebView2 sender, CoreWebView2LaunchingExternalUriSchemeEventArgs args)
    {
        args.Cancel = true;
        if (IsCurrent) _owner.InteractionBlocked();
    }
    private static void CertificateError(CoreWebView2 sender, CoreWebView2ServerCertificateErrorDetectedEventArgs args) =>
        args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
    private void ProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args) => Fail();

    private void Deadline(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => Fail();
    private string CurrentTheme => ActualTheme == ElementTheme.Dark ? "dark" : "light";
    private void ThemeChanged(FrameworkElement sender, object args)
    {
        if (IsCurrent && _parentServed && _view.CoreWebView2 is { } core)
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                nonce = _messageNonce, renderId = _renderId, type = "theme", mode = CurrentTheme
            }));
    }
    private static int LoadTimeoutMilliseconds() =>
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_CANVAS_LOAD_TIMEOUT_MS"), out var value) &&
        value is >= 1000 and <= 120000 ? value : DefaultLoadTimeoutMilliseconds;

    private void Escape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!IsInteractable()) return;
        args.Handled = true;
        _owner.Close();
        _returnToConversation();
    }

    private void Cycle(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!IsInteractable()) return;
        args.Handled = true;
        _returnToConversation();
    }

    private static bool IsWithin(DependencyObject item, DependencyObject parent)
    {
        for (DependencyObject? current = item; current is not null;
             current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, parent)) return true;
        return false;
    }

    private void Fail(Exception? exception = null)
    {
        if (exception is not null)
            Logger.Warn($"[ConversationCanvas] Isolated content surface failed ({exception.GetType().Name}).");
        if (IsCurrent) _owner.SurfaceFailed(_operation);
        Dispose();
    }

    internal void ReportFailure(Exception exception) => Fail(exception);

    private void Invalidate()
    {
        if (DispatcherQueue.HasThreadAccess) Dispose();
        else DispatcherQueue.TryEnqueue(Dispose);
    }

    private void BrowserExited(CoreWebView2Environment sender, CoreWebView2BrowserProcessExitedEventArgs args)
    {
        sender.BrowserProcessExited -= BrowserExited;
        lock (RetiringGate) RetiringEnvironments.Remove(sender);
        _profile.DeleteAfterBrowserExit();
        if (IsCurrent) _owner.SurfaceFailed(_operation);
        if (!_disposed) Invalidate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Visibility = Visibility.Collapsed;
        _owner.Invalidated -= Invalidate;
        _deadline?.Stop();
        if (_deadline is not null) _deadline.Tick -= Deadline;
        _deadline = null;
        _escape.Invoked -= Escape;
        _cycle.Invoked -= Cycle;
        ActualThemeChanged -= ThemeChanged;
        if (_view.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= NavigationStarting;
            core.NavigationCompleted -= NavigationCompleted;
            core.FrameNavigationStarting -= FrameNavigationStarting;
            core.WebMessageReceived -= MessageReceived;
            core.PermissionRequested -= PermissionRequested;
            core.DownloadStarting -= DownloadStarting;
            core.NewWindowRequested -= NewWindowRequested;
            core.BasicAuthenticationRequested -= BasicAuthenticationRequested;
            core.ClientCertificateRequested -= ClientCertificateRequested;
            core.LaunchingExternalUriScheme -= LaunchingExternalUriScheme;
            core.ServerCertificateErrorDetected -= CertificateError;
            core.ProcessFailed -= ProcessFailed;
            core.WebResourceRequested -= ResourceRequested;
        }
        _view.Close();
        Children.Clear();
        if (!_initializing) RetireEnvironment();
    }

    private void RetireEnvironment()
    {
        if (_environment is null) _profile.DeleteAfterBrowserExit();
        else
        {
            lock (RetiringGate) RetiringEnvironments.Add(_environment);
            try
            {
                if (_environment.GetProcessInfos().Count != 0) return;
                _environment.BrowserProcessExited -= BrowserExited;
                lock (RetiringGate) RetiringEnvironments.Remove(_environment);
                _profile.DeleteAfterBrowserExit();
            }
            catch (COMException ex)
            {
                Logger.Warn($"[ConversationCanvas] Browser exit remains pending ({ex.GetType().Name}).");
            }
        }
    }
}
