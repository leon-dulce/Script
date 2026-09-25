using System.Diagnostics;

namespace FlowKey.Core;

public sealed class StepRecorder
{
    private long _previousTick;

    public void Reset() => _previousTick = 0;

    public void Add(List<ScriptStep> steps, ScriptStep step, long tick)
    {
        step.DelayMs = _previousTick == 0 ? 0 : (int)Math.Clamp(Stopwatch.GetElapsedTime(_previousTick, tick).TotalMilliseconds, 0, 60000);
        _previousTick = tick;
        if (step.Type == StepType.Click && steps.LastOrDefault() is { Type: StepType.Click } previous &&
            previous.X == step.X && previous.Y == step.Y && previous.Button == step.Button && step.DelayMs <= 500)
        {
            previous.Type = StepType.DoubleClick;
            return;
        }
        steps.Add(step);
    }
}
