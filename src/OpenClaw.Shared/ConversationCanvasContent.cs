using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared;

public enum ConversationCanvasContentKind
{
    Document,
    HostedPreview,
    McpApp,
}

public sealed record ConversationMcpAppMetadata(
    string ViewId, string? ServerName, string? ToolName, string? UiResourceUri,
    string? ToolCallId, string? OriginSessionKey, string? ResultMetaState);

/// <summary>Gateway-authored presentation metadata, never inferred from visible tool text.</summary>
public sealed record ConversationCanvasContent(
    ConversationCanvasContentKind Kind,
    string? DocumentId,
    string? EntryUrl,
    string? Title,
    bool PreviewAllowsScripts,
    double? PreferredHeight = null,
    string? OriginSessionKey = null,
    string? ToolCallId = null,
    ConversationMcpAppMetadata? McpApp = null)
{
    private static readonly Regex DocumentIdPattern = new(
        @"\A(?!\.{1,2}\z)[A-Za-z0-9._-]{1,256}\z", RegexOptions.CultureInvariant);

    public static bool IsDocumentId(string? value) =>
        value is not null && DocumentIdPattern.IsMatch(value);

    public static ConversationCanvasContent? ReadContentBlock(JsonElement block)
    {
        if (String(block, "type") != "canvas" ||
            !block.TryGetProperty("preview", out var preview))
            return null;
        return ReadPreview(preview);
    }

    public static ConversationCanvasContent? ReadPreview(JsonElement preview)
    {
        if (String(preview, "kind") != "canvas" ||
            String(preview, "render") != "url" ||
            String(preview, "surface") == "tool_card")
            return null;

        var id = String(preview, "viewId")?.Trim();
        var kind = ConversationCanvasContentKind.Document;
        string? originSessionKey = null;
        string? toolCallId = null;
        ConversationMcpAppMetadata? metadata = null;
        if (preview.TryGetProperty("mcpApp", out var app) && app.ValueKind == JsonValueKind.Object)
        {
            id = String(app, "viewId")?.Trim();
            originSessionKey = String(app, "originSessionKey");
            toolCallId = String(app, "toolCallId");
            kind = ConversationCanvasContentKind.McpApp;
            if (!string.IsNullOrWhiteSpace(id)) metadata = ReadMcpAppMetadata(app, id);
        }
        var url = String(preview, "url");
        if (kind == ConversationCanvasContentKind.McpApp)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128) return null;
        }
        else if (!IsDocumentId(id) || !IsManagedDocumentEntry(id!, url))
        {
            kind = ConversationCanvasContentKind.HostedPreview;
        }
        if (kind == ConversationCanvasContentKind.HostedPreview && string.IsNullOrWhiteSpace(url)) return null;
        var title = String(preview, "title");
        if (title?.Length > 512) title = title[..512];
        var height = preview.TryGetProperty("preferredHeight", out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var number) && double.IsFinite(number) && number > 0
                ? Math.Clamp(number, 48, 8000) : (double?)null;
        return new(kind, id, url, title, String(preview, "sandbox") != "strict",
            height, originSessionKey, toolCallId, metadata);
    }

    public static bool IsManagedDocumentEntry(string documentId, string? entryUrl)
    {
        const string prefix = "/__openclaw__/canvas/documents/";
        if (string.IsNullOrWhiteSpace(entryUrl) || !entryUrl.StartsWith(prefix, StringComparison.Ordinal) ||
            !Uri.TryCreate(new Uri("https://canvas.invalid"), entryUrl, out var entry) ||
            !entry.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var segments = entry.AbsolutePath[prefix.Length..].Split('/', 2);
        return segments.Length == 2 && segments[1].Length > 0 &&
            Uri.UnescapeDataString(segments[0]) == documentId;
    }

    public static IReadOnlyList<ConversationCanvasContent> ReadToolResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return [];
        var items = new List<ConversationCanvasContent>();
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                if (ReadContentBlock(block) is { } canvas) items.Add(canvas);
        if (items.Count > 0) return items;
        if (!result.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Object ||
            !details.TryGetProperty("mcpAppPreview", out var preview) || preview.ValueKind != JsonValueKind.Object ||
            String(preview, "kind") != "canvas")
            return [];
        var presentation = preview.TryGetProperty("presentation", out var p) ? p : default;
        var target = String(presentation, "target") ?? String(preview, "target");
        if (target is not (null or "assistant_message")) return [];
        var view = preview.TryGetProperty("view", out var v) ? v : default;
        var app = preview.TryGetProperty("mcpApp", out var a) ? a : default;
        var appId = String(app, "viewId")?.Trim();
        if (string.IsNullOrWhiteSpace(appId) || appId.Length > 128 ||
            (String(view, "id") ?? String(view, "docId")) != appId)
            return [];
        return [new(ConversationCanvasContentKind.McpApp, appId, null,
            Bounded(String(presentation, "title") ?? String(view, "title"), 512),
            String(presentation, "sandbox") != "strict", OriginSessionKey: String(app, "originSessionKey"),
            ToolCallId: String(app, "toolCallId"), McpApp: ReadMcpAppMetadata(app, appId))];
    }

    private static ConversationMcpAppMetadata ReadMcpAppMetadata(JsonElement app, string viewId) =>
        new(viewId, Bounded(String(app, "serverName"), 256), Bounded(String(app, "toolName"), 256),
            Bounded(String(app, "uiResourceUri"), 2048), Bounded(String(app, "toolCallId"), 512),
            Bounded(String(app, "originSessionKey"), 512), String(app, "resultMetaState"));

    private static string? Bounded(string? value, int maximum) =>
        value is { Length: var length } && length > maximum ? value[..maximum] : value;

    private static string? String(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}

public sealed record CanvasDocumentRequest(string DocumentId, string SessionKey);

/// <summary>HTML arrived over authenticated RPC; the public sandbox URL carries no credential.</summary>
public sealed record CanvasDocumentView(string Html, Uri GatewayOrigin, Uri SandboxUrl, string? EmbedSandboxMode = null);

public sealed class CanvasDocumentPendingException : InvalidOperationException
{
    public CanvasDocumentPendingException()
        : base("The Gateway has not supplied current Canvas document availability.") { }
}
