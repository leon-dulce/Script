using FlowKey.Core;

namespace FlowKey.Desktop;

internal static class KeyboardStepFactory
{
    internal static ScriptStep? Create(uint key, uint flags, bool ctrl, bool alt, bool shift, bool win)
    {
        if ((flags & Native.InjectedKeyboard) != 0 || key is < 1 or > 254 ||
            key is >= 0x77 and <= 0x7A || key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C)
            return null;
        var keys = new List<int>();
        if (ctrl) keys.Add(0x11);
        if (alt) keys.Add(0x12);
        if (shift) keys.Add(0x10);
        if (win) keys.Add(0x5B);
        keys.Add((int)key);
        return new ScriptStep { Type = StepType.Key, Keys = keys };
    }
}
