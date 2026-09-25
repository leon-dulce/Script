using System.Text.Json.Serialization;

namespace FlowKey.Core;

public sealed class ScriptDocument
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "我的脚本";
    public string TargetProcessPath { get; set; } = "";
    public string TargetTitle { get; set; } = "";
    public string Hotkey { get; set; } = "F10";
    public int ClientWidth { get; set; }
    public int ClientHeight { get; set; }
    public List<ScriptStep> Steps { get; set; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<StepType>))]
public enum StepType { Click, DoubleClick, Scroll, Key, Text }

public sealed class ScriptStep
{
    public StepType Type { get; set; }
    public int DelayMs { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public string Button { get; set; } = "Left";
    public int WheelDelta { get; set; }
    public List<int> Keys { get; set; } = [];
    public string Text { get; set; } = "";
}
