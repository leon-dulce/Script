using FlowKey.Core;

var checks = new (string Name, Action Run)[]
{
    ("round trip saves all step types", RoundTrip),
    ("rejects unsafe script values", RejectsInvalid),
    ("corrupt file reports an error", CorruptFile),
    ("invalid save preserves old file", InvalidSavePreservesFile),
    ("recording coalesces double click and keeps delays", RecordingSteps),
    ("session rejects overlapping runs and invalid transitions", SessionTransitions)
};
foreach (var check in checks)
{
    check.Run();
    Console.WriteLine($"PASS {check.Name}");
}

static ScriptDocument ValidScript() => new()
{
    Name = "测试", TargetProcessPath = @"C:\Windows\notepad.exe", TargetTitle = "记事本",
    ClientWidth = 800, ClientHeight = 600,
    Steps =
    [
        new() { Type = StepType.Click, X = 4, Y = 8, DelayMs = 200 },
        new() { Type = StepType.Scroll, WheelDelta = -120, DelayMs = 50 },
        new() { Type = StepType.Key, Keys = [17, 83] },
        new() { Type = StepType.Text, Text = "中文" }
    ]
};

static void RoundTrip()
{
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
    try
    {
        var store = new ScriptStore(path);
        store.Save(ValidScript());
        var loaded = store.Load() ?? throw new Exception("Missing script");
        Check(loaded.Steps.Count == 4 && loaded.Steps[3].Text == "中文" && loaded.Hotkey == "F10");
        Check(!File.Exists(path + ".tmp"));
    }
    finally { File.Delete(path); }
}

static void RejectsInvalid()
{
    var script = ValidScript();
    script.Hotkey = "F12";
    Throws(() => ScriptValidator.Validate(script));
    script.Hotkey = "F10";
    script.Steps[0].X = 800;
    Throws(() => ScriptValidator.Validate(script));
    script.Steps[0].X = 4;
    script.Steps[1].Y = 600;
    Throws(() => ScriptValidator.Validate(script));
    script.Steps[1].Y = 0;
    script.Steps[2].Keys = [121];
    Throws(() => ScriptValidator.Validate(script));
    script.Steps[2].Keys = [17, 83];
    script.Steps[3].Text = "";
    Throws(() => ScriptValidator.Validate(script));
    script.Steps[3].Text = "ok";
    script.TargetTitle = null!;
    Throws(() => ScriptValidator.Validate(script));
}

static void CorruptFile()
{
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
    try
    {
        File.WriteAllText(path, "{oops");
        Throws(() => new ScriptStore(path).Load());
        File.WriteAllText(path, "{}");
        Throws(() => new ScriptStore(path).Load());
    }
    finally { File.Delete(path); }
}

static void InvalidSavePreservesFile()
{
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
    try
    {
        var store = new ScriptStore(path);
        store.Save(ValidScript());
        var original = File.ReadAllText(path);
        var invalid = ValidScript();
        invalid.Version = 99;
        Throws(() => store.Save(invalid));
        Check(File.ReadAllText(path) == original);
    }
    finally { File.Delete(path); }
}

static void RecordingSteps()
{
    var recorder = new StepRecorder();
    var steps = new List<ScriptStep>();
    var start = System.Diagnostics.Stopwatch.GetTimestamp();
    recorder.Add(steps, new ScriptStep { Type = StepType.Click, X = 2, Y = 3 }, start);
    recorder.Add(steps, new ScriptStep { Type = StepType.Click, X = 2, Y = 3 }, start + System.Diagnostics.Stopwatch.Frequency / 4);
    Check(steps.Count == 1 && steps[0].Type == StepType.DoubleClick && steps[0].DelayMs == 0);
    recorder.Add(steps, new ScriptStep { Type = StepType.Key, Keys = [65] }, start + System.Diagnostics.Stopwatch.Frequency / 2);
    Check(steps.Count == 2 && steps[1].DelayMs >= 240 && steps[1].DelayMs <= 260);
    recorder.Reset();
    recorder.Add(steps, new ScriptStep { Type = StepType.Scroll, WheelDelta = 120 }, start + System.Diagnostics.Stopwatch.Frequency);
    Check(steps[2].DelayMs == 0);
}

static void SessionTransitions()
{
    var session = new SessionState();
    Check(session.Mode == "ready" && session.BeginRun());
    Check(!session.BeginRun() && !session.BeginRecording());
    Check(session.StopRun() && !session.StopRun());
    Check(session.BeginRecording() && session.PauseRecording());
    Check(!session.BeginRun() && session.BeginRecording());
    Check(session.FinishRecording() && session.Mode == "ready");
}

static void Check(bool condition)
{
    if (!condition) throw new Exception("Assertion failed");
}

static void Throws(Action action)
{
    try { action(); }
    catch (InvalidDataException) { return; }
    throw new Exception("Expected InvalidDataException");
}
