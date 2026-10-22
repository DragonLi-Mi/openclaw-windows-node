using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.Shared;

public enum ConversationMcpAppUiMethod
{
    Initialize,
    Ping,
    Initialized,
    CallTool,
    ListTools,
    ListResources,
    ListResourceTemplates,
    ReadResource,
    WriteResource,
    SubscribeResource,
    UnsubscribeResource,
    OpenFile,
    UpdateModelContext,
    Message,
    OpenLink,
    RequestDisplayMode,
    SizeChanged,
    RequestTeardown,
    Cancelled,
}

public sealed record ConversationMcpAppUiRequest(
    ConversationMcpAppUiMethod Method, JsonElement? Id, JsonElement Parameters, JsonElement? Metadata);

public enum ConversationMcpAppHandshakeState { AwaitingInitialize, AwaitingInitialized, Ready, Retiring }

public sealed record ConversationMcpAppAccess(bool CanRead, bool CanWrite)
{
    public static ConversationMcpAppAccess FromScopes(IReadOnlyList<string> scopes)
    {
        var write = scopes.Contains("operator.admin", StringComparer.Ordinal) ||
            scopes.Contains("operator.write", StringComparer.Ordinal);
        return new(write || scopes.Contains("operator.read", StringComparer.Ordinal), write);
    }
}

public sealed class ConversationMcpAppProtocolException(int code, string message) : InvalidOperationException(message)
{
    public int Code { get; } = code;
}

public enum ConversationMcpAppMessageTarget { Active, New }

public sealed record ConversationMcpAppMessage(
    JsonElement Content, ConversationMcpAppMessageTarget Target, string PlainText);

public sealed record ConversationMcpAppPluginRoute(string PluginId, string? Marketplace, string ToolName, string DeepLink);
public sealed record ConversationMcpAppLink(Uri Url, ConversationMcpAppPluginRoute? Plugin = null);

/// <summary>Native host-side adaptation of the MCP Apps 2026-01-26 wire contract, not arbitrary RPC.</summary>
public static class ConversationMcpAppProtocol
{
    public const string ProtocolVersion = "2026-01-26";
    public const int MaximumMessageCharacters = 8 * 1024 * 1024;
    private static readonly JsonElement EmptyParameters = JsonSerializer.SerializeToElement(new { });

    public static ConversationMcpAppUiRequest ParseRequest(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String ||
            version.GetString() != "2.0" ||
            !message.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
            throw InvalidRequest();
        JsonElement? id = null;
        if (message.TryGetProperty("id", out var requestId))
        {
            if (requestId.ValueKind != JsonValueKind.String &&
                (requestId.ValueKind != JsonValueKind.Number || !requestId.TryGetInt64(out _)))
                throw InvalidRequest();
            id = requestId.Clone();
        }
        var parameters = message.TryGetProperty("params", out var p) ? p : EmptyParameters;
        if (parameters.ValueKind != JsonValueKind.Object) throw InvalidParameters();
        var metadata = ReadMetadata(message) ?? ReadMetadata(parameters);
        var kind = method.GetString() switch
        {
            "ui/initialize" => ConversationMcpAppUiMethod.Initialize,
            "ping" => ConversationMcpAppUiMethod.Ping,
            "ui/notifications/initialized" => ConversationMcpAppUiMethod.Initialized,
            "tools/call" => ConversationMcpAppUiMethod.CallTool,
            "tools/list" => ConversationMcpAppUiMethod.ListTools,
            "resources/list" => ConversationMcpAppUiMethod.ListResources,
            "resources/templates/list" => ConversationMcpAppUiMethod.ListResourceTemplates,
            "resources/read" => ConversationMcpAppUiMethod.ReadResource,
            "openai/resources/write" => ConversationMcpAppUiMethod.WriteResource,
            "resources/subscribe" => ConversationMcpAppUiMethod.SubscribeResource,
            "resources/unsubscribe" => ConversationMcpAppUiMethod.UnsubscribeResource,
            "openai/files/open" => ConversationMcpAppUiMethod.OpenFile,
            "ui/update-model-context" => ConversationMcpAppUiMethod.UpdateModelContext,
            "ui/message" => ConversationMcpAppUiMethod.Message,
            "ui/open-link" => ConversationMcpAppUiMethod.OpenLink,
            "ui/request-display-mode" => ConversationMcpAppUiMethod.RequestDisplayMode,
            "ui/notifications/size-changed" => ConversationMcpAppUiMethod.SizeChanged,
            "ui/notifications/request-teardown" => ConversationMcpAppUiMethod.RequestTeardown,
            "notifications/cancelled" => ConversationMcpAppUiMethod.Cancelled,
            _ => throw new ConversationMcpAppProtocolException(-32601, "This host method is not available."),
        };
        var notification = kind is ConversationMcpAppUiMethod.Initialized or ConversationMcpAppUiMethod.SizeChanged or
            ConversationMcpAppUiMethod.RequestTeardown or ConversationMcpAppUiMethod.Cancelled;
        if (notification == id.HasValue) throw InvalidRequest();
        return new(kind, id, parameters.Clone(), metadata);
    }

    public static void RequireAvailable(ConversationMcpAppUiRequest request, ConversationMcpAppView view,
        ConversationMcpAppAccess access, ConversationMcpAppHandshakeState phase)
    {
        if (!access.CanRead || phase == ConversationMcpAppHandshakeState.Retiring) throw PermissionDenied();
        if (request.Method == ConversationMcpAppUiMethod.Ping) return;
        if (request.Method == ConversationMcpAppUiMethod.Initialize)
        {
            if (phase != ConversationMcpAppHandshakeState.AwaitingInitialize)
                throw new ConversationMcpAppProtocolException(-32600, "The App has already initialized.");
            return;
        }
        if (request.Method == ConversationMcpAppUiMethod.Initialized)
        {
            if (phase != ConversationMcpAppHandshakeState.AwaitingInitialized)
                throw new ConversationMcpAppProtocolException(-32000, "The App has not initialized.");
            return;
        }
        if (phase != ConversationMcpAppHandshakeState.Ready)
            throw new ConversationMcpAppProtocolException(-32000, "The App has not completed initialization.");
        var allowed = request.Method switch
        {
            ConversationMcpAppUiMethod.CallTool => access.CanWrite,
            ConversationMcpAppUiMethod.ListResources or ConversationMcpAppUiMethod.ListResourceTemplates or
                ConversationMcpAppUiMethod.ReadResource => view.MessageSupported,
            ConversationMcpAppUiMethod.WriteResource => access.CanWrite && view.FileResourcesSupported,
            ConversationMcpAppUiMethod.SubscribeResource or ConversationMcpAppUiMethod.UnsubscribeResource => view.FileResourcesSupported,
            ConversationMcpAppUiMethod.OpenFile => view.OpenFilesSupported,
            ConversationMcpAppUiMethod.UpdateModelContext => access.CanWrite && view.UpdateModelContextSupported,
            ConversationMcpAppUiMethod.Message => access.CanWrite && view.MessageSupported,
            _ => true,
        };
        if (!allowed) throw PermissionDenied();
    }

    public static JsonElement BuildHostCapabilities(ConversationMcpAppView view, ConversationMcpAppAccess access)
    {
        if (!access.CanRead) throw PermissionDenied();
        var capabilities = new JsonObject
        {
            ["openLinks"] = new JsonObject(),
            ["serverTools"] = new JsonObject(),
            ["sandbox"] = new JsonObject { ["csp"] = Node(view.Csp) ?? new JsonObject() },
        };
        var experimental = new JsonObject();
        if (view.MessageSupported && access.CanWrite)
        {
            capabilities["serverResources"] = new JsonObject();
            capabilities["message"] = Modalities(true);
            experimental["openai/message"] = new JsonObject();
        }
        else if (view.MessageSupported) capabilities["serverResources"] = new JsonObject();
        if (view.UpdateModelContextSupported && access.CanWrite)
        {
            var modalities = Modalities(view.RichModelContextSupported);
            if (view.RichModelContextSupported) modalities["structuredContent"] = new JsonObject();
            capabilities["updateModelContext"] = modalities;
            if (view.RichModelContextSupported) experimental["openai/modelContext"] = new JsonObject();
        }
        if (view.FileResourcesSupported) experimental["openai/resource"] = new JsonObject();
        if (view.OpenFilesSupported) experimental["openai/files"] = new JsonObject();
        capabilities["experimental"] = experimental;
        return JsonSerializer.SerializeToElement(capabilities);
    }

    public static (IReadOnlyList<string> Available, string Initial) NegotiateDisplayModes(
        ConversationMcpAppView view, JsonElement appCapabilities)
    {
        var appModes = OptionalModes(appCapabilities, "availableDisplayModes");
        var resource = view.DisplayModes;
        var resourceModes = resource is { } r ? OptionalModes(r, "availableDisplayModes") : null;
        var preferred = resource is { } mode && mode.TryGetProperty("preferredDisplayMode", out var p)
            ? RequiredString(p) : null;
        var hints = resourceModes ?? (preferred is null ? appModes : [preferred]);
        var available = new[] { "inline", "fullscreen" }
            .Where(mode => (hints is null || hints.Contains(mode, StringComparer.Ordinal)) &&
                (appModes is null || appModes.Contains(mode, StringComparer.Ordinal)))
            .ToArray();
        if (available.Length == 0)
            throw new ConversationMcpAppProtocolException(-32000, "The App supports no available host display mode.");
        return (available, preferred is not null && available.Contains(preferred, StringComparer.Ordinal)
            ? preferred : available[0]);
    }

    public static void ValidateInitialize(JsonElement parameters)
    {
        RequiredString(parameters, "protocolVersion");
        if (!parameters.TryGetProperty("appInfo", out var info) || info.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("appCapabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object)
            throw InvalidParameters();
        RequiredString(info, "name");
        RequiredString(info, "version");
    }

    public static JsonElement InitializeResult(JsonElement parameters, ConversationMcpAppView view,
        ConversationMcpAppAccess access, string hostName, string hostVersion, JsonElement hostContext)
    {
        ValidateInitialize(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostVersion);
        if (hostContext.ValueKind != JsonValueKind.Object) throw InvalidParameters();
        return JsonSerializer.SerializeToElement(new
        {
            protocolVersion = ProtocolVersion,
            hostInfo = new { name = hostName, version = hostVersion },
            hostCapabilities = BuildHostCapabilities(view, access),
            hostContext,
        });
    }

    public static ConversationMcpAppOperation? GatewayOperation(ConversationMcpAppUiMethod method) => method switch
    {
        ConversationMcpAppUiMethod.CallTool => ConversationMcpAppOperation.CallTool,
        ConversationMcpAppUiMethod.ListTools => ConversationMcpAppOperation.ListTools,
        ConversationMcpAppUiMethod.ListResources => ConversationMcpAppOperation.ListResources,
        ConversationMcpAppUiMethod.ListResourceTemplates => ConversationMcpAppOperation.ListResourceTemplates,
        ConversationMcpAppUiMethod.ReadResource => ConversationMcpAppOperation.ReadResource,
        ConversationMcpAppUiMethod.WriteResource => ConversationMcpAppOperation.WriteResource,
        ConversationMcpAppUiMethod.SubscribeResource => ConversationMcpAppOperation.SubscribeResource,
        ConversationMcpAppUiMethod.UnsubscribeResource => ConversationMcpAppOperation.UnsubscribeResource,
        ConversationMcpAppUiMethod.OpenFile => ConversationMcpAppOperation.OpenFile,
        ConversationMcpAppUiMethod.UpdateModelContext => ConversationMcpAppOperation.UpdateModelContext,
        _ => null,
    };

    public static JsonElement GatewayParameters(ConversationMcpAppUiRequest request)
    {
        var parameters = JsonNode.Parse(request.Parameters.GetRawText())!.AsObject();
        if (request.Metadata is { } metadata) parameters["_meta"] = Node(metadata);
        if (request.Method == ConversationMcpAppUiMethod.CallTool)
        {
            var name = RequiredString(request.Parameters, "name");
            parameters.Remove("name");
            parameters["toolName"] = name;
        }
        return JsonSerializer.SerializeToElement(parameters);
    }

    public static ConversationMcpAppMessage ReadMessage(ConversationMcpAppUiRequest request)
    {
        if (request.Method != ConversationMcpAppUiMethod.Message ||
            RequiredString(request.Parameters, "role") != "user" ||
            !request.Parameters.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array || content.GetArrayLength() == 0 ||
            content.GetRawText().Length > MaximumMessageCharacters)
            throw InvalidParameters();
        var plainText = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            ValidateContent(block);
            if (RequiredString(block, "type") == "text" && !HasTitle(block))
                plainText.Add(RequiredString(block, "text"));
        }
        var text = string.Join("\n\n", plainText);
        var start = 0;
        while (start < text.Length && (char.IsWhiteSpace(text[start]) || text[start] == '\uFEFF')) start++;
        if (start < text.Length && text[start] is '/' or '!')
            throw InvalidParameters();
        var target = ConversationMcpAppMessageTarget.Active;
        if (request.Metadata is { } metadata && metadata.TryGetProperty("openai/message", out var options))
        {
            if (options.ValueKind != JsonValueKind.Object) throw InvalidParameters();
            if (options.TryGetProperty("target", out var t))
                target = RequiredString(t) switch
                {
                    "active" => ConversationMcpAppMessageTarget.Active,
                    "new" => ConversationMcpAppMessageTarget.New,
                    _ => throw InvalidParameters(),
                };
            if (options.TryGetProperty("send", out var send) && send.ValueKind != JsonValueKind.True)
                throw InvalidParameters();
        }
        return new(content.Clone(), target, text);
    }

    public static void ValidateContextUpdate(JsonElement parameters, bool richContent)
    {
        if (parameters.TryGetProperty("structuredContent", out var structured) &&
            (!richContent || structured.ValueKind != JsonValueKind.Object))
            throw InvalidParameters();
        if (!parameters.TryGetProperty("content", out var content)) return;
        if (content.ValueKind != JsonValueKind.Array || content.GetRawText().Length > MaximumMessageCharacters)
            throw InvalidParameters();
        foreach (var block in content.EnumerateArray())
        {
            ValidateContent(block);
            if (!richContent && RequiredString(block, "type") != "text") throw InvalidParameters();
        }
    }

    public static ConversationMcpAppLink ReadLink(string raw)
    {
        if (raw.Any(character => char.IsControl(character)) ||
            !Uri.TryCreate(raw, UriKind.Absolute, out var url) || !string.IsNullOrEmpty(url.UserInfo))
            throw InvalidParameters();
        var native = url.Scheme is "codex" or "chatgpt" or "openclaw" && url.Host == "plugins";
        var web = url.Scheme == "https" && url.Host == "chatgpt.com" &&
            url.AbsolutePath.StartsWith("/plugins/", StringComparison.Ordinal);
        if (native || web)
        {
            if (!string.IsNullOrEmpty(url.Fragment) || !url.IsDefaultPort) throw InvalidParameters();
            var path = web ? url.AbsolutePath["/plugins".Length..] : url.AbsolutePath;
            var parts = path.TrimEnd('/').Split('/');
            if (parts.Length != 4 || parts[0] != "" || parts[2] != "app") throw InvalidParameters();
            var identity = native ? parts[1].Split('@') : [parts[1]];
            if (identity.Length is < 1 or > 2 || identity.Any(string.IsNullOrEmpty)) throw InvalidParameters();
            var pluginId = Uri.UnescapeDataString(identity[0]);
            var marketplace = identity.Length == 2 ? Uri.UnescapeDataString(identity[1]) : null;
            var tool = Uri.UnescapeDataString(parts[3]);
            var deepLink = "/";
            var hasPath = false;
            foreach (var query in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = query.Split('=', 2);
                if (Uri.UnescapeDataString(pair[0]) != "path" || hasPath) throw InvalidParameters();
                hasPath = true;
                deepLink = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace("+", " ", StringComparison.Ordinal)) : "";
            }
            if (string.IsNullOrEmpty(pluginId) || string.IsNullOrEmpty(tool) || !ValidDeepLink(deepLink))
                throw InvalidParameters();
            return new(url, new(pluginId, marketplace, tool, deepLink));
        }
        if (url.Scheme is not ("https" or "http")) throw InvalidParameters();
        return new(url);
    }

    private static bool ValidDeepLink(string value) => value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal) &&
        !value.Any(character => character is '#' or '\\' or ' ' || char.IsControl(character));

    public static void ValidateGatewayResult(ConversationMcpAppOperation operation, JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The Gateway returned an invalid App operation result.");
        var arrayKey = operation switch
        {
            ConversationMcpAppOperation.CallTool => "content",
            ConversationMcpAppOperation.ListTools => "tools",
            ConversationMcpAppOperation.ListResources => "resources",
            ConversationMcpAppOperation.ListResourceTemplates => "resourceTemplates",
            ConversationMcpAppOperation.ReadResource => "contents",
            _ => null
        };
        if (arrayKey is not null &&
            (!result.TryGetProperty(arrayKey, out var items) || items.ValueKind != JsonValueKind.Array ||
            items.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object)))
            throw new InvalidDataException("The Gateway returned an invalid App result collection.");
        if ((operation is ConversationMcpAppOperation.ListTools or ConversationMcpAppOperation.ListResources or
            ConversationMcpAppOperation.ListResourceTemplates) &&
            result.TryGetProperty("nextCursor", out var cursor) && cursor.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The Gateway returned an invalid App result cursor.");
        if (result.TryGetProperty("isError", out var error) && error.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("The Gateway returned an invalid App error flag.");
    }

    private static void ValidateContent(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object) throw InvalidParameters();
        _ = ReadMetadata(block);
        switch (RequiredString(block, "type"))
        {
            case "text":
                RequiredString(block, "text");
                break;
            case "image":
                RequiredString(block, "mimeType");
                RequiredString(block, "data");
                break;
            case "resource_link":
                RequiredString(block, "uri");
                RequiredString(block, "name");
                break;
            case "resource":
                if (!block.TryGetProperty("resource", out var resource) || resource.ValueKind != JsonValueKind.Object)
                    throw InvalidParameters();
                RequiredString(resource, "uri");
                var text = resource.TryGetProperty("text", out var t);
                var blob = resource.TryGetProperty("blob", out var b);
                if (text == blob || text && t.ValueKind != JsonValueKind.String ||
                    blob && b.ValueKind != JsonValueKind.String) throw InvalidParameters();
                break;
            default:
                throw InvalidParameters();
        }
    }

    private static bool HasTitle(JsonElement block) =>
        ReadMetadata(block) is { } metadata && metadata.TryGetProperty("openai/title", out var title) &&
        title.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(title.GetString());

    private static IReadOnlyList<string>? OptionalModes(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var modes)) return null;
        if (modes.ValueKind != JsonValueKind.Array) throw InvalidParameters();
        return modes.EnumerateArray().Select(RequiredString).ToArray();
    }

    private static JsonElement? ReadMetadata(JsonElement value)
    {
        if (!value.TryGetProperty("_meta", out var metadata)) return null;
        if (metadata.ValueKind != JsonValueKind.Object) throw InvalidParameters();
        return metadata.Clone();
    }

    private static string RequiredString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) ? RequiredString(property) : throw InvalidParameters();

    private static string RequiredString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw InvalidParameters();

    private static JsonNode? Node(JsonElement? element) =>
        element.HasValue ? JsonNode.Parse(element.Value.GetRawText()) : null;

    private static JsonObject Modalities(bool rich) => rich
        ? new() { ["text"] = new JsonObject(), ["image"] = new JsonObject(), ["resource"] = new JsonObject(), ["resourceLink"] = new JsonObject() }
        : new() { ["text"] = new JsonObject() };

    private static ConversationMcpAppProtocolException InvalidRequest() => new(-32600, "Invalid App request.");
    private static ConversationMcpAppProtocolException InvalidParameters() => new(-32602, "Invalid App request parameters.");
    private static ConversationMcpAppProtocolException PermissionDenied() => new(-32001, "This App capability is not available.");
}
