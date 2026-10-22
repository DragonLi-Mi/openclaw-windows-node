using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// Original local transport lifetime of an operator read or ordered inbound event.
/// This is not a canonical account, authorization grant, or selected UI context.
/// Producers must carry this same source to their response and publication.
/// </summary>
public sealed class GatewayDataReadSource
{
    private readonly GatewayDataReadScope _issuer;

    internal GatewayDataReadSource(GatewayDataReadScope issuer, IOperatorGatewayClient client,
        long connectionEpoch, long profileDataRevision)
    {
        _issuer = issuer;
        Client = client;
        ConnectionEpoch = connectionEpoch;
        ProfileDataRevision = profileDataRevision;
    }

    public IOperatorGatewayClient Client { get; }
    public long ConnectionEpoch { get; }
    public long ProfileDataRevision { get; }
    public bool IsCurrent => _issuer.IsCurrent(this);

    internal bool WasIssuedBy(GatewayDataReadScope issuer) => ReferenceEquals(_issuer, issuer);
}

/// <summary>
/// A read lost its original transport/account revision. This is cancellation,
/// not an empty response and not an invitation to retry under another owner.
/// </summary>
public sealed class GatewayDataReadRetiredException : OperationCanceledException
{
    public GatewayDataReadRetiredException()
        : base("The Gateway data source changed before the read could complete.") { }
}

/// <summary>
/// One operator client's local read revision. Account lookup remains elsewhere.
/// Revision advancement and internal response/state commits use the same gate.
/// Never invoke external subscribers, network code or a send lock inside a commit.
/// </summary>
internal sealed class GatewayDataReadScope(
    IOperatorGatewayClient client,
    Func<long?> readyEpoch)
{
    private readonly object _gate = new();
    private long _profileDataRevision;

    internal GatewayDataReadSource? Capture(long? expectedEpoch = null)
    {
        lock (_gate)
        {
            var epoch = readyEpoch();
            return epoch.HasValue && (!expectedEpoch.HasValue || expectedEpoch == epoch)
                ? new(this, client, epoch.Value, _profileDataRevision) : null;
        }
    }

    internal void Invalidate()
    {
        lock (_gate) _profileDataRevision++;
    }

    internal bool IsCurrent(GatewayDataReadSource? source)
    {
        lock (_gate) return IsCurrentLocked(source);
    }

    /// <summary>
    /// Runs only a bounded internal mutation. Publish its immutable snapshot and original
    /// source outside this gate; consumers recheck that source, never a current getter.
    /// </summary>
    internal bool TryCommit(GatewayDataReadSource? source, Action internalMutation)
    {
        lock (_gate)
        {
            if (!IsCurrentLocked(source)) return false;
            internalMutation();
            return true;
        }
    }

    internal bool CompleteRead(GatewayDataReadSource source,
        WizardRequestResolution completion, JsonElement result)
    {
        lock (_gate)
        {
            if (!IsCurrentLocked(source))
            {
                completion.TryFault(new GatewayDataReadRetiredException());
                return false;
            }
            // Registry completions require RunContinuationsAsynchronously.
            return completion.TryComplete(result);
        }
    }

    private bool IsCurrentLocked(GatewayDataReadSource? source) =>
        source is not null && source.WasIssuedBy(this) && ReferenceEquals(source.Client, client) &&
        source.ProfileDataRevision == _profileDataRevision &&
        readyEpoch() == source.ConnectionEpoch;
}
