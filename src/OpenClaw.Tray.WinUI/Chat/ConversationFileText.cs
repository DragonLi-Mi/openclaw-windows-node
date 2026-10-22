namespace OpenClawTray.Chat;

/// <summary>WinUI normalizes line breaks to CR. Keep the file's representation outside the native editor.</summary>
internal sealed record ConversationFileText(string LineEnding, bool Bom, bool Uniform)
{
    internal static ConversationFileText Inspect(string text)
    {
        var endings = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    endings.Add("\r\n");
                    index++;
                }
                else endings.Add("\r");
            }
            else if (text[index] == '\n') endings.Add("\n");
        }
        return new(endings.FirstOrDefault() ?? "\n", text.StartsWith('\uFEFF'), endings.Count <= 1);
    }

    internal string ToNative(string content) =>
        Normalize(Bom && content.StartsWith('\uFEFF') ? content[1..] : content);

    internal string FromNative(string content)
    {
        if (!Uniform) throw new InvalidOperationException("Mixed line endings cannot be edited safely.");
        return (Bom ? "\uFEFF" : "") + Normalize(content).Replace("\r", LineEnding, StringComparison.Ordinal);
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
}
