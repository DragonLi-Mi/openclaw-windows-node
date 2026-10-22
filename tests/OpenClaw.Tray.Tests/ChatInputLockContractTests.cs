namespace OpenClaw.Tray.Tests;

public sealed class ChatInputLockContractTests
{
    [Fact]
    public void ConversationTransactionsCannotDispatchOrBlockOnNetworkUnderStateLock()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ChatConversationState.cs"));
        foreach (var forbidden in new[]
        {
            "async ", "await ", ".Wait(", ".Result", "GetAwaiter(",
            "IChatGatewayBridge", "IOperatorGatewayClient", "SendRawAsync", "ExternalOrigin.IsCurrent",
        })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }
}
