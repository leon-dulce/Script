namespace FlowKey.Core;

public static class ScriptValidator
{
    public static readonly string[] Hotkeys = ["F8", "F9", "F10", "F11"];

    public static void Validate(ScriptDocument? script)
    {
        if (script is null) throw new InvalidDataException("腳本為空。");
        if (!Guid.TryParseExact(script.Id, "N", out _)) throw new InvalidDataException("腳本編號無效。");
        if (script.Version != 1) throw new InvalidDataException("不支援此腳本版本。");
        if (string.IsNullOrWhiteSpace(script.Name) || script.Name.Length > 100)
            throw new InvalidDataException("腳本名稱無效。");
        if (script.TargetProcessPath is null || script.TargetTitle is null ||
            script.TargetProcessPath.Length > 1024 || script.TargetTitle.Length > 512)
            throw new InvalidDataException("目標視窗資訊過長。");
        if (!Hotkeys.Contains(script.Hotkey)) throw new InvalidDataException("快捷鍵無效。");
        if (script.Execution is null || !Enum.IsDefined(script.Execution.Mode) ||
            script.Execution.RepeatCount is < 1 or > 10000 || script.Execution.IntervalMs is < 100 or > 60000)
            throw new InvalidDataException("執行模式、次數或輪次間隔無效。");
        if (script.ClientWidth is < 0 or > 16384 || script.ClientHeight is < 0 or > 16384)
            throw new InvalidDataException("視窗尺寸無效。");
        if (script.Steps is null || script.Steps.Count > 10000)
            throw new InvalidDataException("步驟數量無效。");
        foreach (var step in script.Steps)
        {
            if (step is null || !Enum.IsDefined(step.Type) || !Enum.IsDefined(step.KeyAction) || step.DelayMs < 0)
                throw new InvalidDataException("步驟型別或等待時間無效。");
            if (script.GlobalKeyboardRecording && step.Type is StepType.Click or StepType.DoubleClick or StepType.Scroll)
                throw new InvalidDataException("全域鍵盤腳本不能包含滑鼠座標步驟。");
            switch (step.Type)
            {
                case StepType.Click or StepType.DoubleClick:
                    if (script.ClientWidth == 0 || script.ClientHeight == 0 ||
                        step.X < 0 || step.X >= script.ClientWidth || step.Y < 0 || step.Y >= script.ClientHeight ||
                        step.Button is not ("Left" or "Right" or "Middle"))
                        throw new InvalidDataException("點選位置或按鈕無效。");
                    break;
                case StepType.Scroll:
                    if (step.WheelDelta is < -12000 or > 12000 || step.WheelDelta == 0 ||
                        script.ClientWidth == 0 || script.ClientHeight == 0 ||
                        step.X < 0 || step.X >= script.ClientWidth || step.Y < 0 || step.Y >= script.ClientHeight)
                        throw new InvalidDataException("滾動量無效。");
                    break;
                case StepType.Key:
                    if (step.Keys is null || step.Keys.Count is < 1 or > 5 ||
                        (step.KeyAction != KeyAction.Press && step.Keys.Count != 1) ||
                        step.Keys.Any(key => key is < 1 or > 254) ||
                        step.Keys.Contains(119 + Array.IndexOf(Hotkeys, script.Hotkey)))
                        throw new InvalidDataException("按鍵組合無效。");
                    break;
                case StepType.Text:
                    if (string.IsNullOrEmpty(step.Text) || step.Text.Length > 10000)
                        throw new InvalidDataException("文字步驟無效。");
                    break;
            }
        }
    }
}
