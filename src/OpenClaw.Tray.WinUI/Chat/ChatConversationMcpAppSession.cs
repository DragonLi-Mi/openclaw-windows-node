using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

/// <summary>One render's UI protocol and effect lifetime. No connection resolver or generic RPC.</summary>
internal sealed class ChatConversationMcpAppSession : IDisposable
{
    private const int MaximumInflightRequests = 32;
    private const int DefaultTeardownTimeoutMilliseconds = 250;
    private readonly ChatConversationMcpAppAction _owner;
    private readonly ConversationMcpAppView _view;
    private readonly ConversationMcpAppScope _scope;
    private readonly IChatConversationMcpAppActions _actions;
    private readonly Func<bool> _isInteractable;
    private readonly Func<bool> _isEffectVisible;
    private readonly Action<JsonElement> _send;
    private readonly Action<ChatConversationMcpAppNotice> _notice;
    private readonly Action<string, double> _presentation;
    private readonly Action _initialized;
    private readonly CancellationTokenSource _lifetime;
    private readonly Dictionary<string, CancellationTokenSource> _inflight = new(StringComparer.Ordinal);
    private readonly ChatConversationMcpAppMountLease _mount;
    private readonly Func<TimeSpan, Task> _waitForTeardown;
    private readonly Queue<DateTimeOffset> _effectTimes = new();
    private readonly object _gate = new();
    private IDisposable? _events;
    private JsonElement _hostContext;
    private ConversationMcpAppHandshakeState _phase;
    private IReadOnlyList<string> _displayModes = ["inline"];
    private string _displayMode = "inline";
    private double _height = 320;
    private long _contextRevision;
    private bool _effectInFlight;
    private bool _disposed;
    private int _activeHandlers;
    private bool _retirementFinished;
    private readonly TaskCompletionSource _retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _retirement;
    private string? _teardownId;
    private readonly TaskCompletionSource _teardownResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ChatConversationMcpAppSession(ChatConversationMcpAppAction owner, ConversationMcpAppView view,
        IChatConversationMcpAppActions actions, IChatConversationMcpAppEvents events,
        Func<bool> isInteractable, Action<JsonElement> send, Action<ChatConversationMcpAppNotice> notice,
        Action<string, double> presentation, Action initialized, ChatConversationMcpAppMountLease mount,
        Func<TimeSpan, Task>? waitForTeardown = null, Func<bool>? isEffectVisible = null)
    {
        _owner = owner;
        _view = view;
        _scope = new(owner.Selection.Owner.SessionKey, owner.Selection.Content.McpApp!.ViewId,
            owner.Selection.Owner.AgentId);
        _actions = actions;
        _isInteractable = isInteractable;
        _isEffectVisible = isEffectVisible ?? isInteractable;
        _send = send;
        _notice = notice;
        _presentation = presentation;
        _initialized = initialized;
        _mount = mount;
        _mount.AttachSession(RetireAsync);
        _waitForTeardown = waitForTeardown ?? (static delay => Task.Delay(delay));
        _hostContext = view.HostContext ?? JsonSerializer.SerializeToElement(new { });
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(owner.Operation.CancellationToken);
        // Subscribe only after initialized. The seam must deliver on the owning UI dispatcher.
        _eventSource = events;
    }

    private readonly IChatConversationMcpAppEvents _eventSource;
    internal ConversationMcpAppHandshakeState Phase => _phase;
    internal bool IsCurrent => !_disposed && !_lifetime.IsCancellationRequested && _owner.IsCurrent && _mount.IsCurrent;
    private ConversationMcpAppAccess Access =>
        ConversationMcpAppAccess.FromScopes(_owner.Operation.Context.Client.GrantedOperatorScopes);

    internal async Task ReceiveAsync(JsonElement packet, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!IsCurrent) return;
            _activeHandlers++;
        }
        try { await ReceiveCoreAsync(packet, now); }
        finally
        {
            lock (_gate)
            {
                _activeHandlers--;
                if (_retirementFinished && _activeHandlers == 0) _lifetime.Dispose();
            }
        }
    }

    private async Task ReceiveCoreAsync(JsonElement packet, DateTimeOffset now)
    {
        if (!IsCurrent) return;
        if (packet.GetRawText().Length > ConversationMcpAppHostDocument.MaximumPacketCharacters)
        { _notice(ChatConversationMcpAppNotice.Rejected); return; }
        // No host request is outstanding in the steady-state protocol. Unsolicited responses are dropped.
        if (packet.ValueKind == JsonValueKind.Object && !packet.TryGetProperty("method", out _)) return;
        ConversationMcpAppUiRequest request;
        try { request = ConversationMcpAppProtocol.ParseRequest(packet); }
        catch (ConversationMcpAppProtocolException ex)
        {
            _notice(ChatConversationMcpAppNotice.Rejected);
            if (ValidId(packet) is { } id) Error(id, ex.Code, ex.Message);
            return;
        }
        CancellationTokenSource? pending = null;
        var key = request.Id is { } requestId ? IdKey(requestId) : null;
        lock (_gate)
        {
            if (!IsCurrent) return;
            if (key is not null)
            {
                // Do not respond under an occupied id: that would resolve the original request incorrectly.
                if (_inflight.ContainsKey(key)) { _notice(ChatConversationMcpAppNotice.Rejected); return; }
                if (_inflight.Count >= MaximumInflightRequests)
                { _notice(ChatConversationMcpAppNotice.Busy); Error(request.Id!.Value, -32000, "The App host is busy."); return; }
                pending = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _inflight.Add(key, pending);
            }
        }
        try
        {
            ConversationMcpAppProtocol.RequireAvailable(request, _view, Access, _phase);
            var result = await DispatchAsync(request, pending?.Token ?? _lifetime.Token, now);
            if (request.Id is { } id && IsCurrent && pending?.IsCancellationRequested != true)
                Send(new { jsonrpc = "2.0", id, result });
        }
        catch (ConversationMcpAppProtocolException ex)
        {
            if (IsCurrent)
            {
                _notice(ChatConversationMcpAppNotice.Rejected);
                if (request.Id is { } id) Error(id, ex.Code, ex.Message);
            }
        }
        catch (GatewayDataReadRetiredException) { SourceRetired?.Invoke(); Dispose(); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            var expired = ex is GatewayRequestException { Code: "UNAVAILABLE", Details: { ValueKind: JsonValueKind.Object } details } &&
                details.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String &&
                code.GetString() == "MCP_APP_VIEW_EXPIRED";
            var unknown = IsMutation(request.Method) && ex is not (GatewayRequestException or NotSupportedException);
            if (IsCurrent)
            {
                _notice(unknown ? ChatConversationMcpAppNotice.Unknown : ChatConversationMcpAppNotice.Error);
                if (request.Id is { } id)
                    Error(id, expired ? -32004 : -32000,
                        unknown ? "The action outcome is unknown. It was not retried." :
                        expired ? "This App view has expired." : "The App operation could not complete.");
                if (expired) Expired?.Invoke();
            }
            Logger.Warn($"[ConversationMcpApp] Scoped operation ended ({ex.GetType().Name}).");
        }
        finally
        {
            if (key is not null) lock (_gate)
            {
                _inflight.Remove(key);
            }
            pending?.Dispose();
        }
    }

    internal event Action? Expired;
    internal event Action? SourceRetired;

    private async Task<JsonElement> DispatchAsync(ConversationMcpAppUiRequest request,
        CancellationToken token, DateTimeOffset now)
    {
        switch (request.Method)
        {
            case ConversationMcpAppUiMethod.Initialize:
                ConversationMcpAppProtocol.ValidateInitialize(request.Parameters);
                var modes = ConversationMcpAppProtocol.NegotiateDisplayModes(_view,
                    request.Parameters.GetProperty("appCapabilities"));
                _displayModes = modes.Available;
                _displayMode = _view.DisplayMode is { } mode && modes.Available.Contains(mode) ? mode : modes.Initial;
                _phase = ConversationMcpAppHandshakeState.AwaitingInitialized;
                return ConversationMcpAppProtocol.InitializeResult(request.Parameters, _view, Access,
                    "OpenClaw Companion", "1.0.0", BuildHostContext());
            case ConversationMcpAppUiMethod.Initialized:
                if (!_actions.SetDisplayMode(_owner, _displayMode))
                    throw new ConversationMcpAppProtocolException(-32000, "The App display mode could not be applied.");
                if (!IsCurrent) return Empty();
                _phase = ConversationMcpAppHandshakeState.Ready;
                _presentation(_displayMode, _height);
                _events = _eventSource.Subscribe(_owner, _scope.ViewId, ReceiveEvent);
                if (!IsCurrent) { _events.Dispose(); return Empty(); }
                Notify("ui/notifications/host-context-changed", BuildHostContext());
                Notify("ui/notifications/tool-input", new
                { arguments = _view.ToolInput is { ValueKind: JsonValueKind.Object } input ? input : Empty() });
                if (_view.ToolResult is { } toolResult) Notify("ui/notifications/tool-result", toolResult);
                _initialized();
                if (_view.UpdateModelContextSupported) await RefreshModelContextAsync();
                return Empty();
            case ConversationMcpAppUiMethod.Ping:
                return Empty();
            case ConversationMcpAppUiMethod.Cancelled:
                if (request.Parameters.TryGetProperty("requestId", out var cancelled) &&
                    (cancelled.ValueKind == JsonValueKind.String ||
                    cancelled.ValueKind == JsonValueKind.Number && cancelled.TryGetInt64(out _)))
                    lock (_gate)
                        if (_inflight.TryGetValue(IdKey(cancelled), out var cancellation)) cancellation.Cancel();
                return Empty();
            case ConversationMcpAppUiMethod.Message:
                var message = ConversationMcpAppProtocol.ReadMessage(request);
                return await ConfirmedAsync(new(_owner, ChatConversationMcpAppActionKind.Message, Message: message),
                    () => _actions.AcceptMessageAsync(_owner, message, token), token, now);
            case ConversationMcpAppUiMethod.OpenLink:
                var raw = String(request.Parameters, "url");
                var link = ConversationMcpAppProtocol.ReadLink(raw);
                return await ConfirmedAsync(new(_owner, ChatConversationMcpAppActionKind.Link, Link: link.Url),
                    () => _actions.OpenLinkAsync(_owner, link, token), token, now);
            case ConversationMcpAppUiMethod.OpenFile:
                var path = String(request.Parameters, "path");
                return await ConfirmedAsync(new(_owner, ChatConversationMcpAppActionKind.File, Path: path),
                    async () =>
                    {
                        var result = await InvokeAsync(ConversationMcpAppOperation.OpenFile,
                            ConversationMcpAppProtocol.GatewayParameters(request), token);
                        if (!IsCurrent || !Access.CanRead || !_isEffectVisible() || token.IsCancellationRequested)
                            return ChatConversationContentAdmission.Stale;
                        var file = new ChatConversationMcpAppFile(String(result, "path"),
                            result.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null);
                        return await _actions.AcceptFileAsync(_owner, file, token);
                    }, token, now);
            case ConversationMcpAppUiMethod.RequestDisplayMode:
                var requestedMode = String(request.Parameters, "mode");
                if (_displayModes.Contains(requestedMode) && IsCurrent && _actions.SetDisplayMode(_owner, requestedMode))
                {
                    _displayMode = requestedMode;
                    _presentation(_displayMode, _height);
                    Notify("ui/notifications/host-context-changed", BuildHostContext());
                }
                return JsonSerializer.SerializeToElement(new { mode = _displayMode });
            case ConversationMcpAppUiMethod.SizeChanged:
                if (request.Parameters.TryGetProperty("height", out var height) && height.TryGetDouble(out var number) &&
                    double.IsFinite(number) && _displayMode == "inline")
                {
                    _height = Math.Clamp(Math.Round(number), 160, 1200);
                    _presentation(_displayMode, _height);
                    Notify("ui/notifications/host-context-changed", BuildHostContext());
                }
                return Empty();
            case ConversationMcpAppUiMethod.RequestTeardown:
                _actions.ReturnToConversation(_owner);
                Dispose();
                return Empty();
            case ConversationMcpAppUiMethod.UpdateModelContext:
                ConversationMcpAppProtocol.ValidateContextUpdate(request.Parameters, _view.RichModelContextSupported);
                break;
        }
        var operation = ConversationMcpAppProtocol.GatewayOperation(request.Method) ??
            throw new ConversationMcpAppProtocolException(-32601, "This host method is not available.");
        var parameters = ConversationMcpAppProtocol.GatewayParameters(request);
        var uri = operation is ConversationMcpAppOperation.SubscribeResource or ConversationMcpAppOperation.UnsubscribeResource
            ? String(parameters, "uri") : null;
        var result = uri is not null
            ? await _mount.SubscriptionAsync(operation, parameters, token)
            : await InvokeAsync(operation, parameters, token);
        if (!IsCurrent) return Empty();
        ConversationMcpAppProtocol.ValidateGatewayResult(operation, result);
        if (operation == ConversationMcpAppOperation.UpdateModelContext && IsCurrent)
            await RefreshModelContextAsync();
        return result;
    }

    private async Task<JsonElement> ConfirmedAsync(ChatConversationMcpAppConfirmation confirmation,
        Func<Task<ChatConversationContentAdmission>> accept, CancellationToken token, DateTimeOffset now)
    {
        if (!IsCurrent || !_isInteractable()) return Reject();
        lock (_gate)
        {
            while (_effectTimes.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromMinutes(1)) _effectTimes.Dequeue();
            if (_effectInFlight) { _notice(ChatConversationMcpAppNotice.Busy); return Rejected(); }
            if (_effectTimes.Count >= 10) { _notice(ChatConversationMcpAppNotice.RateLimited); return Rejected(); }
            _effectInFlight = true;
            _effectTimes.Enqueue(now);
        }
        try
        {
            if (!await _actions.ConfirmAsync(confirmation, token) || !IsCurrent ||
                token.IsCancellationRequested || !_isEffectVisible()) return Reject();
            if (confirmation.Kind == ChatConversationMcpAppActionKind.Message && !Access.CanWrite)
                return Reject();
            var admission = await accept();
            if (!IsCurrent || token.IsCancellationRequested) admission = admission.AfterOwnerChange();
            if (IsCurrent)
                _notice(admission.Kind == ChatConversationContentAdmissionKind.Unconfirmed
                    ? ChatConversationMcpAppNotice.Unknown : admission.HasCustody
                    ? admission.Kind == ChatConversationContentAdmissionKind.Queued
                        ? ChatConversationMcpAppNotice.Queued : ChatConversationMcpAppNotice.None
                    : ChatConversationMcpAppNotice.Rejected);
            return AdmissionResult(admission);
        }
        finally { lock (_gate) _effectInFlight = false; }
    }

    private Task<JsonElement> InvokeAsync(ConversationMcpAppOperation operation, JsonElement parameters, CancellationToken token)
    {
        if (!IsCurrent) throw new OperationCanceledException();
        if (!Access.CanRead || (operation is ConversationMcpAppOperation.CallTool or
            ConversationMcpAppOperation.WriteResource or ConversationMcpAppOperation.UpdateModelContext) && !Access.CanWrite)
            throw new ConversationMcpAppProtocolException(-32001, "This App capability is not available.");
        return _owner.Operation.Context.Client.InvokeConversationMcpAppAsync(_scope, operation, parameters,
            _owner.Operation.Context.ConnectionEpoch, token);
    }

    internal void UpdatePresentation(string theme, double width, double height)
    {
        if (!IsCurrent || !double.IsFinite(width) || !double.IsFinite(height)) return;
        var context = JsonNode.Parse(_hostContext.GetRawText())!.AsObject();
        context["theme"] = theme == "dark" ? "dark" : "light";
        context["containerDimensions"] = JsonSerializer.SerializeToNode(new
        {
            width = Math.Max(0, width), height = Math.Max(0, height)
        });
        _hostContext = JsonSerializer.SerializeToElement(context);
        if (_phase == ConversationMcpAppHandshakeState.Ready)
            Notify("ui/notifications/host-context-changed", BuildHostContext());
    }

    private JsonElement BuildHostContext()
    {
        var context = JsonNode.Parse(_hostContext.GetRawText())!.AsObject();
        context["displayMode"] = _displayMode;
        context["availableDisplayModes"] = JsonSerializer.SerializeToNode(_displayModes);
        return JsonSerializer.SerializeToElement(context);
    }

    private async Task RefreshModelContextAsync()
    {
        var revision = ++_contextRevision;
        try
        {
            var result = await InvokeAsync(ConversationMcpAppOperation.GetModelContext, Empty(), _lifetime.Token);
            if (!IsCurrent || revision != _contextRevision) return;
            var context = JsonNode.Parse(_hostContext.GetRawText())!.AsObject();
            context["openai/modelContext"] = result.TryGetProperty("state", out var state) ? JsonNode.Parse(state.GetRawText()) : null;
            _hostContext = JsonSerializer.SerializeToElement(context);
            Notify("ui/notifications/host-context-changed", BuildHostContext());
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException or TimeoutException or NotSupportedException)
        { if (IsCurrent) _notice(ChatConversationMcpAppNotice.Error); }
    }

    private void ReceiveEvent(ChatConversationMcpAppEvent delivery)
    {
        if (!IsCurrent || _phase != ConversationMcpAppHandshakeState.Ready ||
            delivery.Source is null || !delivery.Source.IsCurrent ||
            !ReferenceEquals(delivery.Source.Client, _owner.Operation.Context.Client) ||
            delivery.Source.ConnectionEpoch != _mount.Source.ConnectionEpoch ||
            delivery.Source.ProfileDataRevision != _mount.Source.ProfileDataRevision ||
            delivery.Owner != _owner.Selection.Owner || delivery.ConnectionEpoch != _owner.Operation.Context.ConnectionEpoch ||
            delivery.AccountRevision != _owner.Operation.Context.AccountRevision ||
            delivery.RunId != _owner.Selection.RunId || delivery.ViewId != _scope.ViewId ||
            delivery.Parameters.ValueKind != JsonValueKind.Object) return;
        switch (delivery.Kind)
        {
            case ChatConversationMcpAppEventKind.ResourceUpdated when _view.FileResourcesSupported:
                if (delivery.Parameters.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String &&
                    _mount.Contains(uri.GetString()!))
                    Notify("notifications/resources/updated", delivery.Parameters);
                break;
            case ChatConversationMcpAppEventKind.ResourceListChanged when _view.MessageSupported:
                Notify("notifications/resources/list_changed", Empty());
                break;
            case ChatConversationMcpAppEventKind.ModelContextChanged when _view.UpdateModelContextSupported:
                if (delivery.Parameters.TryGetProperty("modelContext", out var cleared) && cleared.ValueKind == JsonValueKind.Null &&
                    delivery.Parameters.TryGetProperty("updateId", out var updateId) && updateId.ValueKind == JsonValueKind.String)
                {
                    if (_hostContext.TryGetProperty("openai/modelContext", out var current) &&
                        current.ValueKind == JsonValueKind.Object && current.TryGetProperty("updateId", out var currentId) &&
                        currentId.ValueKind == JsonValueKind.String &&
                        currentId.GetString() != updateId.GetString()) return;
                    _contextRevision++;
                    var context = JsonNode.Parse(_hostContext.GetRawText())!.AsObject();
                    context["openai/modelContext"] = null;
                    _hostContext = JsonSerializer.SerializeToElement(context);
                    Notify("ui/notifications/host-context-changed", BuildHostContext());
                    return;
                }
                AsyncEventHandlerGuard.Run(RefreshModelContextAsync, operationName: "McpAppModelContext",
                    onError: _ => { if (IsCurrent) _notice(ChatConversationMcpAppNotice.Error); });
                break;
            case ChatConversationMcpAppEventKind.ConversationInputRequested:
                if (_isInteractable())
                {
                    if (_displayModes.Contains("inline") && _actions.SetDisplayMode(_owner, "inline"))
                    {
                        _displayMode = "inline";
                        _presentation(_displayMode, _height);
                        Notify("ui/notifications/host-context-changed", BuildHostContext());
                    }
                    _actions.ReturnToConversation(_owner);
                }
                break;
        }
    }

    private void Notify(string method, object parameters) => Send(new { jsonrpc = "2.0", method, @params = parameters });
    private void Error(JsonElement id, int code, string message) => Send(new { jsonrpc = "2.0", id, error = new { code, message } });
    private void Send(object packet) { if (IsCurrent) _send(JsonSerializer.SerializeToElement(packet)); }
    private static JsonElement Empty() => JsonSerializer.SerializeToElement(new { });
    private static JsonElement Rejected() => JsonSerializer.SerializeToElement(new { isError = true });
    private static JsonElement AdmissionResult(ChatConversationContentAdmission admission)
    {
        var result = new JsonObject
        {
            ["_meta"] = new JsonObject
            {
                ["openclaw/acceptance"] = new JsonObject
                {
                    ["status"] = admission.Kind.ToString().ToLowerInvariant(),
                    ["ownsPayload"] = admission.OwnsPayload,
                    ["error"] = admission.Kind == ChatConversationContentAdmissionKind.Unconfirmed
                        ? "The action outcome is unknown. It was not retried." : admission.Error
                }
            }
        };
        if (!admission.HasCustody) result["isError"] = true;
        return JsonSerializer.SerializeToElement(result);
    }
    private JsonElement Reject()
    {
        if (IsCurrent) _notice(ChatConversationMcpAppNotice.Rejected);
        return Rejected();
    }
    private static string IdKey(JsonElement id) => id.ValueKind == JsonValueKind.String
        ? "s:" + id.GetString() : "n:" + id.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static JsonElement? ValidId(JsonElement packet) =>
        packet.ValueKind == JsonValueKind.Object && packet.TryGetProperty("id", out var id) &&
        (id.ValueKind == JsonValueKind.String || id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out _)) ? id : null;
    private static string String(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) &&
        property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString()) ? property.GetString()! :
            throw new ConversationMcpAppProtocolException(-32602, "Invalid App request parameters.");
    private static bool IsMutation(ConversationMcpAppUiMethod method) => method is
        ConversationMcpAppUiMethod.CallTool or ConversationMcpAppUiMethod.WriteResource or
        ConversationMcpAppUiMethod.UpdateModelContext or ConversationMcpAppUiMethod.Message or
        ConversationMcpAppUiMethod.OpenFile or ConversationMcpAppUiMethod.OpenLink;

    internal Task RetireAsync()
    {
        lock (_gate)
        {
            if (_retirement is not null) return _retirement;
            _phase = ConversationMcpAppHandshakeState.Retiring;
            _disposed = true;
            _retirement = _retired.Task;
            AsyncEventHandlerGuard.Run(async () =>
            {
                try { await RetireCoreAsync(); }
                finally
                {
                    _ = _mount.RetireAsync();
                    lock (_gate)
                    {
                        _retirementFinished = true;
                        if (_activeHandlers == 0) _lifetime.Dispose();
                    }
                    _retired.TrySetResult();
                }
            }, operationName: "McpAppTeardown", onError: _ => { });
            return _retirement;
        }
    }

    private async Task RetireCoreAsync()
    {
        _events?.Dispose();
        _events = null;
        string[] pendingKeys;
        lock (_gate) pendingKeys = _inflight.Keys.ToArray();
        _lifetime.Cancel();
        foreach (var key in pendingKeys)
        {
            var id = key.StartsWith("s:", StringComparison.Ordinal)
                ? JsonSerializer.SerializeToElement(key[2..])
                : JsonSerializer.Deserialize<JsonElement>(key[2..]);
            _send(JsonSerializer.SerializeToElement(new
            { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = id, reason = "The App is retiring." } }));
        }
        _teardownId = "host-teardown-" + Guid.NewGuid().ToString("N");
        _send(JsonSerializer.SerializeToElement(new
        { jsonrpc = "2.0", id = _teardownId, method = "ui/resource-teardown", @params = new { } }));
        await Task.WhenAny(_teardownResponse.Task, _waitForTeardown(TeardownTimeout()));
        _teardownId = null;
        // The browser can close now. Same-view readmission still waits for actual subscription cleanup.
    }

    internal void ReceiveRetirementResponse(JsonElement packet)
    {
        if (_phase != ConversationMcpAppHandshakeState.Retiring || _teardownId is null ||
            packet.ValueKind != JsonValueKind.Object || packet.TryGetProperty("method", out _) ||
            !packet.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0" ||
            !packet.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            id.GetString() != _teardownId ||
            !(packet.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object ||
              packet.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)) return;
        _teardownResponse.TrySetResult();
    }

    private static TimeSpan TeardownTimeout() => TimeSpan.FromMilliseconds(
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_MCP_APP_TEARDOWN_TIMEOUT_MS"), out var value) &&
        value is >= 50 and <= 5000 ? value : DefaultTeardownTimeoutMilliseconds);

    public void Dispose() => AsyncEventHandlerGuard.Run(RetireAsync,
        operationName: "McpAppRetirement", onError: exception => { _ = _mount.RetireAsync(); });
}
