namespace FlowKey.Core;

public static class PlaybackLoop
{
    public static async Task RunAsync(
        ExecutionPlan plan,
        IReadOnlyList<ScriptStep> steps,
        Func<long, int, ScriptStep, CancellationToken, Task> executeStep,
        Func<int, CancellationToken, Task> waitBetweenRuns,
        CancellationToken cancellation)
    {
        for (long iteration = 1; plan.ShouldContinue(iteration - 1); iteration++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (iteration > 1) await waitBetweenRuns(plan.IntervalMs, cancellation);
            for (var index = 0; index < steps.Count; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                await executeStep(iteration, index, steps[index], cancellation);
            }
        }
    }
}
