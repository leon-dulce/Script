namespace FlowKey.Core;

public static class ScriptValidator
{
    public static readonly string[] Hotkeys = ["F8", "F9", "F10", "F11"];

    public static void Validate(ScriptDocument? script)
    {
        if (script is null) throw new InvalidDataException("脚本为空。");
        if (!Guid.TryParseExact(script.Id, "N", out _)) throw new InvalidDataException("脚本编号无效。");
        if (script.Version != 1) throw new InvalidDataException("不支持此脚本版本。");
        if (string.IsNullOrWhiteSpace(script.Name) || script.Name.Length > 100)
            throw new InvalidDataException("脚本名称无效。");
        if (script.TargetProcessPath is null || script.TargetTitle is null ||
            script.TargetProcessPath.Length > 1024 || script.TargetTitle.Length > 512)
            throw new InvalidDataException("目标窗口信息过长。");
        if (!Hotkeys.Contains(script.Hotkey)) throw new InvalidDataException("快捷键无效。");
        if (script.Execution is null || !Enum.IsDefined(script.Execution.Mode) ||
            script.Execution.RepeatCount is < 1 or > 10000 || script.Execution.IntervalMs is < 100 or > 60000)
            throw new InvalidDataException("执行模式、次数或轮次间隔无效。");
        if (script.ClientWidth is < 0 or > 16384 || script.ClientHeight is < 0 or > 16384)
            throw new InvalidDataException("窗口尺寸无效。");
        if (script.Steps is null || script.Steps.Count > 10000)
            throw new InvalidDataException("步骤数量无效。");
        foreach (var step in script.Steps)
        {
            if (step is null || !Enum.IsDefined(step.Type) || !Enum.IsDefined(step.KeyAction) || step.DelayMs < 0)
                throw new InvalidDataException("步骤类型或等待时间无效。");
            if (script.GlobalKeyboardRecording && step.Type is StepType.Click or StepType.DoubleClick or StepType.Scroll)
                throw new InvalidDataException("全域键盘脚本不能包含鼠标坐标步骤。");
            switch (step.Type)
            {
                case StepType.Click or StepType.DoubleClick:
                    if (script.ClientWidth == 0 || script.ClientHeight == 0 ||
                        step.X < 0 || step.X >= script.ClientWidth || step.Y < 0 || step.Y >= script.ClientHeight ||
                        step.Button is not ("Left" or "Right" or "Middle"))
                        throw new InvalidDataException("点击位置或按钮无效。");
                    break;
                case StepType.Scroll:
                    if (step.WheelDelta is < -12000 or > 12000 || step.WheelDelta == 0 ||
                        script.ClientWidth == 0 || script.ClientHeight == 0 ||
                        step.X < 0 || step.X >= script.ClientWidth || step.Y < 0 || step.Y >= script.ClientHeight)
                        throw new InvalidDataException("滚动量无效。");
                    break;
                case StepType.Key:
                    if (step.Keys is null || step.Keys.Count is < 1 or > 5 ||
                        (step.KeyAction != KeyAction.Press && step.Keys.Count != 1) ||
                        step.Keys.Any(key => key is < 1 or > 254) ||
                        step.Keys.Contains(119 + Array.IndexOf(Hotkeys, script.Hotkey)))
                        throw new InvalidDataException("按键组合无效。");
                    break;
                case StepType.Text:
                    if (string.IsNullOrEmpty(step.Text) || step.Text.Length > 10000)
                        throw new InvalidDataException("文字步骤无效。");
                    break;
            }
        }
    }
}
