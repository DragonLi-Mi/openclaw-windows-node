using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared;

public enum ConversationMcpAppGatewayEventKind { HostContextChanged, ResourceUpdated }

/// <summary>
/// A notification's original transport source and explicit wire view. Session,
/// run and canonical authority must come from the admitted subscription, not this payload.
/// </summary>
public sealed class ConversationMcpAppGatewayEventArgs : EventArgs
{
    private ConversationMcpAppGatewayEventArgs(GatewayDataReadSource source,
        ConversationMcpAppGatewayEventKind kind, string viewId, JsonElement parameters)
    {
        Source = source;
        Kind = kind;
        ViewId = viewId;
        Parameters = parameters;
    }

    [JsonIgnore] public GatewayDataReadSource Source { get; }
    public ConversationMcpAppGatewayEventKind Kind { get; }
    public string ViewId { get; }
    public JsonElement Parameters { get; }

    internal static ConversationMcpAppGatewayEventArgs Parse(GatewayDataReadSource source,
        ConversationMcpAppGatewayEventKind kind, JsonElement payload)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsCurrent) throw new GatewayDataReadRetiredException();
        var viewId = ReadString(payload, "viewId");
        JsonElement parameters;
        switch (kind)
        {
            case ConversationMcpAppGatewayEventKind.ResourceUpdated:
                parameters = JsonSerializer.SerializeToElement(new { uri = ReadString(payload, "uri") });
                break;
            case ConversationMcpAppGatewayEventKind.HostContextChanged:
                parameters = payload.TryGetProperty("modelContext", out var context) &&
                    context.ValueKind == JsonValueKind.Null &&
                    payload.TryGetProperty("updateId", out var updateId) && updateId.ValueKind == JsonValueKind.String
                    ? JsonSerializer.SerializeToElement(new { modelContext = (object?)null, updateId = updateId.GetString() })
                    : JsonSerializer.SerializeToElement(new { });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        return new(source, kind, viewId, parameters);
    }

    private static string ReadString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()! : throw new JsonException("The MCP App notification is malformed.");
}
