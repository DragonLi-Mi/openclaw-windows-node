using OpenClaw.Shared;

namespace OpenClawTray.Chat;

internal sealed record ConversationFileOrigin(string? Root, SessionFileSource? Source);
internal sealed record ConversationFileDraftKey(ChatConversationIdentity Identity, SessionFileRequest Request,
    ConversationFileOrigin Origin);
internal sealed record ConversationFileDraft(
    ConversationFileDraftKey Key, string SavedText, string Text, string Hash, ConversationFileText Format,
    int SelectionStart = 0, int SelectionLength = 0, double ScrollOffset = 0, string? PendingSaveText = null);

/// <summary>Window-owned memory only. Reconnect remounts may retain edits, never transport authorization.</summary>
public sealed class ConversationFileDrafts
{
    private readonly object _gate = new();
    private readonly Dictionary<ConversationFileDraftKey, (ConversationFileDraft Draft, long Order)> _drafts = [];
    private readonly Dictionary<ChatConversationIdentity, (SessionFileRequest Request, long Order)> _selection = [];
    private long _order;
    private long _characters;

    internal ConversationFileDraft? Find(ChatConversationIdentity identity, SessionFileRequest request)
    {
        lock (_gate)
            return _drafts.Values.Where(entry => entry.Draft.Key.Identity == identity && entry.Draft.Key.Request == request)
                .OrderByDescending(entry => entry.Order).Select(entry => entry.Draft).FirstOrDefault();
    }

    internal bool Retain(ConversationFileDraft draft)
    {
        lock (_gate)
        {
            if (draft.Text.Length > SessionFileLimits.PreviewBytes ||
                draft.SavedText.Length > SessionFileLimits.PreviewBytes)
                return false;
            var replacing = _drafts.TryGetValue(draft.Key, out var previous);
            var characters = _characters - (replacing ? Size(previous.Draft) : 0) + Size(draft);
            if ((!replacing && _drafts.Count >= SessionFileLimits.RetainedDraftCount) ||
                characters > SessionFileLimits.RetainedDraftCharacters)
                return false;
            _drafts[draft.Key] = (draft, ++_order);
            _characters = characters;
            return true;
        }
    }

    internal void Remove(ConversationFileDraftKey key)
    {
        lock (_gate)
            if (_drafts.Remove(key, out var removed)) _characters -= Size(removed.Draft);
    }

    internal void ReleaseClean(ConversationFileDraftKey key)
    {
        lock (_gate)
            if (_drafts.TryGetValue(key, out var entry) && entry.Draft.PendingSaveText is null &&
                entry.Draft.Text == entry.Draft.SavedText)
                Remove(key);
    }

    internal void UpdatePosition(ConversationFileDraftKey key, int start, int length, double scroll)
    {
        lock (_gate)
            if (_drafts.TryGetValue(key, out var entry))
                _drafts[key] = (entry.Draft with { SelectionStart = start, SelectionLength = length, ScrollOffset = scroll },
                    entry.Order);
    }
    internal bool Contains(ConversationFileDraftKey key) { lock (_gate) return _drafts.ContainsKey(key); }

    internal void Select(ChatConversationIdentity identity, SessionFileRequest request)
    {
        lock (_gate)
        {
            if (!_selection.ContainsKey(identity) && _selection.Count >= SessionFileLimits.RetainedDraftCount)
                _selection.Remove(_selection.MinBy(entry => entry.Value.Order).Key);
            _selection[identity] = (request, ++_order);
        }
    }

    internal SessionFileRequest? Selected(ChatConversationIdentity identity)
    {
        lock (_gate) return _selection.TryGetValue(identity, out var selected) ? selected.Request : null;
    }

    public void Clear()
    {
        lock (_gate) { _drafts.Clear(); _selection.Clear(); _characters = 0; _order = 0; }
    }

    private static long Size(ConversationFileDraft draft) =>
        (long)draft.Text.Length + draft.SavedText.Length + (draft.PendingSaveText?.Length ?? 0);
}
