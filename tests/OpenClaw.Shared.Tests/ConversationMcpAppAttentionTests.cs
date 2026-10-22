using System.Text.Json;

namespace OpenClaw.Shared.Tests;

public sealed class ConversationMcpAppAttentionTests
{
    private const string Question = """
        {"id":"question-one","questions":[{"questionId":"choice","header":"Choice","question":"Synthetic private question","options":[{"label":"Yes"}]}],
         "agentId":"agent-one","sessionKey":"agent:agent-one:session","runId":"wire-run",
         "createdAtMs":1,"expiresAtMs":2,"status":"pending"}
        """;

    private const string PluginApproval = """
        {"approvalKind":"plugin","id":"approval-one",
         "request":{"title":"Synthetic private title","description":"Synthetic private detail",
                    "agentId":"agent-one","sessionKey":"agent:agent-one:session","runId":"wire-run"},
         "createdAtMs":1,"expiresAtMs":2,"sessionKey":"wrong-outer-session","agentId":"wrong-outer-agent"}
        """;

    [Theory]
    [InlineData("question")]
    [InlineData("plugin")]
    public void CapturesExplicitRoutingAndDetachesTheWholeOriginalPayloadWithoutExposingIt(string kind)
    {
        ConversationMcpAppAttention? attention;
        using (var document = JsonDocument.Parse(kind == "question" ? Question : PluginApproval))
            attention = kind == "question"
                ? ConversationMcpAppAttention.ParseQuestion(document.RootElement)
                : ConversationMcpAppAttention.ParsePluginApproval(document.RootElement);

        Assert.NotNull(attention);
        Assert.Equal("agent:agent-one:session", attention.SessionKey);
        Assert.Equal("agent-one", attention.AgentId);
        Assert.Equal(kind == "question" ? "question-one" : "approval-one", attention.Payload.GetProperty("id").GetString());
        Assert.Equal(2, attention.Payload.GetProperty("expiresAtMs").GetInt32());
        var routedPayload = kind == "question" ? attention.Payload : attention.Payload.GetProperty("request");
        Assert.Equal("wire-run", routedPayload.GetProperty("runId").GetString());
        Assert.DoesNotContain("private", attention.ToString());
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(attention));
        Assert.Equal(2, serialized.RootElement.EnumerateObject().Count());
        Assert.DoesNotContain("private", serialized.RootElement.GetRawText());
        Assert.False(serialized.RootElement.TryGetProperty("RunId", out _));
        Assert.False(serialized.RootElement.TryGetProperty("ViewId", out _));
        Assert.False(serialized.RootElement.TryGetProperty("Payload", out _));
    }

    [Theory]
    [InlineData("answered")]
    [InlineData("cancelled")]
    [InlineData("expired")]
    public void QuestionAttentionRequiresPendingRatherThanJustAnEventName(string status)
    {
        using var document = JsonDocument.Parse(Question.Replace("\"pending\"", JsonSerializer.Serialize(status)));
        Assert.Null(ConversationMcpAppAttention.ParseQuestion(document.RootElement));
    }

    [Fact]
    public void PluginAttentionDoesNotRequireAnInventedQuestionStatus()
    {
        using var document = JsonDocument.Parse(PluginApproval);
        Assert.False(document.RootElement.TryGetProperty("status", out _));
        Assert.NotNull(ConversationMcpAppAttention.ParsePluginApproval(document.RootElement));
    }

    [Theory]
    [InlineData("""{"status":"pending"}""")]
    [InlineData("""{"status":"pending","sessionKey":null,"agentId":"agent-one"}""")]
    public void UnroutedQuestionRetainsThePayloadButCannotAcquireTheCurrentSession(string json)
    {
        using var document = JsonDocument.Parse(json);
        var attention = ConversationMcpAppAttention.ParseQuestion(document.RootElement);
        Assert.NotNull(attention);
        Assert.Null(attention.SessionKey);
        Assert.Equal(document.RootElement.GetRawText(), attention.Payload.GetRawText());
    }

    [Theory]
    [InlineData("""{"sessionKey":"wrong-outer","request":{}}""")]
    [InlineData("""{"sessionKey":"wrong-outer","request":{"sessionKey":null}}""")]
    public void UnroutedPluginRequestCannotFallBackToTheOuterOrCurrentSession(string json)
    {
        using var document = JsonDocument.Parse(json);
        var attention = ConversationMcpAppAttention.ParsePluginApproval(document.RootElement);
        Assert.NotNull(attention);
        Assert.Null(attention.SessionKey);
        Assert.Equal("wrong-outer", attention.Payload.GetProperty("sessionKey").GetString());
    }

    [Theory]
    [InlineData("""{"status":"pending","sessionKey":"exact-session"}""")]
    [InlineData("""{"status":"pending","sessionKey":"exact-session","agentId":null}""")]
    public void MissingAgentStaysMissingRatherThanBecomingTheSelectedAgent(string json)
    {
        using var document = JsonDocument.Parse(json);
        var attention = ConversationMcpAppAttention.ParseQuestion(document.RootElement);
        Assert.NotNull(attention);
        Assert.Equal("exact-session", attention.SessionKey);
        Assert.Null(attention.AgentId);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"status":12,"sessionKey":"one"}""")]
    [InlineData("""{"status":"pending","sessionKey":12}""")]
    [InlineData("""{"status":"pending","sessionKey":" "}""")]
    [InlineData("""{"status":"pending","sessionKey":"one","agentId":12}""")]
    public void MalformedQuestionRoutingNeverBecomesAttention(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<JsonException>(() => ConversationMcpAppAttention.ParseQuestion(document.RootElement));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"request":null}""")]
    [InlineData("""{"request":[]}""")]
    [InlineData("""{"request":{"sessionKey":12}}""")]
    [InlineData("""{"request":{"sessionKey":"one","agentId":12}}""")]
    public void MalformedPluginRoutingNeverBecomesAttention(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<JsonException>(() => ConversationMcpAppAttention.ParsePluginApproval(document.RootElement));
    }
}
