using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class GatewayRetryMetadataTests
{
    [Theory]
    [InlineData("{}", null, null)]
    [InlineData("""{"retryable":false,"retryAfterMs":0}""", false, 0)]
    [InlineData("""{"retryable":true,"retryAfterMs":2500}""", true, 2500)]
    [InlineData("""{"retryable":"true","retryAfterMs":"2500"}""", null, null)]
    [InlineData("""{"retryable":null,"retryAfterMs":-1}""", null, null)]
    [InlineData("""{"retryable":1,"retryAfterMs":1.5}""", null, null)]
    [InlineData("""{"retryable":[],"retryAfterMs":2147483648}""", null, null)]
    public void GatewayRetryMetadataPreservesOnlyTypedEnvelopeValues(string fields, bool? retryable, int? delay)
    {
        var source = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(fields)!;
        source["message"] = JsonSerializer.SerializeToElement("Do not infer retryable from this message.");
        source["details"] = JsonSerializer.SerializeToElement(new { retryable = true, retryAfterMs = 9999 });
        var envelope = JsonSerializer.SerializeToElement(new { error = source });
        var error = GatewayRequestException.FromResponse(envelope, "Unavailable");
        Assert.Equal(retryable, error.Retryable);
        Assert.Equal(delay, error.RetryAfterMs);
        Assert.IsAssignableFrom<InvalidOperationException>(error);
    }
}

public sealed partial class GatewayProtocolLiveRoundTripTests
{
    [Theory]
    [InlineData("{}", null, null)]
    [InlineData("""{"retryable":false,"retryAfterMs":0}""", false, 0)]
    [InlineData("""{"retryable":true,"retryAfterMs":2500}""", true, 2500)]
    [InlineData("""{"retryable":"true","retryAfterMs":"2500"}""", null, null)]
    [InlineData("""{"retryable":null,"retryAfterMs":-1}""", null, null)]
    public async Task GatewayRetryMetadataSurvivesTheRealResponseEnvelope(string fields, bool? retryable, int? delay)
    {
        using var server = new LoopbackGatewayServer();
        var error = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(fields)!;
        error["code"] = JsonSerializer.SerializeToElement("UNAVAILABLE");
        error["message"] = JsonSerializer.SerializeToElement("Unavailable");
        server.OnMethod("retry-metadata-proof", _ => new LoopbackResponse(false, Error: error));
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        await ConnectAndWaitAsync(client, server);
        var result = await Assert.ThrowsAsync<GatewayRequestException>(() => client.SendWizardRequestAsync("retry-metadata-proof"));
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(delay, result.RetryAfterMs);
        await client.DisconnectAsync();
    }

    [Fact]
    public async Task CanvasPolicySignalUsesOnlyConfigEventsAndTheAcceptedReadyEpoch()
    {
        using var server = new LoopbackGatewayServer();
        server.OnMethod("policy-barrier", _ => new { });
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "test-token", identityPath: _identityDir);
        var signals = new List<CanvasPolicyInvalidation>();
        var generic = 0;
        client.CanvasPolicyInvalidated += (_, signal) => signals.Add(signal);
        client.ModelCatalogInvalidated += (_, _) => generic++;
        await ConnectAndWaitAsync(client, server);
        generic = 0;
        var epoch = client.SessionMutationConnectionEpoch!.Value;
        await server.SendTextToCurrentSocketAsync("""{"type":"event","event":"chat.metadata.changed","payload":{}}""");
        await client.SendWizardRequestAsync("policy-barrier");
        Assert.Empty(signals);
        Assert.Equal(1, generic);
        Assert.Equal(0, client.CanvasPolicyRevision);
        await server.SendTextToCurrentSocketAsync("""{"type":"event","event":"config.changed","payload":{}}""");
        await client.SendWizardRequestAsync("policy-barrier");
        var signal = Assert.Single(signals);
        Assert.Equal(epoch, signal.ConnectionEpoch);
        Assert.Equal(client.AuthenticatedSigningDeviceId, signal.SigningDeviceId);
        Assert.Equal(1, signal.SourceRevision);
        Assert.Equal(2, generic);
        await client.DisconnectAsync();
        var parser = typeof(OpenClawGatewayClient).GetMethod("ProcessMessage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        parser.Invoke(client, ["""{"type":"event","event":"config.changed","payload":{}}"""]);
        Assert.Single(signals);
        Assert.Equal(1, client.CanvasPolicyRevision);
        await ConnectAndWaitAsync(client, server);
        Assert.NotEqual(signal.ConnectionEpoch, client.SessionMutationConnectionEpoch);
        await server.SendTextToCurrentSocketAsync("""{"type":"event","event":"config.changed","payload":{}}""");
        await client.SendWizardRequestAsync("policy-barrier");
        Assert.Equal(2, signals.Count);
        Assert.Equal(client.SessionMutationConnectionEpoch, signals[1].ConnectionEpoch);
        Assert.Equal(2, signals[1].SourceRevision);
        await client.DisconnectAsync();
    }
}
