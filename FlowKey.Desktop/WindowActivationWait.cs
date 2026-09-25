namespace FlowKey.Desktop;

internal enum ActivationStatus { Waiting, Ready, Missing, TimedOut }

internal sealed class WindowActivationWait(long started)
{
    private long? _foregroundSince;
    internal ActivationStatus Observe(long now, bool exists, bool foreground)
    {
        if (!exists) return ActivationStatus.Missing;
        if (now - started >= 5000) return ActivationStatus.TimedOut;
        if (!foreground) { _foregroundSince = null; return ActivationStatus.Waiting; }
        _foregroundSince ??= now;
        return now - _foregroundSince.Value >= 200 ? ActivationStatus.Ready : ActivationStatus.Waiting;
    }
}
