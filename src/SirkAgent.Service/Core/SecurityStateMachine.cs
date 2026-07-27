namespace SirkAgent.Service.Core;

internal enum SecurityState
{
    Boot,
    Operational,
    Degraded,
    PolicyExpired,
    TamperDetected,
    Quarantine,
    RecoveryPending,
    Recovering,
    RecoveryFailed,
    Stopping
}

internal sealed class SecurityStateMachine
{
    private readonly DateTimeOffset _startedAtUtc;
    private SecurityState _current = SecurityState.Boot;
    private DateTimeOffset _changedAtUtc;
    private string _reason = "AGENT_STARTING";

    public SecurityStateMachine(DateTimeOffset startedAtUtc)
    {
        _startedAtUtc = startedAtUtc;
        _changedAtUtc = startedAtUtc;
    }

    public SecurityStateSnapshot Evaluate(
        DateTimeOffset timestampUtc,
        bool policyHealthy,
        string policyHealthCode,
        bool quarantineActive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyHealthCode);

        var target = DetermineTargetState(policyHealthy, policyHealthCode, quarantineActive);
        Transition(target, timestampUtc, policyHealthCode);
        return Snapshot(timestampUtc);
    }

    public SecurityStateSnapshot Stop(DateTimeOffset timestampUtc)
    {
        Transition(SecurityState.Stopping, timestampUtc, "AGENT_STOPPING");
        return Snapshot(timestampUtc);
    }

    private static SecurityState DetermineTargetState(
        bool policyHealthy,
        string policyHealthCode,
        bool quarantineActive)
    {
        if (quarantineActive)
            return SecurityState.Quarantine;

        if (string.Equals(policyHealthCode, "POLICY_EXPIRED", StringComparison.Ordinal))
            return SecurityState.PolicyExpired;

        if (!policyHealthy)
            return SecurityState.TamperDetected;

        return SecurityState.Operational;
    }

    private void Transition(SecurityState target, DateTimeOffset timestampUtc, string reason)
    {
        if (_current == target && string.Equals(_reason, reason, StringComparison.Ordinal))
            return;

        _current = target;
        _changedAtUtc = timestampUtc;
        _reason = reason;
    }

    private SecurityStateSnapshot Snapshot(DateTimeOffset timestampUtc) => new(
        State: _current.ToString(),
        Reason: _reason,
        StartedAtUtc: _startedAtUtc,
        StateChangedAtUtc: _changedAtUtc,
        UpdatedAtUtc: timestampUtc,
        UptimeSeconds: Math.Max(0, (long)(timestampUtc - _startedAtUtc).TotalSeconds));
}

internal sealed record SecurityStateSnapshot(
    string State,
    string Reason,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset StateChangedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long UptimeSeconds);
