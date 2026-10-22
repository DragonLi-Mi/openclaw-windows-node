using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Input;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ConversationFilesArtifact(
    SessionFileScope Scope, string ArtifactId, string? AgentId, string? GatewayMessageId,
    string DisplayName, string Kind, string? MimeType);

internal sealed record ReactorConversationFilesPanelProps(
    ConversationFilesController Controller, Func<nint> WindowHandle,
    IReadOnlyList<ConversationFilesArtifact>? Artifacts = null,
    Func<ConversationFilesArtifact, Element>? RenderArtifact = null);

internal sealed class ReactorConversationFilesPanel : Component<ReactorConversationFilesPanelProps>
{
    public override Element Render()
    {
        var controller = Props.Controller;
        var initialView = controller.View;
        var lease = controller.CaptureLease();
        var scope = controller.CurrentScope;
        var (_, invalidate) = UseState(0L, threadSafe: true);
        var (searchDraft, setSearchDraft) = UseState<(ChatConversationIdentity? Identity, string Text)>((null, ""));
        var search = searchDraft.Identity == lease?.Context.Identity ? searchDraft.Text : initialView.Search;
        void SetSearch(string text) => setSearchDraft((lease?.Context.Identity, text));
        var root = UseRef<FrameworkElement?>(null);
        UseEffect((Func<Action>)(() =>
        {
            long revision = 0;
            void Changed() => invalidate(Interlocked.Increment(ref revision));
            controller.Changed += Changed;
            if (!ReferenceEquals(initialView, controller.View)) Changed();
            return () => controller.Changed -= Changed;
        }), controller);
        var view = initialView;
        var capabilities = controller.Capabilities;
        var editor = UseRef<EditorBinding?>(null);
        var image = UseRef<(SessionFileContent? Snapshot, Element Content)>((null, Empty()));
        var document = view.Document;
        if (!ReferenceEquals(image.Current.Snapshot, document?.Snapshot))
            image.Current = (document?.Snapshot, BuildImage(document));
        var sourceName = view.Listing?.Root;
        var entries = view.Tab == ConversationFilesTab.Outputs
            ? view.Listing?.Files
                .Select(file => new FileRow(file.Path, file.Name, false, file.Missing)).ToArray() ?? []
            : view.Listing?.Browser?.Entries
                .Select(file => new FileRow(file.Path, file.Name, file.IsDirectory, false)).ToArray() ?? [];

        void Open(FileRow file)
        {
            if (file.Directory) _ = controller.LoadListingAsync(file.Path);
            else if (scope is not null) _ = controller.OpenAsync(new(scope, file.Path));
        }
        Element ActionButton(string key, Action action, bool enabled = true) =>
            ReactorConversationFilesPanel.ActionButton(key, () => { if (lease?.IsCurrent == true) action(); },
                enabled && !view.Busy && lease?.IsCurrent == true);

        var files = ItemsView(entries, file => file.Path, (file, _) => ItemContainer(
            Button(
                HStack(8,
                    TextBlock(file.Directory ? Text("Directory") : Text("File")),
                    TextBlock(file.Name).TextTrimming(TextTrimming.CharacterEllipsis)),
                () => { if (lease?.IsCurrent == true) Open(file); })
                .AutomationName($"{Text(file.Directory ? "Directory" : "File")}: {file.Name}")
                .AutomationId("ConversationFileEntry")
                .ToolTip(file.Path)
                .IsEnabled(!file.Missing && !view.Busy)
                .HAlign(HorizontalAlignment.Stretch)
                .Set(button => button.HorizontalContentAlignment = HorizontalAlignment.Stretch)))
            with { SelectionMode = ItemsViewSelectionMode.None, IsItemInvokedEnabled = false };

        var nativeEditor = TextBox()
            .AutomationId("ConversationFileEditor")
            .AutomationName(document?.Request.Path ?? Text("Preview"))
            .MinHeight(128)
            .MaxHeight(480)
            .Set(box => editor.Current?.Apply(document, controller.CanEdit))
            .OnMount(control =>
            {
                editor.Current = new EditorBinding((TextBox)control, controller);
                editor.Current.Apply(controller.View.Document, controller.CanEdit);
            })
            .OnUnmount(_ =>
            {
                editor.Current?.Dispose();
                editor.Current = null;
            })
            .WithKey("conversation-file-editor");

        var artifactElements = Props.Artifacts is { } artifacts && Props.RenderArtifact is { } renderArtifact
            ? artifacts.Where(artifact => artifact.Scope == scope)
                .Select(artifact => renderArtifact(artifact)
                    .WithKey(JsonSerializer.Serialize(new[] { artifact.ArtifactId, artifact.AgentId, artifact.GatewayMessageId }))).ToArray()
            : [];
        var browser = ScrollViewer(Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto,
            GridSize.Px(160), GridSize.Auto, GridSize.Star()],
            TextBlock(sourceName ?? Text("RemoteRoot"))
                .TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled(true).Grid(row: 0),
            Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                TextBox(search, SetSearch, Text("Search"))
                    .AutomationName(Text("Search"))
                    .OnPreviewKeyDown((_, args) =>
                    {
                        if (args.Key != VirtualKey.Enter) return;
                        args.Handled = true;
                        if (lease?.IsCurrent == true && !view.Busy)
                            _ = controller.LoadListingAsync("", search);
                    }).Grid(column: 0),
                ActionButton("Search", () => _ = controller.LoadListingAsync("", search), capabilities.List && !view.Busy)
                    .Grid(column: 1))
                .IsVisible(view.Tab == ConversationFilesTab.Browse).Grid(row: 1),
            Grid([GridSize.Star(), GridSize.Star()], [GridSize.Auto],
                ActionButton("Parent", () => _ = controller.LoadListingAsync(view.Listing?.Browser?.ParentPath ?? ""),
                    view.Listing?.Browser?.ParentPath is not null && !view.Busy).Grid(column: 0),
                ActionButton("Refresh", () => _ = controller.RefreshAsync(), !view.Busy).Grid(column: 1))
                .Grid(row: 2),
            TextBlock(Text("Truncated")).TextWrapping(TextWrapping.Wrap)
                .IsVisible(view.Listing?.Browser?.Truncated == true).Grid(row: 3),
            files.Grid(row: 4).AutomationName(Text("Files")),
            TextBlock(Text("Empty")).TextWrapping(TextWrapping.Wrap)
                .IsVisible(view.Status == ConversationFilesStatus.Ready && entries.Length == 0 && artifactElements.Length == 0)
                .Grid(row: 4),
            VStack(8, artifactElements).IsVisible(view.Tab == ConversationFilesTab.Outputs).Grid(row: 5),
            Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto],
                TextBlock(document?.Request.Path ?? Text("ChooseFile")).TextWrapping(TextWrapping.Wrap)
                    .IsTextSelectionEnabled(true).Grid(row: 0),
                Grid([GridSize.Star(), GridSize.Star()], [GridSize.Auto, GridSize.Auto],
                    ActionButton("Edit", controller.BeginEdit, controller.CanEdit && document?.Editing != true).Grid(column: 0),
                    ActionButton("Save", () => _ = controller.SaveAsync(),
                        controller.CanEdit && (document is { Dirty: true } || document?.PendingSaveText is not null) &&
                            view.Notice != ConversationFilesNotice.Conflict).Grid(column: 1),
                    ActionButton("Discard", controller.Discard, document?.Editing == true && !view.Busy).Grid(row: 1, column: 0),
                    ActionButton("Download", () => _ = controller.DownloadAsync(ChooseDestination),
                        document is not null && capabilities.Download && !view.Busy).Grid(row: 1, column: 1))
                    .Grid(row: 1),
                VStack(4,
                    TextBlock(Text(document?.Request.MessageEntryId is not null ? "Historical" : "ReadOnly"))
                        .TextWrapping(TextWrapping.Wrap)
                        .IsVisible(document is not null && !controller.CanEdit && !view.Busy),
                    TextBlock(Text("Uncertain")).TextWrapping(TextWrapping.Wrap)
                        .IsVisible(document?.PendingSaveText is not null && view.Notice != ConversationFilesNotice.Uncertain))
                    .Grid(row: 2),
                nativeEditor.IsVisible(document?.Text is not null).Grid(row: 3),
                image.Current.Content.IsVisible(document?.Text is null && document?.Snapshot.PreviewKind == "image").Grid(row: 3),
                TextBlock(Text("UnsupportedPreview")).TextWrapping(TextWrapping.Wrap)
                    .IsVisible(document is not null && document.Text is null && document.Snapshot.PreviewKind != "image")
                    .Grid(row: 3),
                VStack(8,
                    TextBlock(Text("Conflict")).TextWrapping(TextWrapping.Wrap),
                    ScrollViewer(TextBlock(document?.LatestText ?? Text("UnsupportedPreview")).IsTextSelectionEnabled(true)
                        .TextWrapping(TextWrapping.Wrap)).MaxHeight(160),
                    Grid([GridSize.Star(), GridSize.Star()], [GridSize.Auto],
                        ActionButton("Reload", () => _ = controller.ReloadAsync(), !view.Busy).Grid(column: 0),
                        ActionButton("Overwrite", () => _ = controller.SaveAsync(overwrite: true),
                            controller.CanEdit && document?.LatestHash is not null).Grid(column: 1)))
                    .IsVisible(view.Notice == ConversationFilesNotice.Conflict).Grid(row: 4))
                .IsVisible(document is not null || entries.Length > 0).Grid(row: 6)))
            .IsVisible(view.Tab != ConversationFilesTab.Changes);

        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Star()],
            Grid([GridSize.Star(), GridSize.Star(), GridSize.Star()], [GridSize.Auto],
                ActionButton("Files", () => controller.SelectTab(ConversationFilesTab.Browse)).Grid(column: 0),
                ActionButton("Outputs", () => controller.SelectTab(ConversationFilesTab.Outputs)).Grid(column: 1),
                ActionButton("Changes", () => controller.SelectTab(ConversationFilesTab.Changes), capabilities.Diff).Grid(column: 2))
                .Grid(row: 0),
            TextBlock(view.Status == ConversationFilesStatus.Ready ? "" : Text(view.Status.ToString())).TextWrapping(TextWrapping.Wrap)
                .IsVisible(view.Status != ConversationFilesStatus.Ready).Grid(row: 1),
            TextBlock(view.Notice == ConversationFilesNotice.None ? "" : Text(view.Notice.ToString()))
                .TextWrapping(TextWrapping.Wrap)
                .Set(text => AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite))
                .Grid(row: 2),
            browser.Grid(row: 3),
            Changes(controller, view, lease).IsVisible(view.Tab == ConversationFilesTab.Changes).Grid(row: 3))
            .Margin(12)
            .OnMount(control => root.Current = (FrameworkElement)control)
            .OnUnmount(_ => root.Current = null);

        Task<ConversationFileDestination?> ChooseDestination(string filename, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<ConversationFileDestination?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var host = root.Current;
            if (host is null || !host.DispatcherQueue.TryEnqueue(async () =>
            {
                StorageStreamTransaction? transaction = null;
                Stream? stream = null;
                var transferred = false;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var handle = Props.WindowHandle();
                    if (!host.IsLoaded || handle == 0) throw new InvalidOperationException("The file picker has no active owner.");
                    var picker = new FileSavePicker { SuggestedFileName = filename };
                    picker.FileTypeChoices.Add(Text("File"), [ConversationFilesController.DownloadExtension(filename)]);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
                    var file = await picker.PickSaveFileAsync();
                    if (file is null) { completion.TrySetResult(null); return; }
                    cancellationToken.ThrowIfCancellationRequested();
                    transaction = await file.OpenTransactedWriteAsync();
                    stream = transaction.Stream.AsStreamForWrite();
                    stream.SetLength(0);
                    transferred = completion.TrySetResult(new(stream,
                        async () => { await stream.FlushAsync(); await transaction.CommitAsync(); },
                        () => { stream.Dispose(); transaction.Dispose(); return ValueTask.CompletedTask; }));
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or
                    UnauthorizedAccessException or InvalidOperationException or COMException or ArgumentException)
                { completion.TrySetException(error); }
                finally
                {
                    if (!transferred) { stream?.Dispose(); transaction?.Dispose(); }
                }
            })) completion.TrySetException(new InvalidOperationException("The file picker owner is unavailable."));
            return completion.Task;
        }
    }

    private static Element Changes(ConversationFilesController controller, ConversationFilesView view,
        ChatConversationPanelOperation? lease)
    {
        var scope = controller.CurrentScope;
        Element ActionButton(string key, Action action, bool enabled = true) =>
            ReactorConversationFilesPanel.ActionButton(key, () => { if (lease?.IsCurrent == true) action(); },
                enabled && !view.Busy && lease?.IsCurrent == true);
        var diff = view.Diff;
        var changes = diff?.Files ?? [];
        var rows = ItemsView(changes, file => file.Path, (file, index) => ItemContainer(
            VStack(8,
                Button(file.Path, () =>
                {
                    if (lease?.IsCurrent == true && scope is not null)
                    {
                        controller.SelectTab(ConversationFilesTab.Browse);
                        _ = controller.OpenAsync(new(scope, file.Path));
                    }
                }).AutomationName(file.Path),
                TextBlock(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    Text("Counts"), file.Additions, file.Deletions)),
                TextBlock(Text(file.Binary ? "Binary" : file.Truncated ? "Truncated" :
                    file.Patch is null ? "UnavailablePatch" : "Patch"))
                    .TextWrapping(TextWrapping.Wrap),
                TextBlock(file.Binary ? "" : file.Patch ?? "").IsTextSelectionEnabled(true).TextWrapping(TextWrapping.NoWrap))))
            with { SelectionMode = ItemsViewSelectionMode.None, IsItemInvokedEnabled = false };
        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Star()],
            Grid([GridSize.Star(), GridSize.Star(), GridSize.Star()], [GridSize.Auto],
                ActionButton("AllChanges", () => _ = controller.LoadDiffAsync()).Grid(column: 0),
                ActionButton("Uncommitted", () => _ = controller.LoadDiffAsync(SessionDiffScope.Uncommitted)).Grid(column: 1),
                ActionButton("Refresh", () => _ = controller.LoadDiffAsync(view.DiffScope, view.Commit)).Grid(column: 2))
                .Grid(row: 0),
            ScrollViewer(VStack(4, (diff?.Commits ?? []).Select(commit =>
                Button(string.IsNullOrWhiteSpace(commit.Subject) ? commit.Sha : commit.Subject, () =>
                    { if (lease?.IsCurrent == true && !view.Busy) _ = controller.LoadDiffAsync(SessionDiffScope.Commit, commit.Sha); })
                    .IsEnabled(!view.Busy && lease?.IsCurrent == true)
                    .AutomationName(string.IsNullOrWhiteSpace(commit.Subject) ? commit.Sha : commit.Subject)
                    .ToolTip(commit.Sha).WithKey(commit.Sha)).ToArray())).MaxHeight(128).Grid(row: 1),
            TextBlock(diff?.Computing == true ? Text("Computing") :
                diff?.UnavailableReason is { } reason ? Text("Diff_" + reason) :
                diff?.Truncated == true ? Text("Truncated") :
                diff is not null && diff.Files.Length == 0 ? Text("NoChanges") : diff?.Branch ?? "")
                .TextWrapping(TextWrapping.Wrap).Grid(row: 2),
            rows.IsVisible(view.Status != ConversationFilesStatus.Loading).Grid(row: 3).AutomationName(Text("Changes")));
    }

    private static Element BuildImage(ConversationFileDocument? document)
    {
        if (document?.Snapshot is not { PreviewKind: "image", ContentEncoding: "base64", Content: { } content,
            MimeType: "image/png" or "image/jpeg" or "image/gif" or "image/webp" } ||
            content.Length > SessionFileLimits.PreviewBytes * 2)
            return TextBlock(Text("UnsupportedPreview"));
        try
        {
            var bytes = Convert.FromBase64String(content);
            if (bytes.Length > SessionFileLimits.PreviewBytes || ChatAttachmentBitmapDecoder.TryDecode(bytes) is not { } bitmap)
                return TextBlock(Text("UnsupportedPreview"));
            return Border(Empty()).MinHeight(128).MaxHeight(320)
                .Background(new ImageBrush { ImageSource = bitmap, Stretch = Stretch.Uniform })
                .AutomationName(document.Snapshot.Name ?? Text("Preview"));
        }
        catch (FormatException) { return TextBlock(Text("UnsupportedPreview")); }
    }

    private static Element ActionButton(string key, Action action, bool enabled = true) =>
        Button(TextBlock(Text(key)).TextWrapping(TextWrapping.Wrap), action).AutomationName(Text(key))
            .MinHeight(32).HAlign(HorizontalAlignment.Stretch).IsEnabled(enabled);

    internal static string Text(string key) => LocalizationHelper.GetString("Chat_Files_" + key);
    private sealed record FileRow(string Path, string Name, bool Directory, bool Missing);

    private sealed class EditorBinding : IDisposable
    {
        private readonly TextBox _editor;
        private readonly ConversationFilesController _controller;
        private bool _applying;
        private bool _composing;
        private SessionFileRequest? _request;
        private ConversationFileDraftKey? _target;
        private Microsoft.UI.Xaml.Controls.ScrollViewer? _scroll;
        private string? _lastModelText;
        private string? _lastNativeText;
        private bool _capturing;

        internal EditorBinding(TextBox editor, ConversationFilesController controller)
        {
            _editor = editor;
            _controller = controller;
            editor.AcceptsReturn = true;
            editor.TextWrapping = TextWrapping.NoWrap;
            Microsoft.UI.Xaml.Controls.ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
            Microsoft.UI.Xaml.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
            editor.TextChanged += TextChanged;
            editor.SelectionChanged += SelectionChanged;
            editor.TextCompositionStarted += CompositionStarted;
            editor.TextCompositionEnded += CompositionEnded;
            editor.PreviewKeyDown += KeyDown;
            editor.Loaded += Loaded;
        }

        internal void Apply(ConversationFileDocument? document, bool canEdit)
        {
            _editor.IsReadOnly = document?.Editing != true || !canEdit;
            _editor.MaxLength = _editor.IsReadOnly ? int.MaxValue : SessionFileLimits.NativeEditorCharacters;
            if (_composing) return;
            var target = _controller.EditorTarget;
            if (target == _target && (_capturing || document?.Text == _lastModelText)) return;
            _target = target;
            var text = document?.Text is { } value ? document.Format?.ToNative(value) ?? value : "";
            var newRequest = _request != document?.Request;
            _applying = true;
            try
            {
                if (_editor.Text != text) _editor.Text = text;
                if (newRequest && document is not null)
                {
                    _editor.Select(Math.Min(document.SelectionStart, text.Length),
                        Math.Min(document.SelectionLength, Math.Max(0, text.Length - document.SelectionStart)));
                    _scroll?.ChangeView(null, document.ScrollOffset, null, disableAnimation: true);
                }
                _request = document?.Request;
                _lastModelText = document?.Text;
                _lastNativeText = _editor.Text;
            }
            finally { _applying = false; }
        }

        private void TextChanged(object sender, TextChangedEventArgs args) => Capture();
        private void SelectionChanged(object sender, RoutedEventArgs args) => Capture();
        private void Capture()
        {
            if (_applying || _composing || _target is not { } target) return;
            var text = _editor.Text;
            if (text == _lastNativeText)
            {
                _controller.UpdateEditorPosition(target, _editor.SelectionStart, _editor.SelectionLength,
                    _scroll?.VerticalOffset ?? 0);
                return;
            }
            _capturing = true;
            try
            {
                var accepted = _controller.UpdateNativeText(target, text, _editor.SelectionStart, _editor.SelectionLength,
                    _scroll?.VerticalOffset ?? 0);
                if (accepted is not null) { _lastModelText = accepted; _lastNativeText = text; }
            }
            finally { _capturing = false; }
        }
        private void CompositionStarted(TextBox sender, TextCompositionStartedEventArgs args) => _composing = true;
        private void CompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
        {
            _composing = false;
            Capture();
            Apply(_controller.View.Document, _controller.CanEdit);
        }
        private void Loaded(object sender, RoutedEventArgs args)
        {
            _scroll = FindScroll(_editor);
            if (_scroll is not null)
            {
                _scroll.ViewChanged += ViewChanged;
                _scroll.ChangeView(null, _controller.View.Document?.ScrollOffset ?? 0, null, disableAnimation: true);
            }
        }
        private void ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args) => Capture();
        private static Microsoft.UI.Xaml.Controls.ScrollViewer? FindScroll(DependencyObject root)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                if (child is Microsoft.UI.Xaml.Controls.ScrollViewer scroll) return scroll;
                if (FindScroll(child) is { } nested) return nested;
            }
            return null;
        }
        private void KeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (args.Key == VirtualKey.S && !_composing &&
                InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                    .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                args.Handled = true;
                _ = _controller.SaveAsync();
            }
        }
        public void Dispose()
        {
            Capture();
            _editor.TextChanged -= TextChanged;
            _editor.SelectionChanged -= SelectionChanged;
            _editor.TextCompositionStarted -= CompositionStarted;
            _editor.TextCompositionEnded -= CompositionEnded;
            _editor.PreviewKeyDown -= KeyDown;
            _editor.Loaded -= Loaded;
            if (_scroll is not null) _scroll.ViewChanged -= ViewChanged;
        }
    }
}
