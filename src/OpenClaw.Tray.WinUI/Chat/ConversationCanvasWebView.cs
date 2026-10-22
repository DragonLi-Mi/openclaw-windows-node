using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

/// <summary>Canvas grants only its private conversational-prompt channel, never MCP App RPC.</summary>
internal sealed class ConversationCanvasWebView : ConversationContentWebView
{
    private readonly ChatConversationCanvasController _owner;
    private readonly ChatConversationPanelOperation _operation;
    private readonly CanvasDocumentView _document;
    private bool _documentSent;
    private bool _rendered;

    internal ConversationCanvasWebView(ChatConversationCanvasController owner,
        ChatConversationPanelOperation operation, CanvasDocumentView document, Action returnToConversation)
        : base(document.GatewayOrigin, document.SandboxUrl, owner.Selection.Content.Title ?? "Canvas",
            "ConversationCanvasDocument", returnToConversation)
    {
        _owner = owner;
        _operation = operation;
        _document = document;
        owner.Invalidated += Invalidate;
    }

    protected override bool IsOwnerCurrent => _operation.IsCurrent &&
        ReferenceEquals(_owner.Snapshot.Operation, _operation);
    protected override int MaximumBridgeMessageCharacters => ConversationCanvasBoundary.MaximumPromptCharacters * 6 + 1024;
    protected override string BuildHostDocument() => ConversationCanvasHostDocument.Build(
        Boundary, MessageNonce, _owner.Selection.Content.Title ?? "Canvas", RenderId, ScriptNonce, CurrentTheme);
    protected override void InteractionBlocked() => _owner.InteractionBlocked();
    protected override void SurfaceFailed() => _owner.SurfaceFailed(_operation);
    protected override void ClosePanel() => _owner.Close();
    protected override void OnDisposing() => _owner.Invalidated -= Invalidate;

    protected override async Task HandleBridgeMessageAsync(JsonElement message)
    {
        switch (message.GetProperty("type").GetString())
        {
            case "proxyReady" when !_documentSent:
                _documentSent = true;
                Post(new
                {
                    type = "document", nonce = MessageNonce, renderId = RenderId, html = _document.Html,
                    allowScripts = ConversationCanvasBoundary.AllowScripts(
                        _owner.Snapshot.EmbedSandboxMode, _owner.Selection.Content)
                });
                break;
            case "rendered" when !_rendered && _documentSent:
                _rendered = true;
                MarkReady();
                _owner.SurfaceReady(_operation);
                break;
            case "surfaceError":
                Fail();
                break;
            case "prompt" when _rendered && message.TryGetProperty("text", out var text) &&
                                    text.ValueKind == JsonValueKind.String:
                var outcome = await _owner.SubmitPromptAsync(text.GetString(), IsInteractable, DateTimeOffset.UtcNow);
                if (IsCurrent && outcome != ChatConversationCanvasPromptResult.Accepted)
                    _owner.InteractionBlocked();
                break;
        }
    }
}
