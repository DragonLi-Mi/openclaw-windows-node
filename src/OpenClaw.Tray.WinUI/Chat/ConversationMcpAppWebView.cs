using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

/// <summary>MCP JSON-RPC is a different grant from the Canvas private prompt channel.</summary>
internal sealed class ConversationMcpAppWebView : ConversationContentWebView
{
    private readonly ChatConversationMcpAppController _owner;
    private readonly ChatConversationPanelOperation _operation;
    private readonly ConversationMcpAppView _document;
    private readonly ChatConversationMcpAppSession _session;
    private bool _documentSent;
    private bool _rendered;
    private bool _initialized;

    internal ConversationMcpAppWebView(ChatConversationMcpAppController owner,
        ChatConversationPanelOperation operation, ConversationMcpAppView document, Action returnToConversation)
        : base(document.GatewayOrigin, document.SandboxUrl, owner.Selection.Content.Title ?? "Canvas",
            "ConversationMcpAppDocument", returnToConversation)
    {
        _owner = owner;
        _operation = operation;
        _document = document;
        _session = owner.CreateSession(operation, document, IsInteractable, IsEffectVisible,
            packet =>
            {
                var message = new { type = "rpc", nonce = MessageNonce, renderId = RenderId, packet };
                if (_session?.Phase == ConversationMcpAppHandshakeState.Retiring) PostRetirement(message);
                else Post(message);
            },
            () => { _initialized = true; Ready(); });
        owner.Invalidated += Invalidate;
        HostPresentationChanged();
    }

    protected override bool IsOwnerCurrent => _operation.IsCurrent &&
        ReferenceEquals(_owner.Snapshot.Operation, _operation);
    protected override int MaximumBridgeMessageCharacters => ConversationMcpAppHostDocument.MaximumPacketCharacters + 512;
    protected override string BuildHostDocument() => ConversationMcpAppHostDocument.Build(
        Boundary, MessageNonce, _owner.Selection.Content.Title ?? "Canvas", RenderId, ScriptNonce, CurrentTheme);
    protected override void InteractionBlocked() => _owner.Notice(ChatConversationMcpAppNotice.Rejected);
    protected override void SurfaceFailed() => _owner.SurfaceFailed(_operation);
    protected override void ClosePanel() => _owner.Close();
    protected override void OnDisposing()
    {
        _owner.Invalidated -= Invalidate;
    }
    protected override Task RetireContentAsync() => _session.RetireAsync();
    protected override void HandleRetirementMessage(JsonElement message)
    {
        if (message.GetProperty("type").GetString() == "rpc" && message.TryGetProperty("packet", out var packet))
            _session.ReceiveRetirementResponse(packet);
    }
    protected override void HostPresentationChanged() =>
        _session.UpdatePresentation(CurrentTheme, ActualWidth, ActualHeight);

    private void Ready()
    {
        if (!_rendered || !_initialized || !IsCurrent) return;
        MarkReady();
        _owner.SurfaceReady(_operation);
    }

    protected override async Task HandleBridgeMessageAsync(JsonElement message)
    {
        switch (message.GetProperty("type").GetString())
        {
            case "proxyReady" when !_documentSent:
                _documentSent = true;
                Post(new { type = "document", nonce = MessageNonce, renderId = RenderId, html = _document.Html, csp = _document.Csp });
                break;
            case "rendered" when _documentSent && !_rendered:
                _rendered = true;
                Ready();
                break;
            case "surfaceError":
                Fail();
                break;
            case "blocked":
                InteractionBlocked();
                break;
            case "rpc" when _documentSent && message.TryGetProperty("packet", out var packet):
                await _session.ReceiveAsync(packet.Clone(), DateTimeOffset.UtcNow);
                break;
        }
    }
}
