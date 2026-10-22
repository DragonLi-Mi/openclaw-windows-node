using System.Net.Http;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

internal enum ConversationFilesStatus { Waiting, Loading, Ready, Unsupported, Error }
internal enum ConversationFilesNotice
{
    None, Offline, Rejected, Permission, Conflict, SourceChanged, InvalidContent, DraftLimit,
    Restored, Saved, DownloadFailed, Downloaded, Uncertain
}
internal enum ConversationFilesTab { Browse, Outputs, Changes }

internal sealed record ConversationFileDocument(
    SessionFileRequest Request, SessionFileContent Snapshot, ConversationFileOrigin Origin,
    string? Text, string? SavedText, ConversationFileText? Format,
    string? ExpectedHash = null,
    bool Editing = false, bool Dirty = false, bool Restored = false,
    string? LatestText = null, string? LatestHash = null, bool SourceChanged = false,
    int SelectionStart = 0, int SelectionLength = 0, double ScrollOffset = 0)
{
    internal ConversationFileDraftKey Key(ChatConversationIdentity identity) => new(identity, Request, Origin);
}

internal sealed record ConversationFilesView(
    ConversationFilesStatus Status = ConversationFilesStatus.Waiting,
    ConversationFilesTab Tab = ConversationFilesTab.Browse, string Path = "", string Search = "",
    SessionFileList? Listing = null, ConversationFileDocument? Document = null, SessionDiff? Diff = null,
    SessionDiffScope DiffScope = SessionDiffScope.All, string? Commit = null,
    ConversationFilesNotice Notice = ConversationFilesNotice.None, bool Busy = false);

internal sealed class ConversationFileDestination(
    Stream stream, Func<Task> commit, Func<ValueTask> dispose) : IAsyncDisposable
{
    internal Stream Stream { get; } = stream;
    internal Task CommitAsync() => commit();
    public ValueTask DisposeAsync() => dispose();
}

/// <summary>One mounted consumer. Only the injected window store survives a client/root replacement.</summary>
internal sealed class ConversationFilesController : IDisposable
{
    internal const string PanelId = "files";
    private readonly object _gate = new();
    private readonly ChatConversationPanelState _panels;
    private readonly ConversationFileDrafts _drafts;
    private ChatConversationContext? _binding;
    private long _revision;
    private bool _disposed;
    private ConversationFilesView _view = new();

    internal ConversationFilesController(ChatConversationPanelState panels, ConversationFileDrafts drafts)
    {
        _panels = panels;
        _drafts = drafts;
        panels.Changed += BindingChanged;
        BindingChanged();
    }

    internal event Action? Changed;
    internal ConversationFilesView View { get { lock (_gate) return _view; } }
    internal ChatConversationPanelOperation? CaptureLease() => _panels.BeginOperation(PanelId);
    internal ConversationFileDraftKey? EditorTarget
    {
        get { lock (_gate) return _binding is { } context && _view.Document is { } document
            ? document.Key(context.Identity) : null; }
    }
    internal SessionFileScope? CurrentScope => _panels.IsReady && _panels.Context is { Identity.AgentId: { } agent } context
        ? new(context.Identity.SessionKey, agent) : null;
    internal SessionFilesCapabilities Capabilities => _panels.IsReady && _panels.Context is { } context
        ? context.Client.SessionFilesCapabilities : new(SessionFilesReadiness.Waiting);
    internal bool CanEdit
    {
        get
        {
            lock (_gate)
                return _panels.IsReady && _panels.Context == _binding && !_view.Busy &&
                    _view.Notice != ConversationFilesNotice.Permission &&
                    Capabilities.Set && _binding!.Client.GrantedOperatorScopes.Contains("operator.admin", StringComparer.Ordinal) &&
                    _view.Document is { Request.MessageEntryId: null, SourceChanged: false, Format.Uniform: true,
                        Snapshot.Source: not null, Snapshot.PreviewKind: "text", Snapshot.ContentEncoding: "utf8",
                        ExpectedHash: { } hash, Text: not null } &&
                    SessionFileLimits.IsContentHash(hash) &&
                    _view.Document.Format.ToNative(_view.Document.Text).Length <= SessionFileLimits.NativeEditorCharacters;
        }
    }

    private void BindingChanged()
    {
        var context = _panels.IsReady ? _panels.Context : null;
        bool load;
        lock (_gate)
        {
            if (_disposed) return;
            var active = context is not null && _panels.Layout.SelectedPanel == PanelId;
            load = active && (_binding != context || _view.Status == ConversationFilesStatus.Waiting);
            if (!active || _binding != context)
            {
                _revision++;
                _binding = active ? context : null;
                _view = new(Notice: context is null ? ConversationFilesNotice.Offline : ConversationFilesNotice.None);
            }
        }
        Changed?.Invoke();
        if (load) _ = RefreshAsync();
    }

    internal Task RefreshAsync() => LoadListingAsync(View.Path, View.Search);

    internal async Task LoadListingAsync(string path = "", string search = "")
    {
        var operation = Begin();
        if (operation is null) return;
        var (lease, revision) = operation.Value;
        if (Capabilities.Readiness == SessionFilesReadiness.Waiting)
        {
            Apply(lease, revision, v => v with { Status = ConversationFilesStatus.Waiting });
            return;
        }
        var scope = Scope(lease);
        if (scope is null) { Apply(lease, revision, v => v with { Status = ConversationFilesStatus.Waiting }); return; }
        Apply(lease, revision, v => v with { Status = ConversationFilesStatus.Loading, Path = path, Search = search,
            Notice = ConversationFilesNotice.None });
        try
        {
            var listing = await lease.Context.Client.ListSessionFilesAsync(scope, lease.Context.ConnectionEpoch,
                path, search, lease.CancellationToken).ConfigureAwait(false);
            Apply(lease, revision, v => v with { Status = ConversationFilesStatus.Ready, Listing = listing });
            if (IsCurrent(lease, revision) && View.Document is null &&
                _drafts.Selected(lease.Context.Identity) is { } selected)
                await OpenAsync(selected).ConfigureAwait(false);
        }
        catch (Exception error) when (IsFileError(error)) { Fail(lease, revision, error); }
    }

    internal async Task OpenAsync(SessionFileRequest request, string? runId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate) { if (_disposed || _view.Busy) return; }
        var previous = _panels.BeginOperation(PanelId, runId);
        if (previous is null || request.Scope != Scope(previous)) return;
        _panels.Open(PanelId, FileContentId(request), runId ?? previous.RunId);
        var operation = Begin(runId);
        if (operation is null) return;
        var (lease, revision) = operation.Value;
        if (request.Scope != Scope(lease))
        {
            Apply(lease, revision, v => v with { Notice = ConversationFilesNotice.Rejected });
            return;
        }
        Apply(lease, revision, v => v with { Status = ConversationFilesStatus.Loading,
            Document = null, Notice = ConversationFilesNotice.None });
        try
        {
            var snapshot = await lease.Context.Client.GetSessionFileAsync(request, lease.Context.ConnectionEpoch,
                lease.CancellationToken).ConfigureAwait(false);
            var text = ReadText(snapshot);
            var origin = new ConversationFileOrigin(snapshot.Root, snapshot.Source);
            var document = new ConversationFileDocument(request, snapshot, origin, text, text,
                text is null ? null : ConversationFileText.Inspect(text), snapshot.Hash);
            var draft = _drafts.Find(lease.Context.Identity, request);
            var notice = ConversationFilesNotice.None;
            if (draft is not null && draft.Text != draft.SavedText)
            {
                var sourceChanged = draft.Key.Origin != origin;
                var conflict = sourceChanged || draft.Hash != snapshot.Hash && draft.Text != text;
                document = document with
                {
                    Origin = draft.Key.Origin, Text = draft.Text, SavedText = draft.SavedText, Format = draft.Format,
                    ExpectedHash = draft.Hash,
                    Editing = true, Dirty = true, Restored = true, SourceChanged = sourceChanged,
                    Snapshot = snapshot, LatestText = conflict && !sourceChanged ? text : null,
                    LatestHash = conflict && !sourceChanged ? snapshot.Hash : null,
                    SelectionStart = draft.SelectionStart, SelectionLength = draft.SelectionLength,
                    ScrollOffset = draft.ScrollOffset
                };
                notice = sourceChanged ? ConversationFilesNotice.SourceChanged :
                    conflict ? ConversationFilesNotice.Conflict : ConversationFilesNotice.Restored;
            }
            if (Apply(lease, revision, v => v with { Status = ConversationFilesStatus.Ready, Document = document, Notice = notice }))
                _drafts.Select(lease.Context.Identity, request);
        }
        catch (Exception error) when (IsFileError(error)) { Fail(lease, revision, error); }
    }

    internal void SelectTab(ConversationFilesTab tab)
    {
        lock (_gate) _view = _view with { Tab = tab };
        Changed?.Invoke();
        if (tab == ConversationFilesTab.Changes) _ = LoadDiffAsync(View.DiffScope, View.Commit);
    }

    internal void BeginEdit()
    {
        lock (_gate)
        {
            if (!CanEdit || _view.Document is not { } document) return;
            if (!_drafts.Retain(Draft(document, _binding!.Identity)))
            {
                _view = _view with { Notice = ConversationFilesNotice.DraftLimit };
                Changed?.Invoke();
                return;
            }
            _view = _view with { Document = document with { Editing = true } };
        }
        Changed?.Invoke();
    }

    internal void UpdateNativeText(ConversationFileDraftKey target, string text, int selectionStart, int selectionLength,
        double scrollOffset = 0)
    {
        lock (_gate)
        {
            if (_disposed || _view.Document is not { Editing: true, Format.Uniform: true } document ||
                _binding is null || document.Key(_binding.Identity) != target) return;
            var content = document.Format.FromNative(text);
            var dirty = content != document.SavedText;
            var next = document with { Text = content, Dirty = dirty, SelectionStart = selectionStart,
                SelectionLength = selectionLength, ScrollOffset = scrollOffset };
            var draft = Draft(next, _binding.Identity);
            if (!_drafts.Retain(draft))
            {
                // The native editor retains the rejected text. Never silently replace it with a smaller buffer.
                _view = _view with { Document = next, Notice = ConversationFilesNotice.DraftLimit };
            }
            else
            {
                _view = _view with { Document = next };
            }
        }
        Changed?.Invoke();
    }

    internal async Task SaveAsync(bool overwrite = false)
    {
        ConversationFileDocument document;
        (ChatConversationPanelOperation Lease, long Revision)? operation;
        lock (_gate)
        {
            if (!CanEdit || _view.Document is not { Dirty: true, Text: not null } current) return;
            operation = Begin();
            if (operation is null) return;
            document = current;
            _view = _view with { Busy = true, Notice = ConversationFilesNotice.None };
        }
        var (lease, revision) = operation.Value;
        Changed?.Invoke();
        var capturedText = document.Text!;
        try
        {
            // Every save revalidates the original authority, including after remount and permission changes.
            var latest = await lease.Context.Client.GetSessionFileAsync(document.Request,
                lease.Context.ConnectionEpoch, lease.CancellationToken).ConfigureAwait(false);
            if (!IsCurrent(lease, revision)) return;
            if (new ConversationFileOrigin(latest.Root, latest.Source) != document.Origin)
            {
                Apply(lease, revision, v => v with { Busy = false, Notice = ConversationFilesNotice.SourceChanged,
                    Document = v.Document! with { SourceChanged = true } });
                return;
            }
            var latestText = ReadText(latest);
            if (latestText is null || !ConversationFileText.Inspect(latestText).Uniform ||
                !SessionFileLimits.IsContentHash(latest.Hash))
                throw new InvalidOperationException("The current file cannot be edited safely.");
            // Overwrite is valid only after the user saw this exact latest version alongside the draft.
            var expectedHash = document.ExpectedHash;
            if (latest.Hash != expectedHash && !overwrite ||
                overwrite && (document.LatestHash is null || latest.Hash != document.LatestHash))
            {
                Apply(lease, revision, v => v with { Busy = false, Notice = ConversationFilesNotice.Conflict,
                    Document = v.Document! with { LatestText = latestText, LatestHash = latest.Hash } });
                return;
            }
            if (!IsCurrent(lease, revision)) return;
            lease.Connection.RequireCurrent();
            var saved = await lease.Context.Client.SetSessionFileAsync(
                new(document.Request.Scope, document.Request.Path, capturedText, latest.Hash!, document.Origin.Source!),
                lease.Context.ConnectionEpoch, lease.CancellationToken).ConfigureAwait(false);
            if (!IsCurrent(lease, revision)) return;
            Apply(lease, revision, v =>
            {
                var current = v.Document!;
                var dirty = current.Text != capturedText;
                var next = current with { Snapshot = saved, ExpectedHash = saved.Hash,
                    SavedText = capturedText, Dirty = dirty, Restored = false,
                    LatestText = null, LatestHash = null };
                _drafts.Retain(Draft(next, lease.Context.Identity));
                return v with { Busy = false, Document = next, Notice = ConversationFilesNotice.Saved };
            });
        }
        catch (GatewayRequestException error) when (error.DetailType == "session_file_conflict")
        {
            Apply(lease, revision, v => v with { Busy = false, Notice = ConversationFilesNotice.Conflict });
            await FetchLatestAsync(lease, revision, document).ConfigureAwait(false);
        }
        catch (Exception error) when (IsFileError(error)) { Fail(lease, revision, error); }
    }

    private async Task FetchLatestAsync(ChatConversationPanelOperation lease, long revision, ConversationFileDocument document)
    {
        try
        {
            var latest = await lease.Context.Client.GetSessionFileAsync(document.Request,
                lease.Context.ConnectionEpoch, lease.CancellationToken).ConfigureAwait(false);
            var changed = new ConversationFileOrigin(latest.Root, latest.Source) != document.Origin;
            Apply(lease, revision, v => v with
            {
                Notice = changed ? ConversationFilesNotice.SourceChanged : ConversationFilesNotice.Conflict,
                Document = v.Document! with { SourceChanged = changed, LatestText = changed ? null : ReadText(latest),
                    LatestHash = changed ? null : latest.Hash }
            });
        }
        catch (Exception error) when (IsFileError(error)) { Fail(lease, revision, error); }
    }

    internal async Task ReloadAsync()
    {
        if (View.Document is not { } document) return;
        var operation = Begin();
        if (operation is null) return;
        var (lease, revision) = operation.Value;
        try
        {
            var latest = await lease.Context.Client.GetSessionFileAsync(document.Request,
                lease.Context.ConnectionEpoch, lease.CancellationToken).ConfigureAwait(false);
            if (new ConversationFileOrigin(latest.Root, latest.Source) != document.Origin)
            {
                Apply(lease, revision, v => v with { Notice = ConversationFilesNotice.SourceChanged });
                return;
            }
            var text = ReadText(latest);
            Apply(lease, revision, v =>
            {
                _drafts.Remove(document.Key(lease.Context.Identity));
                return v with { Document = new(document.Request, latest, document.Origin, text, text,
                    text is null ? null : ConversationFileText.Inspect(text), latest.Hash), Notice = ConversationFilesNotice.None };
            });
        }
        catch (Exception error) when (IsFileError(error)) { Fail(lease, revision, error); }
    }

    internal void Discard()
    {
        lock (_gate)
        {
            if (_binding is null || _view.Busy || _view.Document is not { } document) return;
            _drafts.Remove(document.Key(_binding.Identity));
            _view = _view with { Document = document with { Text = document.SavedText, Editing = false,
                Dirty = false, Restored = false, LatestText = null, LatestHash = null },
                Notice = document.SourceChanged ? ConversationFilesNotice.SourceChanged : ConversationFilesNotice.None };
        }
        Changed?.Invoke();
    }

    internal async Task LoadDiffAsync(SessionDiffScope kind = SessionDiffScope.All, string? commit = null)
    {
        var operation = Begin();
        if (operation is null) return;
        var (lease, revision) = operation.Value;
        if (Scope(lease) is not { } scope) return;
        Apply(lease, revision, v => v with { Status = ConversationFilesStatus.Loading, DiffScope = kind, Commit = commit });
        try
        {
            var diff = await lease.Context.Client.GetSessionDiffAsync(new(scope, kind, commit),
                lease.Context.ConnectionEpoch, lease.CancellationToken).ConfigureAwait(false);
            Apply(lease, revision, v => v with { Status = ConversationFilesStatus.Ready, Diff = diff });
        }
        catch (Exception error) when (IsFileError(error)) { Fail(lease, revision, error); }
    }

    internal async Task DownloadAsync(Func<string, CancellationToken, Task<ConversationFileDestination?>> chooseDestination)
    {
        if (View.Document is not { } document) return;
        var operation = Begin();
        if (operation is null) return;
        var (lease, revision) = operation.Value;
        try
        {
            Apply(lease, revision, v => v with { Busy = true, Notice = ConversationFilesNotice.None });
            var grant = await lease.Context.Client.GetSessionFileDownloadAsync(document.Request,
                lease.Context.ConnectionEpoch, lease.CancellationToken).ConfigureAwait(false);
            if (!IsCurrent(lease, revision)) return;
            await using var destination = await chooseDestination(SafeDownloadName(grant.Filename),
                lease.CancellationToken).ConfigureAwait(false);
            if (destination is null) { Apply(lease, revision, v => v with { Busy = false }); return; }
            if (!IsCurrent(lease, revision)) return;
            await lease.Context.Client.DownloadSessionFileAsync(grant, lease.Context.ConnectionEpoch, destination.Stream,
                lease.CancellationToken).ConfigureAwait(false);
            if (!IsCurrent(lease, revision)) return;
            await destination.CommitAsync().ConfigureAwait(false);
            Apply(lease, revision, v => v with { Busy = false, Notice = ConversationFilesNotice.Downloaded });
        }
        catch (Exception error) when (IsFileError(error) || error is IOException)
        {
            if (IsCurrent(lease, revision))
                Apply(lease, revision, v => v with { Busy = false, Notice = ConversationFilesNotice.DownloadFailed });
        }
    }

    private (ChatConversationPanelOperation Lease, long Revision)? Begin(string? runId = null)
    {
        lock (_gate)
        {
            if (_disposed || _view.Busy || _panels.BeginOperation(PanelId, runId) is not { } lease) return null;
            return (lease, ++_revision);
        }
    }

    private bool IsCurrent(ChatConversationPanelOperation lease, long revision)
    {
        lock (_gate) return !_disposed && revision == _revision && lease.IsCurrent && lease.Context == _binding;
    }

    private bool Apply(ChatConversationPanelOperation lease, long revision, Func<ConversationFilesView, ConversationFilesView> apply)
    {
        lock (_gate)
        {
            if (!IsCurrent(lease, revision)) return false;
            _view = apply(_view);
        }
        Changed?.Invoke();
        return true;
    }

    private void Fail(ChatConversationPanelOperation lease, long revision, Exception error)
    {
        Apply(lease, revision, view => view with
        {
            Busy = false,
            Status = error is NotSupportedException ? ConversationFilesStatus.Unsupported :
                view.Document is null ? ConversationFilesStatus.Error : ConversationFilesStatus.Ready,
            Notice = error switch
            {
                GatewayRequestException { DetailType: "session_file_source_changed" } => ConversationFilesNotice.SourceChanged,
                GatewayRequestException { Code: "FORBIDDEN" } or UnauthorizedAccessException => ConversationFilesNotice.Permission,
                GatewayRequestException { DetailType: "session_file_too_large" } => ConversationFilesNotice.InvalidContent,
                TimeoutException or OperationCanceledException => ConversationFilesNotice.Uncertain,
                _ => ConversationFilesNotice.Rejected
            },
            Document = error is GatewayRequestException { DetailType: "session_file_source_changed" } && view.Document is { } doc
                ? doc with { SourceChanged = true } : view.Document
        });
    }

    private static bool IsFileError(Exception error) => error is InvalidOperationException or
        NotSupportedException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or
        JsonException or HttpRequestException or ArgumentException or FormatException;

    private static SessionFileScope? Scope(ChatConversationPanelOperation lease) =>
        string.IsNullOrWhiteSpace(lease.Context.Identity.AgentId) ? null :
            new(lease.Context.Identity.SessionKey, lease.Context.Identity.AgentId);

    private static ConversationFileDraft Draft(ConversationFileDocument document, ChatConversationIdentity identity) =>
        new(document.Key(identity), document.SavedText!, document.Text!, document.ExpectedHash!, document.Format!,
            document.SelectionStart, document.SelectionLength, document.ScrollOffset);

    internal static string? ReadText(SessionFileContent snapshot)
    {
        if (snapshot.Missing || snapshot.PreviewKind is "image" or "unsupported" || snapshot.Content is null) return null;
        if (snapshot.ContentEncoding is null or "utf8")
            return Encoding.UTF8.GetByteCount(snapshot.Content) <= SessionFileLimits.PreviewBytes ? snapshot.Content : null;
        return null;
    }

    internal static string SafeDownloadName(string name)
    {
        var leaf = name.Split(['/', '\\']).LastOrDefault() ?? "";
        leaf = new string(leaf.Where(c => !char.IsControl(c) && !"<>:\"/\\|?*".Contains(c)).ToArray()).Trim().TrimEnd('.');
        if (leaf.Length > 120) leaf = leaf[..120];
        var stem = leaf.Split('.')[0];
        if (leaf.Length == 0 || stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '0' and <= '9'))
            return "download.bin";
        return leaf;
    }

    internal static string FileContentId(SessionFileRequest request) => JsonSerializer.Serialize(
        new[] { request.Scope.SessionKey, request.Scope.AgentId, request.Path, request.MessageEntryId });

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _revision++;
            _view = new();
            _binding = null;
        }
        _panels.Changed -= BindingChanged;
        Changed = null;
    }
}
