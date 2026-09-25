using System.Diagnostics;

namespace FlowKey.Desktop;

// Schedule against the recording's cumulative timestamps so work between
// repeated key events does not lengthen a long hold.
internal sealed class PlaybackTimeline
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private long _dueMs;

    internal TimeSpan NextDelay(int recordedDelayMs, double? elapsedMs = null)
    {
        _dueMs += recordedDelayMs;
        return TimeSpan.FromMilliseconds(Math.Max(0, _dueMs - (elapsedMs ?? _watch.Elapsed.TotalMilliseconds)));
    }

    internal void Reset()
    {
        _dueMs = 0;
        _watch.Restart();
    }
}
