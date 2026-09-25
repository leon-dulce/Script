using FlowKey.Core;

var checks = new (string Name, Action Run)[]
{
    ("round trip saves all step types", RoundTrip),
    ("global keyboard scope persists and rejects coordinate steps", GlobalRecordingScope),
    ("rejects unsafe script values", RejectsInvalid),
    ("corrupt file reports an error", CorruptFile),
    ("invalid save preserves old file", InvalidSavePreservesFile),
    ("recording coalesces double click and keeps delays", RecordingSteps),
    ("recording preserves first, inter-key and long delays", RecordingEventDelays),
    ("key actions persist and legacy keys remain presses", KeyActionPersistence),
    ("key actions validate active shortcut and event shape", KeyActionValidation),
    ("session rejects overlapping runs and invalid transitions", SessionTransitions),
    ("catalog migrates, lists, updates and deletes scripts", CatalogLifecycle),
    ("execution settings reject invalid ranges", RejectsExecutionSettings)
};
foreach (var check in checks)
{
    check.Run();
    Console.WriteLine($"PASS {check.Name}");
}
await PlaybackModes();
Console.WriteLine("PASS once, counted and continuous playback with cancellation");

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

static void GlobalRecordingScope()
{
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
    try
    {
        var store = new ScriptStore(path);
        var script = new ScriptDocument { GlobalKeyboardRecording = true, Steps = [new() { Type = StepType.Key, Keys = [65], KeyAction = KeyAction.Down }] };
        store.Save(script);
        Check(store.Load()!.GlobalKeyboardRecording && store.Load()!.ClientWidth == 0);
        var json = File.ReadAllText(path);
        var legacy = System.Text.RegularExpressions.Regex.Replace(json, "\"globalKeyboardRecording\"\\s*:\\s*true,?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        File.WriteAllText(path, legacy);
        Check(!store.Load()!.GlobalKeyboardRecording);
        script.ClientWidth = script.ClientHeight = 100;
        foreach (var type in new[] { StepType.Click, StepType.DoubleClick, StepType.Scroll })
        {
            script.Steps = [new() { Type = type, WheelDelta = 120 }];
            Throws(() => ScriptValidator.Validate(script));
        }
    }
    finally { File.Delete(path); }
}

static void RoundTrip()
{
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
    try
    {
        var store = new ScriptStore(path);
        store.Save(ValidScript());
        var loaded = store.Load() ?? throw new Exception("Missing script");
        Check(loaded.Steps.Count == 4 && loaded.Steps[3].Text == "中文" && loaded.Hotkey == "F10");
        Check(loaded.Id.Length == 32 && loaded.Execution.Mode == ExecutionMode.Once);
        Check(!File.Exists(path + ".tmp"));
    }
    finally { File.Delete(path); }
}

static void RejectsExecutionSettings()
{
    var script = ValidScript();
    script.Execution.Mode = ExecutionMode.Count;
    script.Execution.RepeatCount = 0;
    Throws(() => ScriptValidator.Validate(script));
    script.Execution.RepeatCount = 3;
    script.Execution.IntervalMs = 99;
    Throws(() => ScriptValidator.Validate(script));
    script.Execution.IntervalMs = 60001;
    Throws(() => ScriptValidator.Validate(script));
    script.Execution.IntervalMs = 500;
    script.Execution.Mode = (ExecutionMode)99;
    Throws(() => ScriptValidator.Validate(script));
    script.Execution.Mode = ExecutionMode.Continuous;
    ScriptValidator.Validate(script);
}

static void CatalogLifecycle()
{
    var root = Path.Combine(Path.GetTempPath(), "FlowKey-catalog-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(root);
        var catalog = new ScriptCatalog(Path.Combine(root, "scripts"));
        var legacy = Path.Combine(root, "script.json");
        var original = ValidScript();
        new ScriptStore(legacy).Save(original);
        Check(catalog.MigrateLegacy(legacy)?.Id == original.Id);
        Check(File.Exists(legacy) && catalog.List().Scripts.Count == 1);
        var second = ValidScript();
        second.Name = "第二个脚本";
        second.Execution.Mode = ExecutionMode.Count;
        second.Execution.RepeatCount = 3;
        catalog.Save(second);
        Check(catalog.List().Scripts.Count == 2);
        Check(catalog.Load(second.Id)?.Execution.RepeatCount == 3);
        second.Name = "更新名称";
        catalog.Save(second);
        Check(catalog.Load(second.Id)?.Name == "更新名称");
        Check(catalog.MigrateLegacy(legacy) is null);
        Throws(() => catalog.Load("../script.json"));
        File.WriteAllText(Path.Combine(catalog.DirectoryPath, Guid.NewGuid().ToString("N") + ".json"), "{oops");
        Check(catalog.List().Scripts.Count == 2 && catalog.List().Errors.Count == 1);
        Check(catalog.Delete(second.Id) && !catalog.Delete(second.Id));
        Check(catalog.List().Scripts.Count == 1);
    }
    finally
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unexpected catalog test directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}

static async Task PlaybackModes()
{
    var steps = new[] { new ScriptStep { Type = StepType.Key, Keys = [65] }, new ScriptStep { Type = StepType.Key, Keys = [66] } };
    var calls = new List<(long Iteration, int Index)>();
    var waits = new List<int>();
    Task Execute(long iteration, int index, ScriptStep _, CancellationToken token)
    { calls.Add((iteration, index)); return Task.CompletedTask; }
    Task Wait(int duration, CancellationToken token)
    { waits.Add(duration); return Task.CompletedTask; }

    await PlaybackLoop.RunAsync(new ExecutionPlan(), steps, Execute, Wait, CancellationToken.None);
    Check(calls.SequenceEqual([(1, 0), (1, 1)]) && waits.Count == 0);
    calls.Clear();
    var counted = new ExecutionPlan { Mode = ExecutionMode.Count, RepeatCount = 3, IntervalMs = 750 };
    await PlaybackLoop.RunAsync(counted, steps, Execute, Wait, CancellationToken.None);
    Check(calls.Count == 6 && calls[^1] == (3, 1) && waits.SequenceEqual([750, 750]));

    calls.Clear(); waits.Clear();
    using var stop = new CancellationTokenSource();
    Task StopAfterThird(long iteration, int index, ScriptStep _, CancellationToken token)
    {
        calls.Add((iteration, index));
        if (iteration == 3 && index == 1) stop.Cancel();
        return Task.CompletedTask;
    }
    try
    {
        await PlaybackLoop.RunAsync(new ExecutionPlan { Mode = ExecutionMode.Continuous }, steps, StopAfterThird, Wait, stop.Token);
        throw new Exception("Continuous run did not stop.");
    }
    catch (OperationCanceledException) { }
    Check(calls.Count == 6 && waits.Count == 2);
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

static void RecordingEventDelays()
{
    var recorder = new StepRecorder();
    var steps = new List<ScriptStep>();
    var start = System.Diagnostics.Stopwatch.GetTimestamp();
    var frequency = System.Diagnostics.Stopwatch.Frequency;
    recorder.Reset(start);
    recorder.Add(steps, new ScriptStep { Type = StepType.Key, Keys = [65], KeyAction = KeyAction.Down }, start + frequency / 4);
    recorder.Add(steps, new ScriptStep { Type = StepType.Key, Keys = [65], KeyAction = KeyAction.Down }, start + frequency / 2);
    recorder.Add(steps, new ScriptStep { Type = StepType.Key, Keys = [65], KeyAction = KeyAction.Up }, start + frequency);
    Check(steps.Count == 3 && steps.Select(step => step.KeyAction).SequenceEqual([KeyAction.Down, KeyAction.Down, KeyAction.Up]));
    Check(steps[0].DelayMs is >= 249 and <= 250 && steps[1].DelayMs is >= 249 and <= 250 && steps[2].DelayMs is >= 499 and <= 500);
    recorder.Add(steps, new ScriptStep { Type = StepType.Key, Keys = [66], KeyAction = KeyAction.Down }, start + frequency * 62);
    Check(steps[^1].DelayMs == 61000);
    recorder.Reset(start);
    recorder.Add(steps, new ScriptStep { Type = StepType.Key, Keys = [66], KeyAction = KeyAction.Up }, start - frequency);
    Check(steps[^1].DelayMs == 0);
    recorder.Reset(start);
    recorder.Add(steps, new ScriptStep { Type = StepType.Key, Keys = [67] }, start + frequency * 2147484L);
    Check(steps[^1].DelayMs == int.MaxValue);
}

static void KeyActionPersistence()
{
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
    try
    {
        var script = ValidScript();
        script.Steps =
        [
            new() { Type = StepType.Key, Keys = [17, 83] },
            new() { Type = StepType.Key, Keys = [0xA3], KeyAction = KeyAction.Down, DelayMs = 61000 },
            new() { Type = StepType.Key, Keys = [0xA3], KeyAction = KeyAction.Up, DelayMs = 145 }
        ];
        var store = new ScriptStore(path);
        store.Save(script);
        var loaded = store.Load() ?? throw new Exception("Missing key action script");
        Check(loaded.Steps.Select(step => step.KeyAction).SequenceEqual([KeyAction.Press, KeyAction.Down, KeyAction.Up]));
        Check(loaded.Steps[1].Keys.SequenceEqual([0xA3]) && loaded.Steps[1].DelayMs == 61000 && loaded.Steps[2].DelayMs == 145);
        var legacyJson = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var step in legacyJson["Steps"]!.AsArray()) step!.AsObject().Remove("KeyAction");
        File.WriteAllText(path, legacyJson.ToJsonString());
        Check(store.Load()!.Steps.All(step => step.KeyAction == KeyAction.Press));
        legacyJson["Steps"]![0]!["KeyAction"] = "Unknown";
        File.WriteAllText(path, legacyJson.ToJsonString());
        Throws(() => store.Load());
    }
    finally { File.Delete(path); }
}

static void KeyActionValidation()
{
    var script = ValidScript();
    var step = new ScriptStep { Type = StepType.Key, Keys = [0x11, 0x12, 0x10, 0x5B, 0x53] };
    script.Steps = [step];
    ScriptValidator.Validate(script);
    step.Keys.Add(0x41);
    Throws(() => ScriptValidator.Validate(script));
    step.Keys = [0xA2, 0x41];
    step.KeyAction = KeyAction.Down;
    Throws(() => ScriptValidator.Validate(script));
    step.KeyAction = KeyAction.Up;
    Throws(() => ScriptValidator.Validate(script));
    step.Keys = [0xA2];
    ScriptValidator.Validate(script);
    step.KeyAction = (KeyAction)99;
    Throws(() => ScriptValidator.Validate(script));
    step.KeyAction = KeyAction.Down;
    foreach (var invalidKeys in new List<int>[] { [], [0], [255] })
    {
        step.Keys = invalidKeys;
        Throws(() => ScriptValidator.Validate(script));
    }
    foreach (var hotkey in ScriptValidator.Hotkeys)
    {
        script.Hotkey = hotkey;
        foreach (var key in new[] { 119, 120, 121, 122 })
        {
            step.Keys = [key];
            foreach (var action in Enum.GetValues<KeyAction>())
            {
                step.KeyAction = action;
                if (key == 119 + Array.IndexOf(ScriptValidator.Hotkeys, hotkey)) Throws(() => ScriptValidator.Validate(script));
                else ScriptValidator.Validate(script);
            }
        }
    }
    step.Keys = [65];
    step.DelayMs = int.MaxValue;
    ScriptValidator.Validate(script);
    step.DelayMs = -1;
    Throws(() => ScriptValidator.Validate(script));
}

static void SessionTransitions()
{
    var session = new SessionState();
    Check(session.ResolveShortcut(false, false, true) == ShortcutAction.StartRecording);
    Check(session.ResolveShortcut(true, false, true) == ShortcutAction.StartRecording);
    Check(session.ResolveShortcut(true, true, true) == ShortcutAction.StartRecording);
    Check(session.ResolveShortcut(false, true, true) == ShortcutAction.StartRecording);
    Check(session.ResolveShortcut(true, true, false) == ShortcutAction.StartRun);
    Check(session.ResolveShortcut(true, false, false) == ShortcutAction.SelectScript);
    Check(session.ResolveShortcut(false, false, false) == ShortcutAction.SelectScript);
    Check(session.ResolveShortcut(false, true, false) == ShortcutAction.SelectTarget);
    Check(session.Mode == "ready" && session.BeginRun());
    Check(session.ResolveShortcut(true, true, false) == ShortcutAction.StopRun);
    Check(session.ResolveShortcut(false, true, false) == ShortcutAction.StopRun);
    Check(!session.BeginRun() && !session.BeginRecording());
    Check(session.StopRun() && !session.StopRun());
    Check(session.BeginRecording() && session.PauseRecording());
    Check(session.ResolveShortcut(true, false, true) == ShortcutAction.FinishRecording);
    Check(session.ResolveShortcut(false, true, true) == ShortcutAction.FinishRecording);
    Check(!session.BeginRun() && session.BeginRecording());
    Check(session.ResolveShortcut(true, false, true) == ShortcutAction.FinishRecording);
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
