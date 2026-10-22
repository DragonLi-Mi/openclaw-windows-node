using System.Reflection;
using System.Text.Json;

namespace OpenClaw.Shared.Tests;

public sealed class GatewayDataReadSourceTests
{
    private static readonly TimeSpan DeadlockDeadline = TimeSpan.FromSeconds(10);

    public class ClientProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            throw new NotSupportedException(method?.Name);
    }

    private static IOperatorGatewayClient Client() =>
        DispatchProxy.Create<IOperatorGatewayClient, ClientProxy>();

    [Fact]
    public async Task OldAccountReadFaultsBeforeCompletionOrCatalogMutation()
    {
        var scope = new GatewayDataReadScope(Client(), () => 12);
        var source = scope.Capture()!;
        var registry = new PendingRequestRegistry();
        registry.OpenConnection();
        var read = registry.RegisterWizard("read-a", "models.list");
        Assert.True(registry.TryTake("read-a", out var resolution));
        scope.Invalidate();
        Assert.False(scope.CompleteRead(source, (WizardRequestResolution)resolution!,
            JsonSerializer.SerializeToElement(new { models = new[] { "private-a" } })));
        await Assert.ThrowsAsync<GatewayDataReadRetiredException>(() => read.Task);
        var mutated = false;
        Assert.False(scope.TryCommit(source, () => mutated = true));
        Assert.False(mutated);
    }

    [Fact]
    public void AToBToASameEpochCannotMakeRevisionZeroCurrentAgain()
    {
        var scope = new GatewayDataReadScope(Client(), () => 12);
        var firstA = scope.Capture()!;
        scope.Invalidate();
        var b = scope.Capture()!;
        scope.Invalidate();
        var newA = scope.Capture()!;
        Assert.Equal(0, firstA.ProfileDataRevision);
        Assert.Equal(1, b.ProfileDataRevision);
        Assert.Equal(2, newA.ProfileDataRevision);
        Assert.False(firstA.IsCurrent);
        Assert.False(b.IsCurrent);
        Assert.True(newA.IsCurrent);
    }

    [Fact]
    public void SameEpochRevisionFromAnotherClientIsNotTheOriginalSource()
    {
        var first = new GatewayDataReadScope(Client(), () => 12);
        var second = new GatewayDataReadScope(Client(), () => 12);
        var source = first.Capture()!;
        Assert.True(source.IsCurrent);
        Assert.False(second.IsCurrent(source));
        Assert.False(second.TryCommit(source, () => throw new InvalidOperationException("Must not commit")));
    }

    [Fact]
    public void EventFromRetiredConnectionCannotBeCapturedAsTheNewEpoch()
    {
        long? epoch = 12;
        var scope = new GatewayDataReadScope(Client(), () => epoch);
        var source = scope.Capture(expectedEpoch: 12)!;
        epoch = null;
        Assert.Null(scope.Capture());
        Assert.False(source.IsCurrent);
        epoch = 13;
        Assert.Null(scope.Capture(expectedEpoch: 12));
        Assert.Equal(13, scope.Capture(expectedEpoch: 13)!.ConnectionEpoch);
        Assert.False(source.IsCurrent);
    }

    [Fact]
    public async Task CompletingRegistryReadDoesNotRunALockingContinuationUnderTheGate()
    {
        var scope = new GatewayDataReadScope(Client(), () => 12);
        var source = scope.Capture()!;
        var registry = new PendingRequestRegistry();
        registry.OpenConnection();
        var read = registry.RegisterWizard("read", "chat.history");
        registry.TryTake("read", out var resolution);
        var continuation = read.Task.ContinueWith(_ =>
        {
            // If this continuation ran inline inside CompleteRead, the other
            // thread could not invalidate until this bounded wait failed.
            Task.Run(scope.Invalidate).WaitAsync(DeadlockDeadline).GetAwaiter().GetResult();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var completion = Task.Run(() => scope.CompleteRead(source,
            (WizardRequestResolution)resolution!, JsonSerializer.SerializeToElement(new { messages = Array.Empty<object>() })));
        Assert.True(await completion.WaitAsync(DeadlockDeadline));
        await continuation.WaitAsync(DeadlockDeadline);
        Assert.False(source.IsCurrent);
    }

    [Fact]
    public void OutOfOrderPublicationCarriesItsOriginalSourceInsteadOfRecapturing()
    {
        var client = Client();
        var scope = new GatewayDataReadScope(client, () => 12);
        var oldSource = scope.Capture()!;
        scope.Invalidate();
        var currentSource = scope.Capture()!;
        var observed = new List<string>();
        Assert.True(scope.TryCommit(currentSource, () => observed.Add("B")));
        Assert.False(scope.TryCommit(oldSource, () => observed.Add("late-A")));
        Assert.False(scope.TryCommit(null, () => observed.Add("unowned")));
        Assert.Equal(["B"], observed);
    }
}
