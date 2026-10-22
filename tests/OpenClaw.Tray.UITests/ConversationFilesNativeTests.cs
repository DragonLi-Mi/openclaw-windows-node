using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class ConversationFilesNativeTests(UIThreadFixture ui)
{
    [Theory]
    [Trait("Category", "NativeConversationFiles")]
    [InlineData("notes.txt")]
    [InlineData("LICENSE")]
    public async Task NativeSavePickerAcceptsConcreteFileExtension(string filename)
    {
        await ui.RunOnUIAsync(() =>
        {
            RecordOwnedWindow();
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            var extension = ConversationFilesController.DownloadExtension(filename);
            picker.FileTypeChoices.Add("File", [extension]);
            Assert.NotEqual("*", Assert.Single(picker.FileTypeChoices["File"]));
        });
    }
    [Theory]
    [Trait("Category", "NativeConversationFiles")]
    [InlineData("one\ntwo\n")]
    [InlineData("one\r\ntwo\r\n")]
    [InlineData("\uFEFFone\ntwo")]
    [InlineData("\uFEFFone\r\ntwo")]
    [InlineData("no final newline")]
    public async Task NativeTextBoxRetainsOriginalLineEndingsThroughCodec(string original)
    {
        await ui.RunOnUIAsync(() =>
        {
            RecordOwnedWindow();
            var format = ConversationFileText.Inspect(original);
            var editor = new TextBox { AcceptsReturn = true, Text = format.ToNative(original) };
            ui.Container.Children.Clear();
            ui.Container.Children.Add(editor);
            Assert.Equal(original, format.FromNative(editor.Text));
            editor.Select(1, Math.Min(2, editor.Text.Length - 1));
            var native = editor.Text;
            Assert.Equal(original, format.FromNative(native));
            Assert.Equal(1, editor.SelectionStart);
        });
    }

    private void RecordOwnedWindow()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_CONVERSATION_FILES_PROOF_DIR") is not { Length: > 0 } directory)
            return;
        Directory.CreateDirectory(directory);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(ui.TestWindow);
        var metadata = new
        {
            RunId = Path.GetFileName(directory),
            Head = Environment.GetEnvironmentVariable("OPENCLAW_CONVERSATION_FILES_PROOF_HEAD"),
            ProcessId = process.Id,
            StartedUtc = process.StartTime.ToUniversalTime(),
            Executable = Environment.ProcessPath,
            CommandLine = Environment.CommandLine,
            Window = handle.ToInt64(),
            Port = (int?)null,
            Data = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR"),
            AppData = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR"),
            LocalAppData = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR"),
            LegacyLocal = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR"),
            SyntheticInputs = true,
            RealGateway = false,
            ActualPickerDialog = false,
            OsInput = false,
            Binaries = new[]
            {
                typeof(ConversationFilesNativeTests).Assembly.Location,
                typeof(ConversationFilesController).Assembly.Location,
                typeof(OpenClaw.Shared.SessionFileScope).Assembly.Location
            }.Select(path => new
            {
                Path = path,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
            }).ToArray()
        };
        File.WriteAllText(Path.Combine(directory, $"owned-window-{handle.ToInt64()}.json"),
            System.Text.Json.JsonSerializer.Serialize(metadata));
    }
}
