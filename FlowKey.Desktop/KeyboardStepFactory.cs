using FlowKey.Core;

namespace FlowKey.Desktop;

internal static class KeyboardStepFactory
{
    internal static ScriptStep? Create(uint key, uint flags, bool keyUp, int controlHotkey)
    {
        if ((flags & Native.InjectedKeyboard) != 0 || key is < 1 or > 254 ||
            key == controlHotkey)
            return null;
        return new ScriptStep
        {
            Type = StepType.Key,
            Keys = [(int)key],
            KeyAction = keyUp ? KeyAction.Up : KeyAction.Down
        };
    }
}
