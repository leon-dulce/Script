namespace FlowKey.Desktop;

internal sealed class RecordingPublishThrottle
{
    private long _lastTick;
    private bool _pending;

    internal void Reset() { _lastTick = 0; _pending = false; }
    internal void MarkChanged() => _pending = true;

    internal bool ShouldPublish(long tick)
    {
        if (!_pending || tick - _lastTick < 500) return false;
        _pending = false;
        _lastTick = tick;
        return true;
    }
}
