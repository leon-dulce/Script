using System.Text.Json.Serialization;

namespace FlowKey.Core;

public sealed class ScriptDocument
{
    [JsonRequired] public int Version { get; set; } = 1;
    [JsonRequired] public string Name { get; set; } = "我的脚本";
    [JsonRequired] public string TargetProcessPath { get; set; } = "";
    [JsonRequired] public string TargetTitle { get; set; } = "";
    [JsonRequired] public string Hotkey { get; set; } = "F10";
    [JsonRequired] public int ClientWidth { get; set; }
    [JsonRequired] public int ClientHeight { get; set; }
    [JsonRequired] public List<ScriptStep> Steps { get; set; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<StepType>))]
public enum StepType { Click, DoubleClick, Scroll, Key, Text }

public sealed class ScriptStep
{
    [JsonRequired] public StepType Type { get; set; }
    [JsonRequired] public int DelayMs { get; set; }
    [JsonRequired] public int X { get; set; }
    [JsonRequired] public int Y { get; set; }
    [JsonRequired] public string Button { get; set; } = "Left";
    [JsonRequired] public int WheelDelta { get; set; }
    [JsonRequired] public List<int> Keys { get; set; } = [];
    [JsonRequired] public string Text { get; set; } = "";
}
