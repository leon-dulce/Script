using FlowKey.Core;

namespace FlowKey.Desktop;

internal sealed class KeyboardPlayback(Action<ushort, bool> send)
{
    private readonly List<ushort> _held = [];

    public void Execute(ScriptStep step)
    {
        if (step.KeyAction == KeyAction.Up)
        {
            Release((ushort)step.Keys[0]);
            return;
        }
        if (step.KeyAction == KeyAction.Down)
        {
            Press((ushort)step.Keys[0]);
            return;
        }

        // Legacy scripts store a complete key combination in one step. Preserve any
        // keys held by earlier Down steps while pressing and releasing this combination.
        var previous = _held.ToHashSet();
        try { foreach (var key in step.Keys) Press((ushort)key); }
        finally { ReleaseKeys(_held.Where(key => !previous.Contains(key)).Reverse().ToArray()); }
    }

    public void ReleaseAll() => ReleaseKeys(_held.AsEnumerable().Reverse().ToArray());

    private void Press(ushort key)
    {
        // Track before sending so cleanup also releases an input whose delivery failed.
        if (!_held.Contains(key)) _held.Add(key);
        send(key, false);
    }

    private void Release(ushort key)
    {
        if (!_held.Contains(key)) return;
        send(key, true);
        _held.Remove(key);
    }

    private void ReleaseKeys(IEnumerable<ushort> keys)
    {
        Exception? failure = null;
        foreach (var key in keys)
        {
            try { Release(key); }
            catch (Exception error) { failure ??= error; }
        }
        if (failure is not null) throw failure;
    }
}
