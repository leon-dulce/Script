using System.Text.Json.Serialization;

namespace FlowKey.Core;

public sealed class ScriptDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonRequired] public int Version { get; set; } = 1;
    [JsonRequired] public string Name { get; set; } = "我的脚本";
    [JsonRequired] public string TargetProcessPath { get; set; } = "";
    [JsonRequired] public string TargetTitle { get; set; } = "";
    [JsonRequired] public string Hotkey { get; set; } = "F10";
    [JsonRequired] public int ClientWidth { get; set; }
    [JsonRequired] public int ClientHeight { get; set; }
    public ExecutionPlan Execution { get; set; } = new();
    [JsonRequired] public List<ScriptStep> Steps { get; set; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<ExecutionMode>))]
public enum ExecutionMode { Once, Count, Continuous }

public sealed class ExecutionPlan
{
    public ExecutionMode Mode { get; set; } = ExecutionMode.Once;
    public int RepeatCount { get; set; } = 1;
    public int IntervalMs { get; set; } = 1000;

    public bool ShouldContinue(long completedRuns) => Mode switch
    {
        ExecutionMode.Once => completedRuns < 1,
        ExecutionMode.Count => completedRuns < RepeatCount,
        ExecutionMode.Continuous => true,
        _ => false
    };
}

[JsonConverter(typeof(JsonStringEnumConverter<StepType>))]
public enum StepType { Click, DoubleClick, Scroll, Key, Text }

[JsonConverter(typeof(JsonStringEnumConverter<KeyAction>))]
public enum KeyAction { Press, Down, Up }

public sealed class ScriptStep
{
    [JsonRequired] public StepType Type { get; set; }
    [JsonRequired] public int DelayMs { get; set; }
    [JsonRequired] public int X { get; set; }
    [JsonRequired] public int Y { get; set; }
    [JsonRequired] public string Button { get; set; } = "Left";
    [JsonRequired] public int WheelDelta { get; set; }
    [JsonRequired] public List<int> Keys { get; set; } = [];
    public KeyAction KeyAction { get; set; } = KeyAction.Press;
    [JsonRequired] public string Text { get; set; } = "";
}
