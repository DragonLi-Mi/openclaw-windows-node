using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

internal enum ConversationFilesStatus { Waiting, Loading, Ready, Unsupported, Error }
internal enum ConversationFilesNotice
{
    None, Offline, Rejected, Permission, Conflict, SourceChanged, InvalidContent, DraftLimit,
    Restored, Saved, PendingEdits, DownloadFailed, Downloaded, Uncertain
}
internal enum ConversationFilesTab { Browse, Outputs, Changes }

internal sealed record ConversationFileDocument(
    SessionFileRequest Request, SessionFileContent Snapshot, ConversationFileOrigin Origin,
    string? Text, string? SavedText, ConversationFileText? Format,
    string? ExpectedHash = null,
    bool Editing = false, bool Dirty = false, bool Restored = false,
    string? LatestText = null, string? LatestHash = null, bool SourceChanged = false,
    int SelectionStart = 0, int SelectionLength = 0, double ScrollOffset = 0, int NativeLength = 0,
    string? PendingSaveText = null)
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
    private long _observedGeneration;
    private long? _busyRevision;
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
            var authority = CaptureEditAuthority();
            lock (_gate) return authority is { } current && IsEditable(current);
        }
    }

    private (ChatConversationPanelOperation Lease, bool Set, bool Admin)? CaptureEditAuthority()
    {
        var lease = CaptureLease();
        if (lease is null) return null;
        var set = lease.Context.Client.SessionFilesCapabilities.Set;
        var admin = lease.Context.Client.GrantedOperatorScopes.Contains("operator.admin", StringComparer.Ordinal);
        return lease.IsCurrent ? (lease, set, admin) : null;
    }

    private bool IsEditable((ChatConversationPanelOperation Lease, bool Set, bool Admin) authority) =>
        !_disposed && authority.Lease.Context == _binding && !_view.Busy && authority.Set && authority.Admin &&
        _view.Notice != ConversationFilesNotice.Permission &&
        _view.Document is { Request.MessageEntryId: null, SourceChanged: false, Format.Uniform: true,
            Snapshot.Source: not null, Snapshot.PreviewKind: "text", Snapshot.ContentEncoding: "utf8",
            ExpectedHash: { } hash, Text: not null } &&
        SessionFileLimits.IsContentHash(hash) && _view.Document.NativeLength <= SessionFileLimits.NativeEditorCharacters;

    private void BindingChanged()
    {
        var context = _panels.IsReady ? _panels.Context : null;
        var active = context is not null && _panels.Layout.SelectedPanel == PanelId;
        var generation = _panels.Generation;
        ConversationFileDraftKey? retired = null;
        bool load;
        lock (_gate)
        {
            if (_disposed) return;
            load = active && (_binding != context || _view.Status == ConversationFilesStatus.Waiting);
            if (!active || _binding != context)
            {
                if (_binding is not null && _view.Document is { } document)
                    retired = document.Key(_binding.Identity);
                _revision++;
                _busyRevision = null;
                _binding = active ? context : null;
                _view = new(Notice: context is null ? ConversationFilesNotice.Offline : ConversationFilesNotice.None);
            }
            else if (generation != _observedGeneration && _busyRevision.HasValue)
            {
                _revision++;
                _busyRevision = null;
                _view = _view with { Busy = false, Notice = ConversationFilesNotice.Uncertain };
            }
            _observedGeneration = generation;
        }
        if (retired is not null) _drafts.ReleaseClean(retired);
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
        var retired = EditorTarget;
        _panels.Open(PanelId, FileContentId(request), runId ?? previous.RunId);
        if (retired is not null) _drafts.ReleaseClean(retired);
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
            var format = text is null ? null : ConversationFileText.Inspect(text);
            var document = new ConversationFileDocument(request, snapshot, origin, text, text,
                format, snapshot.Hash, NativeLength: text is null ? 0 : format!.ToNative(text).Length);
            var draft = _drafts.Find(lease.Context.Identity, request);
            var notice = ConversationFilesNotice.None;
            if (draft is not null && (draft.Text != draft.SavedText || draft.PendingSaveText is not null))
            {
                var sourceChanged = draft.Key.Origin != origin;
                var reconciled = !sourceChanged && draft.PendingSaveText is not null &&
                    draft.PendingSaveText == text && SessionFileLimits.IsContentHash(snapshot.Hash);
                var savedText = reconciled ? draft.PendingSaveText! : draft.SavedText;
                var conflict = !reconciled && (sourceChanged || draft.Hash != snapshot.Hash && draft.Text != text);
                document = document with
                {
                    Origin = draft.Key.Origin, Text = draft.Text, SavedText = savedText, Format = draft.Format,
                    ExpectedHash = reconciled ? snapshot.Hash : draft.Hash,
                    Editing = true, Dirty = draft.Text != savedText, Restored = true, SourceChanged = sourceChanged,
                    Snapshot = snapshot, LatestText = conflict && !sourceChanged ? text : null,
                    LatestHash = conflict && !sourceChanged ? snapshot.Hash : null,
                    SelectionStart = draft.SelectionStart, SelectionLength = draft.SelectionLength,
                    ScrollOffset = draft.ScrollOffset, NativeLength = draft.Format.ToNative(draft.Text).Length,
                    PendingSaveText = reconciled ? null : draft.PendingSaveText
                };
                notice = sourceChanged ? ConversationFilesNotice.SourceChanged :
                    conflict ? ConversationFilesNotice.Conflict : reconciled
                        ? document.Dirty ? ConversationFilesNotice.PendingEdits : ConversationFilesNotice.Saved
                        : draft.PendingSaveText is not null ? ConversationFilesNotice.Uncertain : ConversationFilesNotice.Restored;
            }
            if (Apply(lease, revision, v =>
            {
                var retained = !document.Editing || _drafts.Retain(Draft(document, lease.Context.Identity));
                return v with { Status = ConversationFilesStatus.Ready, Document = document,
                    Notice = retained ? notice : ConversationFilesNotice.DraftLimit };
            }))
                _drafts.Select(lease.Context.Identity, request);
        }
        catch (GatewayRequestException error) when (error.DetailType == "session_file_too_large")
        {
            var metadata = new SessionFileContent
            {
                Key = request.Scope.SessionKey, Path = request.Path, Name = request.Path, PreviewKind = "unsupported",
                Size = error.Details is { ValueKind: JsonValueKind.Object } details &&
                    details.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) && bytes >= 0 ? bytes : null
            };
            Apply(lease, revision, view => view with { Status = ConversationFilesStatus.Ready,
                Notice = ConversationFilesNotice.InvalidContent,
                Document = new(request, metadata, new(null, null), null, null, null) });
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
        var authority = CaptureEditAuthority();
        lock (_gate)
        {
            if (authority is not { } current || !IsEditable(current) || _view.Document is not { } document) return;
            if (!_drafts.Retain(Draft(document, _binding!.Identity)))
                _view = _view with { Notice = ConversationFilesNotice.DraftLimit };
            else _view = _view with { Document = document with { Editing = true } };
        }
        Changed?.Invoke();
    }

    internal string? UpdateNativeText(ConversationFileDraftKey target, string text, int selectionStart, int selectionLength,
        double scrollOffset = 0)
    {
        string content;
        lock (_gate)
        {
            if (_disposed || _view.Document is not { Editing: true, Format.Uniform: true } document ||
                _binding is null || document.Key(_binding.Identity) != target) return null;
            content = document.Format.FromNative(text);
            var dirty = content != document.SavedText;
            var next = document with { Text = content, Dirty = dirty, SelectionStart = selectionStart,
                SelectionLength = selectionLength, ScrollOffset = scrollOffset, NativeLength = text.Length };
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
        return content;
    }

    internal void UpdateEditorPosition(ConversationFileDraftKey target, int start, int length, double scroll)
    {
        lock (_gate)
        {
            if (_disposed || _binding is null || _view.Document is not { } document ||
                document.Key(_binding.Identity) != target) return;
            _view = _view with { Document = document with { SelectionStart = start, SelectionLength = length, ScrollOffset = scroll } };
            _drafts.UpdatePosition(target, start, length, scroll);
        }
    }

    internal async Task SaveAsync(bool overwrite = false)
    {
        ConversationFileDocument document;
        var authority = CaptureEditAuthority();
        if (authority is not { } currentAuthority) return;
        var lease = currentAuthority.Lease;
        long revision;
        lock (_gate)
        {
            if (!IsEditable(currentAuthority) || _view.Document is not { Text: not null } current ||
                !current.Dirty && current.PendingSaveText is null) return;
            revision = ++_revision;
            _busyRevision = revision;
            document = current;
            _view = _view with { Busy = true, Notice = current.PendingSaveText is null
                ? ConversationFilesNotice.None : ConversationFilesNotice.Uncertain };
        }
        Changed?.Invoke();
        var capturedText = document.Text!;
        try
        {
            if (Encoding.UTF8.GetByteCount(capturedText) > SessionFileLimits.PreviewBytes)
            {
                Apply(lease, revision, view => view with { Notice = ConversationFilesNotice.InvalidContent });
                return;
            }
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
            if (document.PendingSaveText is { } pendingText && latestText == pendingText)
            {
                ApplySaved(lease, revision, latest, pendingText);
                return;
            }
            if (document.PendingSaveText is not null && !overwrite)
            {
                Apply(lease, revision, view => view with { Notice = ConversationFilesNotice.Conflict,
                    Document = view.Document! with { LatestText = latestText, LatestHash = latest.Hash } });
                return;
            }
            if (latestText == capturedText)
            {
                ApplySaved(lease, revision, latest, capturedText);
                return;
            }
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
            var retained = false;
            if (!Apply(lease, revision, view =>
            {
                var pending = view.Document! with { PendingSaveText = capturedText };
                retained = _drafts.Retain(Draft(pending, lease.Context.Identity));
                return retained ? view with { Document = pending } : view with { Notice = ConversationFilesNotice.DraftLimit };
            }) || !retained) return;
            SessionFileContent saved;
            try
            {
                saved = await lease.Context.Client.SetSessionFileAsync(
                    new(document.Request.Scope, document.Request.Path, capturedText, latest.Hash!, document.Origin.Source!),
                    lease.Context.ConnectionEpoch, lease.CancellationToken).ConfigureAwait(false);
            }
            catch (GatewayRequestException error) when (error.DetailType is
                "session_file_conflict" or "session_file_too_large" or "session_file_not_found")
            {
                Apply(lease, revision, view =>
                {
                    var rejected = view.Document! with { PendingSaveText = null };
                    var kept = _drafts.Retain(Draft(rejected, lease.Context.Identity));
                    return view with { Document = rejected,
                        Notice = kept ? ConversationFilesNotice.Rejected : ConversationFilesNotice.DraftLimit };
                });
                throw;
            }
            if (!IsCurrent(lease, revision)) return;
            ApplySaved(lease, revision, saved, capturedText);
        }
        catch (GatewayRequestException error) when (error.DetailType == "session_file_conflict")
        {
            Apply(lease, revision, v => v with { Busy = false, Notice = ConversationFilesNotice.Conflict });
            await FetchLatestAsync(lease, revision, document).ConfigureAwait(false);
        }
        catch (Exception error) when (IsFileError(error)) { Fail(lease, revision, error); }
        finally { ReleaseBusy(lease, revision); }
    }

    private void ApplySaved(ChatConversationPanelOperation lease, long revision, SessionFileContent snapshot, string text) =>
        Apply(lease, revision, view =>
        {
            var document = view.Document!;
            // The real set response is metadata-only. Its receipt confirms the UTF-8 text we sent,
            // not a new preview representation that should make the editor read-only.
            var readable = new SessionFileContent
            {
                Key = snapshot.Key, Root = document.Origin.Root, Source = snapshot.Source, Path = snapshot.Path,
                WorkspacePath = snapshot.WorkspacePath ?? document.Snapshot.WorkspacePath,
                Name = snapshot.Name ?? document.Snapshot.Name, Kind = snapshot.Kind, Hash = snapshot.Hash,
                Content = text, ContentEncoding = "utf8", PreviewKind = "text",
                MimeType = snapshot.MimeType ?? document.Snapshot.MimeType, Size = snapshot.Size, UpdatedAt = snapshot.UpdatedAt
            };
            var next = document with { Snapshot = readable, ExpectedHash = snapshot.Hash, SavedText = text,
                Dirty = document.Text != text, Restored = false, LatestText = null, LatestHash = null,
                PendingSaveText = null };
            var retained = _drafts.Retain(Draft(next, lease.Context.Identity));
            return view with { Busy = false, Document = next,
                Notice = !retained ? ConversationFilesNotice.DraftLimit :
                    next.Dirty ? ConversationFilesNotice.PendingEdits : ConversationFilesNotice.Saved };
        });

    private async Task FetchLatestAsync(ChatConversationPanelOperation lease, long revision, ConversationFileDocument document)
    {
        try
        {
            var latest = await lease.Context.Client.GetSessionFileAsync(document.Request,
                lease.Context.ConnectionEpoch, lease.CancellationToken).ConfigureAwait(false);
            var changed = new ConversationFileOrigin(latest.Root, latest.Source) != document.Origin;
            Apply(lease, revision, v => v with
            {
                Notice = changed ? ConversationFilesNotice.SourceChanged : latest.Hash == document.ExpectedHash
                    ? ConversationFilesNotice.Rejected : ConversationFilesNotice.Conflict,
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
            var format = text is null ? null : ConversationFileText.Inspect(text);
            Apply(lease, revision, v =>
            {
                _drafts.Remove(document.Key(lease.Context.Identity));
                return v with { Document = new(document.Request, latest, document.Origin, text, text,
                    format, latest.Hash, NativeLength: text is null ? 0 : format!.ToNative(text).Length),
                    Notice = ConversationFilesNotice.None };
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
                Dirty = false, Restored = false, LatestText = null, LatestHash = null, PendingSaveText = null },
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
        lock (_gate)
        {
            if (_disposed || _revision != revision || _view.Document?.Request != document.Request) return;
            _busyRevision = revision;
            _view = _view with { Busy = true, Notice = ConversationFilesNotice.None };
        }
        Changed?.Invoke();
        try
        {
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
        catch (Exception error) when (IsFileError(error) || error is IOException or COMException)
        {
            if (IsCurrent(lease, revision))
                Apply(lease, revision, v => v with { Busy = false, Notice = ConversationFilesNotice.DownloadFailed });
        }
        finally { ReleaseBusy(lease, revision); }
    }

    private (ChatConversationPanelOperation Lease, long Revision)? Begin(string? runId = null)
    {
        var lease = _panels.BeginOperation(PanelId, runId);
        if (lease is null || !lease.IsCurrent) return null;
        lock (_gate)
        {
            if (_disposed || _view.Busy || _binding != lease.Context) return null;
            return (lease, ++_revision);
        }
    }

    private bool IsCurrent(ChatConversationPanelOperation lease, long revision)
    {
        var current = lease.IsCurrent;
        lock (_gate) return current && !_disposed && revision == _revision && lease.Context == _binding;
    }

    private bool Apply(ChatConversationPanelOperation lease, long revision, Func<ConversationFilesView, ConversationFilesView> apply)
    {
        if (!lease.IsCurrent) return false;
        lock (_gate)
        {
            if (_disposed || revision != _revision || lease.Context != _binding) return false;
            _view = apply(_view);
        }
        Changed?.Invoke();
        return true;
    }

    private void ReleaseBusy(ChatConversationPanelOperation lease, long revision)
    {
        var stale = !lease.IsCurrent;
        var changed = false;
        lock (_gate)
        {
            if (!_disposed && _busyRevision == revision)
            {
                _busyRevision = null;
                _view = _view with { Busy = false, Notice = stale ? ConversationFilesNotice.Uncertain : _view.Notice };
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
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
                _ when view.Document?.PendingSaveText is not null => ConversationFilesNotice.Uncertain,
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
            document.SelectionStart, document.SelectionLength, document.ScrollOffset, document.PendingSaveText);

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
        leaf = new string(leaf.Where(c => !char.IsControl(c) &&
            char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format &&
            !"<>:\"/\\|?*".Contains(c)).ToArray()).Trim().TrimEnd('.');
        if (leaf.Length > 120) leaf = leaf[..120];
        if (leaf.Length > 0 && char.IsHighSurrogate(leaf[^1])) leaf = leaf[..^1];
        leaf = leaf.TrimEnd(' ', '.');
        var stem = leaf.Split('.')[0].TrimEnd(' ', '.');
        if (leaf.Length == 0 || stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && (stem[3] is >= '0' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3')))
            return "download.bin";
        return leaf;
    }

    internal static string DownloadExtension(string filename)
    {
        var extension = Path.GetExtension(filename);
        return extension.Length is > 1 and <= 32 && extension[1..].All(c => char.IsLetterOrDigit(c) || c is '_' or '-')
            ? extension : ".bin";
    }

    internal static string FileContentId(SessionFileRequest request) => JsonSerializer.Serialize(
        new[] { request.Scope.SessionKey, request.Scope.AgentId, request.Path, request.MessageEntryId });

    public void Dispose()
    {
        ConversationFileDraftKey? retired;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            retired = _binding is not null && _view.Document is { } document ? document.Key(_binding.Identity) : null;
            _revision++;
            _view = new();
            _binding = null;
        }
        _panels.Changed -= BindingChanged;
        if (retired is not null) _drafts.ReleaseClean(retired);
        Changed = null;
    }
}
