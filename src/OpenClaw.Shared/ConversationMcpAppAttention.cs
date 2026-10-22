using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared;

/// <summary>
/// Explicit wire routing for conversation attention, not a question renderer,
/// approval decision or authority receipt. Publication must retain its original source.
/// </summary>
public sealed class ConversationMcpAppAttention
{
    private ConversationMcpAppAttention(string? sessionKey, string? agentId, JsonElement payload)
    {
        SessionKey = sessionKey;
        AgentId = agentId;
        Payload = payload.Clone();
    }

    public string? SessionKey { get; }
    public string? AgentId { get; }
    [JsonIgnore] public JsonElement Payload { get; }

    internal static ConversationMcpAppAttention? ParseQuestion(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
            throw new JsonException("The question attention notification is malformed.");
        return status.GetString() == "pending" ? ParseRouting(payload, payload) : null;
    }

    internal static ConversationMcpAppAttention? ParsePluginApproval(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("request", out var request) || request.ValueKind != JsonValueKind.Object)
            throw new JsonException("The plugin attention notification is malformed.");
        return ParseRouting(request, payload);
    }

    private static ConversationMcpAppAttention ParseRouting(JsonElement input, JsonElement payload) =>
        new(ReadOptionalString(input, "sessionKey"), ReadOptionalString(input, "agentId"), payload);

    private static string? ReadOptionalString(JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString() : throw new JsonException("The attention notification routing is malformed.");
    }
}
