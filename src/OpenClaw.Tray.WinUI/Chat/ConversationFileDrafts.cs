using OpenClaw.Shared;

namespace OpenClawTray.Chat;

internal sealed record ConversationFileOrigin(string? Root, SessionFileSource? Source);
internal sealed record ConversationFileDraftKey(ChatConversationIdentity Identity, SessionFileRequest Request,
    ConversationFileOrigin Origin);
internal sealed record ConversationFileDraft(
    ConversationFileDraftKey Key, string SavedText, string Text, string Hash, ConversationFileText Format,
    int SelectionStart = 0, int SelectionLength = 0, double ScrollOffset = 0);

/// <summary>Window-owned memory only. Reconnect remounts may retain edits, never transport authorization.</summary>
public sealed class ConversationFileDrafts
{
    private readonly object _gate = new();
    private readonly Dictionary<ConversationFileDraftKey, ConversationFileDraft> _drafts = [];
    private readonly Dictionary<ChatConversationIdentity, SessionFileRequest> _selection = [];

    internal ConversationFileDraft? Find(ChatConversationIdentity identity, SessionFileRequest request)
    {
        lock (_gate)
            return _drafts.Values.LastOrDefault(draft => draft.Key.Identity == identity && draft.Key.Request == request);
    }

    internal bool Retain(ConversationFileDraft draft)
    {
        lock (_gate)
        {
            if (draft.Text.Length > SessionFileLimits.PreviewBytes ||
                draft.SavedText.Length > SessionFileLimits.PreviewBytes)
                return false;
            var others = _drafts.Values.Where(d => d.Key != draft.Key).ToArray();
            if (others.Length >= SessionFileLimits.RetainedDraftCount ||
                others.Sum(d => (long)d.Text.Length + d.SavedText.Length) + draft.Text.Length +
                    draft.SavedText.Length > SessionFileLimits.RetainedDraftCharacters)
                return false;
            _drafts[draft.Key] = draft;
            return true;
        }
    }

    internal void Remove(ConversationFileDraftKey key) { lock (_gate) _drafts.Remove(key); }
    internal bool Contains(ConversationFileDraftKey key) { lock (_gate) return _drafts.ContainsKey(key); }

    internal void Select(ChatConversationIdentity identity, SessionFileRequest request)
    {
        lock (_gate)
        {
            if (!_selection.ContainsKey(identity) && _selection.Count >= SessionFileLimits.RetainedDraftCount)
                _selection.Remove(_selection.Keys.First());
            _selection[identity] = request;
        }
    }

    internal SessionFileRequest? Selected(ChatConversationIdentity identity)
    {
        lock (_gate) return _selection.GetValueOrDefault(identity);
    }

    public void Clear()
    {
        lock (_gate) { _drafts.Clear(); _selection.Clear(); }
    }
}
