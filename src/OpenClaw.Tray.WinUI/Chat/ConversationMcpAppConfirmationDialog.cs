using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

/// <summary>Controlled native dialog for the Root's pending confirmation, not an imperative popup.</summary>
internal static class ConversationMcpAppConfirmationDialog
{
    private const int MaximumPreviewCharacters = 4000;

    internal static Element Render(ChatConversationMcpAppConfirmation request, bool isOpen, Action<bool> respond)
    {
        var key = request.Kind switch
        {
            ChatConversationMcpAppActionKind.File => "ConfirmFile",
            ChatConversationMcpAppActionKind.Link => "ConfirmLink",
            _ => request.Message?.Target == ConversationMcpAppMessageTarget.New ? "ConfirmNewMessage" : "ConfirmMessage"
        };
        var detail = request.Kind switch
        {
            ChatConversationMcpAppActionKind.File => request.Path,
            ChatConversationMcpAppActionKind.Link => request.Link?.AbsoluteUri,
            _ => request.Message?.PlainText
        };
        var preview = detail is { Length: > MaximumPreviewCharacters }
            ? detail[..MaximumPreviewCharacters] + "..." : detail ?? "";
        var content = VStack(12,
            TextBlock(Text(key)).TextWrapping(TextWrapping.Wrap),
            TextBlock(preview).TextWrapping(TextWrapping.Wrap).Set(text => text.IsTextSelectionEnabled = true),
            TextBlock(request.Message is { } message
                ? string.Format(Text("ContentBlocks"), message.Content.GetArrayLength()) : "")
                .TextWrapping(TextWrapping.Wrap));
        return ContentDialog(Text("ConfirmTitle"), content, Text("Cancel")) with
        {
            PrimaryButtonText = Text("Allow"),
            DefaultButton = ContentDialogButton.Close,
            IsOpen = isOpen && request.Owner.IsCurrent,
            OnClosed = result => respond(request.Owner.IsCurrent && result == ContentDialogResult.Primary)
        };
    }

    private static string Text(string key) => LocalizationHelper.GetString("Chat_McpApp_" + key);
}
