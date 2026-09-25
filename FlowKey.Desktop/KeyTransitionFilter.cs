using FlowKey.Core;

namespace FlowKey.Desktop;

// One instance per recording; called only by the capture thread.
internal sealed class KeyTransitionFilter
{
    private readonly HashSet<int> _held = [];

    internal bool Accept(ScriptStep step)
    {
        if (step.Type != StepType.Key || step.Keys.Count != 1) return false;
        var key = step.Keys[0];
        return step.KeyAction switch
        {
            KeyAction.Down => _held.Add(key),
            KeyAction.Up => _held.Remove(key),
            _ => false
        };
    }
}
