using System.Text;
using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>Matches the separate-origin document contract, not node-global canvas navigation.</summary>
public sealed class ConversationCanvasBoundary
{
    public const int MaximumHtmlUtf8Bytes = 10 * 1024 * 1024;
    public const int MaximumPromptCharacters = 4000;
    private const string SandboxPath = "/mcp-app-sandbox";
    private readonly string _nonce;

    public ConversationCanvasBoundary(CanvasDocumentView document, string nonce, string? messageNonce = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        if (nonce.Length != 32 || nonce.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("Invalid Canvas presentation nonce.", nameof(nonce));
        GatewayOrigin = HttpOrigin(document.GatewayOrigin.AbsoluteUri);
        SandboxUrl = RequireHttp(document.SandboxUrl.AbsoluteUri);
        if (SameOrigin(GatewayOrigin, SandboxUrl) ||
            SandboxUrl.AbsolutePath != SandboxPath ||
            !string.IsNullOrEmpty(SandboxUrl.Fragment) ||
            GatewayOrigin.Scheme == "https" && SandboxUrl.Scheme != "https")
            throw new InvalidDataException("The Canvas sandbox must use a separate origin.");
        ValidatePublicQuery(SandboxUrl);
        messageNonce ??= nonce;
        if (messageNonce.Length != 32 || messageNonce.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("Invalid Canvas message nonce.", nameof(messageNonce));
        _nonce = messageNonce;
        ParentUrl = new Uri(GatewayOrigin, $"/__companion_canvas__/{nonce}/index.html");
    }

    public Uri GatewayOrigin { get; }
    public Uri SandboxUrl { get; }
    public Uri ParentUrl { get; }
    public string SandboxOrigin => SandboxUrl.GetLeftPart(UriPartial.Authority);

    public bool IsParent(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        string.Equals(uri.AbsoluteUri, ParentUrl.AbsoluteUri, StringComparison.Ordinal);

    public bool MayServeParent(string? url, string method, bool pendingNavigation) =>
        pendingNavigation && method == "GET" && IsParent(url);

    public bool MayNavigateFrame(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.AbsoluteUri == SandboxUrl.AbsoluteUri || uri.AbsoluteUri == "about:srcdoc" ||
         uri.AbsoluteUri == "about:blank");

    public bool IsNativeMessage(string? source, JsonElement message) =>
        IsParent(source) && message.ValueKind == JsonValueKind.Object &&
        message.TryGetProperty("nonce", out var nonce) &&
        nonce.ValueKind == JsonValueKind.String && nonce.GetString() == _nonce;

    public static string? ConversationalPrompt(string? raw)
    {
        if (raw is null) return null;
        var text = raw.Trim();
        if (text.Length is 0 or > MaximumPromptCharacters || text[0] is '/' or '!' ||
            text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t') ||
                character is >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069'))
            return null;
        return text;
    }

    public static bool AllowScripts(string? gatewayEmbedSandbox, ConversationCanvasContent content) =>
        gatewayEmbedSandbox is "scripts" or "trusted" && content.PreviewAllowsScripts;

    public static CanvasDocumentView ParseDocument(JsonElement payload, string gatewayUrl)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("html", out var htmlValue) || htmlValue.ValueKind != JsonValueKind.String ||
            !payload.TryGetProperty("sandboxUrl", out var sandboxValue) || sandboxValue.ValueKind != JsonValueKind.String ||
            !payload.TryGetProperty("sandboxPort", out var portValue) || portValue.ValueKind != JsonValueKind.Number ||
            !portValue.TryGetInt32(out var port) ||
            port is < 1 or > 65535)
            throw new InvalidDataException("The Gateway returned an invalid Canvas document.");
        var html = htmlValue.GetString()!;
        if (Encoding.UTF8.GetByteCount(html) > MaximumHtmlUtf8Bytes)
            throw new InvalidDataException("The Canvas document exceeds the supported size.");
        var gateway = HttpOrigin(gatewayUrl);
        Uri origin;
        if (payload.TryGetProperty("sandboxOrigin", out var originValue))
        {
            if (originValue.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("The Canvas sandbox origin is invalid.");
            var raw = originValue.GetString()!;
            origin = HttpOrigin(raw);
            if (raw.TrimEnd('/') != origin.GetLeftPart(UriPartial.Authority))
                throw new InvalidDataException("The Canvas sandbox origin is not an origin.");
        }
        else
        {
            origin = new UriBuilder(gateway) { Port = port }.Uri;
        }
        var sandbox = RequireHttp(new Uri(origin, sandboxValue.GetString()).AbsoluteUri);
        if (!SameOrigin(origin, sandbox))
            throw new InvalidDataException("The Canvas sandbox URL does not match its origin.");
        var embedSandbox = payload.TryGetProperty("embedSandbox", out var mode) &&
            mode.ValueKind == JsonValueKind.String && mode.GetString() is "strict" or "scripts" or "trusted"
                ? mode.GetString() : null;
        var result = new CanvasDocumentView(html, gateway, sandbox, embedSandbox);
        _ = new ConversationCanvasBoundary(result, new string('0', 32));
        return result;
    }

    public static Uri HttpOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("The Canvas Gateway origin is invalid.");
        var builder = new UriBuilder(uri) { Path = "/", Query = "", Fragment = "" };
        builder.Scheme = uri.Scheme switch
        {
            "ws" => "http",
            "wss" => "https",
            "http" or "https" => uri.Scheme,
            _ => throw new InvalidDataException("The Canvas Gateway scheme is invalid.")
        };
        return RequireHttp(builder.Uri.GetLeftPart(UriPartial.Authority));
    }

    private static Uri RequireHttp(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("The Canvas URL must be HTTP or HTTPS without credentials.");
        return uri;
    }

    public static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme == right.Scheme &&
        string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static void ValidatePublicQuery(Uri uri)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = Uri.UnescapeDataString(part.Split('=', 2)[0]);
            if (name is not ("v" or "csp") || !names.Add(name))
                throw new InvalidDataException("The Canvas public shell URL contains unsupported parameters.");
        }
    }
}
