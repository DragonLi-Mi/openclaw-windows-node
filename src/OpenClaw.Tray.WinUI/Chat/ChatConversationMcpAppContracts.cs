using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

internal enum ChatConversationMcpAppPhase { Waiting, Loading, Loaded, Ready, Unavailable, Blocked, Error, Expired }
internal enum ChatConversationMcpAppNotice { None, Busy, RateLimited, Rejected, Error, Unknown }
internal enum ChatConversationMcpAppActionKind { Message, File, Link }
internal enum ChatConversationMcpAppEventKind { ResourceUpdated, ResourceListChanged, ModelContextChanged, ConversationInputRequested }

internal sealed record ChatConversationMcpAppAction(
    ChatConversationCanvasSelection Selection, ChatConversationPanelOperation Operation)
{
    internal long Generation => Operation.Generation;
    internal bool IsCurrent => Operation.IsCurrent && Operation.Context.Identity == Selection.Owner &&
        Operation.RunId == Selection.RunId && Operation.PanelId == ChatConversationCanvasController.PanelId;
}

internal sealed record ChatConversationMcpAppFile(string Path, string? Name);
internal sealed record ChatConversationMcpAppConfirmation(
    ChatConversationMcpAppAction Owner, ChatConversationMcpAppActionKind Kind,
    ConversationMcpAppMessage? Message = null, string? Path = null, Uri? Link = null);

/// <summary>
/// Root must confirm in native chrome and return actual acceptance from each owner.
/// Implementations fence Owner.Operation before and after every await, including a new-chat handoff.
/// File paths remain Gateway paths for the conversation Files owner, never OS paths.
/// </summary>
internal interface IChatConversationMcpAppActions
{
    Task<bool> ConfirmAsync(ChatConversationMcpAppConfirmation request, CancellationToken cancellationToken);
    Task<bool> AcceptMessageAsync(ChatConversationMcpAppAction owner, ConversationMcpAppMessage message,
        CancellationToken cancellationToken);
    Task<bool> AcceptFileAsync(ChatConversationMcpAppAction owner, ChatConversationMcpAppFile file,
        CancellationToken cancellationToken);
    // Plugin routes go to the authoritative native discovery/navigation owner, never an OS URI launcher.
    Task<bool> OpenLinkAsync(ChatConversationMcpAppAction owner, ConversationMcpAppLink link, CancellationToken cancellationToken);
    bool SetDisplayMode(ChatConversationMcpAppAction owner, string mode);
    void ReturnToConversation(ChatConversationMcpAppAction owner);
}

/// <summary>Exact authenticated owner delivery, not an independent connection/event registry.</summary>
internal sealed record ChatConversationMcpAppEvent(
    ChatConversationIdentity Owner, long ConnectionEpoch, string? RunId, string ViewId,
    ChatConversationMcpAppEventKind Kind, JsonElement Parameters);

internal interface IChatConversationMcpAppEvents
{
    IDisposable Subscribe(ChatConversationMcpAppAction owner, string viewId, Action<ChatConversationMcpAppEvent> receive);
}

internal sealed record ChatConversationMcpAppSnapshot(
    ChatConversationMcpAppPhase Phase,
    ChatConversationPanelOperation? Operation = null,
    ConversationMcpAppView? View = null,
    ChatConversationMcpAppNotice Notice = ChatConversationMcpAppNotice.None,
    string DisplayMode = "inline", double Height = 320);

internal static class ChatConversationMcpAppIntegration
{
    // Intentionally no fallback to BeginOperation(panelId, runId). That API cannot bind the selected view.
    // Replace only after the canonical UserId/AccountRevision/ContentId foundation is adopted.
    internal static ChatConversationPanelOperation? AcquireContentOperation(
        ChatConversationPanelState panels, ChatConversationCanvasSelection selection) => null;
}
