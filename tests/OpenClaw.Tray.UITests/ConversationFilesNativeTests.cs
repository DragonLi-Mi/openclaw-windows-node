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
            var format = ConversationFileText.Inspect(original);
            var editor = new TextBox { AcceptsReturn = true, Text = format.ToNative(original) };
            Assert.Equal(original, format.FromNative(editor.Text));
            editor.Select(1, Math.Min(2, editor.Text.Length - 1));
            var native = editor.Text;
            Assert.Equal(original, format.FromNative(native));
            Assert.Equal(1, editor.SelectionStart);
        });
    }
}
