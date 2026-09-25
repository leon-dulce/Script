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
            // Windows emits another Down for each typematic repeat. Games can act on
            // every one of those events, so keep them while the key remains held.
            KeyAction.Down => AcceptDown(key),
            KeyAction.Up => _held.Remove(key),
            _ => false
        };
    }

    private bool AcceptDown(int key)
    {
        _held.Add(key);
        return true;
    }
}
