using System.Text.Json;

namespace OpenClaw.Shared;

public sealed record ConversationMcpAppScope(string SessionKey, string ViewId, string? AgentId = null)
{
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SessionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(ViewId);
        if (AgentId is not null) ArgumentException.ThrowIfNullOrWhiteSpace(AgentId);
    }
}

public enum ConversationMcpAppOperation
{
    CallTool,
    ListTools,
    ListResources,
    ListResourceTemplates,
    ReadResource,
    WriteResource,
    SubscribeResource,
    UnsubscribeResource,
    OpenFile,
    GetModelContext,
    UpdateModelContext,
    RemoveModelContext,
}

/// <summary>MCP App capabilities come from this authenticated view, not from Canvas's script policy.</summary>
public sealed record ConversationMcpAppView
{
    public required string Html { get; init; }
    public required Uri GatewayOrigin { get; init; }
    public required Uri SandboxUrl { get; init; }
    public JsonElement? Csp { get; init; }
    public JsonElement? ToolInput { get; init; }
    public JsonElement? ToolResult { get; init; }
    public JsonElement? HostContext { get; init; }
    public JsonElement? DisplayModes { get; init; }
    public string? DisplayMode { get; init; }
    public bool MessageSupported { get; init; }
    public bool UpdateModelContextSupported { get; init; }
    public bool RichModelContextSupported { get; init; }
    public bool FileResourcesSupported { get; init; }
    public bool OpenFilesSupported { get; init; }

    internal static ConversationMcpAppView Parse(JsonElement payload, string gatewayUrl)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("html", out var html) || html.ValueKind != JsonValueKind.String ||
            html.GetString() is not { } content || content.Length > WebSocketClientBase.MaxInboundMessageChars)
            throw new InvalidDataException("The Gateway returned an invalid MCP App document.");
        var origin = ConversationCanvasBoundary.HttpOrigin(gatewayUrl);
        var mode = Optional(payload, "displayMode");
        if (mode is { } displayMode &&
            (displayMode.ValueKind != JsonValueKind.String || displayMode.GetString() is not ("inline" or "fullscreen")))
            throw new InvalidDataException("The Gateway returned an invalid MCP App display mode.");
        return new()
        {
            Html = content,
            GatewayOrigin = origin,
            SandboxUrl = ConversationCanvasBoundary.ResolveSandboxUrl(payload, origin),
            Csp = OptionalObject(payload, "csp"),
            ToolInput = Optional(payload, "toolInput"),
            ToolResult = OptionalObject(payload, "toolResult"),
            HostContext = OptionalObject(payload, "hostContext"),
            DisplayModes = OptionalObject(payload, "displayModes"),
            DisplayMode = mode?.GetString(),
            MessageSupported = Capability(payload, "messageSupported"),
            UpdateModelContextSupported = Capability(payload, "updateModelContextSupported"),
            RichModelContextSupported = Capability(payload, "richModelContextSupported"),
            FileResourcesSupported = Capability(payload, "fileResourcesSupported"),
            OpenFilesSupported = Capability(payload, "openFilesSupported"),
        };
    }

    private static JsonElement? Optional(JsonElement value, string key) =>
        value.TryGetProperty(key, out var property) ? property.Clone() : null;

    private static JsonElement? OptionalObject(JsonElement value, string key)
    {
        var property = Optional(value, key);
        if (property is { ValueKind: not JsonValueKind.Object })
            throw new InvalidDataException($"The MCP App {key} field must be an object.");
        return property;
    }

    private static bool Capability(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out var property)) return false;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"The MCP App {key} field must be boolean."),
        };
    }
}

public sealed class ConversationMcpAppPendingException : InvalidOperationException
{
    public ConversationMcpAppPendingException()
        : base("The Gateway has not supplied current MCP App availability.") { }
}

internal static class ConversationMcpAppRequests
{
    internal static string Method(ConversationMcpAppOperation operation) => operation switch
    {
        ConversationMcpAppOperation.CallTool => "mcp.app.callTool",
        ConversationMcpAppOperation.ListTools => "mcp.app.listTools",
        ConversationMcpAppOperation.ListResources => "mcp.app.listResources",
        ConversationMcpAppOperation.ListResourceTemplates => "mcp.app.listResourceTemplates",
        ConversationMcpAppOperation.ReadResource => "mcp.app.readResource",
        ConversationMcpAppOperation.WriteResource => "mcp.app.writeResource",
        ConversationMcpAppOperation.SubscribeResource => "mcp.app.subscribeResource",
        ConversationMcpAppOperation.UnsubscribeResource => "mcp.app.unsubscribeResource",
        ConversationMcpAppOperation.OpenFile => "mcp.app.openFile",
        ConversationMcpAppOperation.GetModelContext => "mcp.app.modelContext",
        ConversationMcpAppOperation.UpdateModelContext => "mcp.app.updateModelContext",
        ConversationMcpAppOperation.RemoveModelContext => "mcp.app.removeModelContext",
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    internal static Dictionary<string, object?> Parameters(
        ConversationMcpAppScope scope, ConversationMcpAppOperation operation, JsonElement parameters)
    {
        scope.Validate();
        _ = Method(operation);
        if (parameters.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("MCP App parameters must be an object.", nameof(parameters));
        switch (operation)
        {
            case ConversationMcpAppOperation.CallTool:
                RequireString(parameters, "toolName");
                OptionalKind(parameters, "arguments", JsonValueKind.Object);
                break;
            case ConversationMcpAppOperation.ListTools:
            case ConversationMcpAppOperation.ListResources:
            case ConversationMcpAppOperation.ListResourceTemplates:
                OptionalKind(parameters, "cursor", JsonValueKind.String);
                break;
            case ConversationMcpAppOperation.ReadResource:
            case ConversationMcpAppOperation.SubscribeResource:
            case ConversationMcpAppOperation.UnsubscribeResource:
                RequireString(parameters, "uri");
                break;
            case ConversationMcpAppOperation.WriteResource:
                RequireString(parameters, "uri");
                var text = parameters.TryGetProperty("text", out var t);
                var blob = parameters.TryGetProperty("blob", out var b);
                if (text == blob || text && t.ValueKind != JsonValueKind.String ||
                    blob && b.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("An MCP App resource write requires exactly one text or blob value.");
                if (parameters.TryGetProperty("ifMatch", out _)) RequireString(parameters, "ifMatch");
                break;
            case ConversationMcpAppOperation.OpenFile:
                RequireString(parameters, "path");
                break;
            case ConversationMcpAppOperation.UpdateModelContext:
                OptionalKind(parameters, "content", JsonValueKind.Array);
                OptionalKind(parameters, "structuredContent", JsonValueKind.Object);
                break;
            case ConversationMcpAppOperation.RemoveModelContext:
                RequireString(parameters, "updateId");
                OptionalKind(parameters, "index", JsonValueKind.Number);
                break;
        }
        OptionalKind(parameters, "_meta", JsonValueKind.Object);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in parameters.EnumerateObject())
            if (property.Name is not ("sessionKey" or "viewId" or "agentId"))
                result[property.Name] = property.Value.Clone();
        result["sessionKey"] = scope.SessionKey;
        result["viewId"] = scope.ViewId;
        if (scope.AgentId is not null) result["agentId"] = scope.AgentId;
        return result;
    }

    private static void RequireString(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw new ArgumentException($"MCP App {key} must be a non-empty string.");
    }

    private static void OptionalKind(JsonElement value, string key, JsonValueKind kind)
    {
        if (value.TryGetProperty(key, out var property) && property.ValueKind != kind)
            throw new ArgumentException($"MCP App {key} has an invalid type.");
    }
}
