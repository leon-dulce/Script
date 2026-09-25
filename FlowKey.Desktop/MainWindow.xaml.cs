using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FlowKey.Core;
using Microsoft.Web.WebView2.Core;

namespace FlowKey.Desktop;

public partial class MainWindow : Window
{
    private readonly string _dataDirectory;
    private readonly ScriptCatalog _catalog;
    private readonly DispatcherTimer _focusTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Native.HookCallback _keyboardCallback;
    private readonly Native.HookCallback _mouseCallback;
    private readonly List<WindowInfo> _windows = [];
    private readonly StepRecorder _recorder = new();
    private readonly SessionState _session = new();
    private ScriptDocument _script = new();
    private IReadOnlyList<ScriptDocument> _savedScripts = [];
    private WindowInfo? _target;
    private nint _handle, _keyboardHook, _mouseHook;
    private int _hotkeyCode = 121;
    private string? _registeredHotkey;
    private string _mode => _session.Mode;
    private string _message = "请选择目标窗口。";
    private int _currentStep = -1;
    private long _currentIteration;
    private CancellationTokenSource? _playback;
    private bool _pageReady;
    private bool _pendingRecord, _pendingRun, _waitingForNextRun, _dirty;

    public MainWindow() : this(null) { }

    public MainWindow(string? dataDirectory)
    {
        _dataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowKey");
        _catalog = new ScriptCatalog(Path.Combine(_dataDirectory, "scripts"));
        InitializeComponent();
        _keyboardCallback = OnKeyboard;
        _mouseCallback = OnMouse;
        Loaded += OnLoaded;
        Closed += OnClosed;
        _focusTimer.Tick += (_, _) => CheckFocus();
        _focusTimer.Start();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_handle).AddHook(OnWindowMessage);
        try
        {
            var migrated = _catalog.MigrateLegacy(Path.Combine(_dataDirectory, "script.json"));
            RefreshCatalog();
            _script = migrated ?? _savedScripts.FirstOrDefault() ?? new ScriptDocument();
            if (!_message.StartsWith("有 ", StringComparison.Ordinal))
                _message = _savedScripts.Count > 0 ? "已载入脚本，请选择目标窗口。" : "请选择目标窗口开始录制。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { _message = $"载入脚本库失败：{error.Message}"; }
        RegisterShortcut(_script.Hotkey);
        RefreshWindows();
        try
        {
            var assetDirectory = WebAssets.ExtractTo(Path.Combine(_dataDirectory, "assets"));
            var webViewEnvironment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(_dataDirectory, "WebView2"));
            await Browser.EnsureCoreWebView2Async(webViewEnvironment);
            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping("flowkey.local", assetDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.IsWebMessageEnabled = true;
            Browser.CoreWebView2.WebMessageReceived += OnWebMessage;
            Browser.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var destination) || destination.Host != "flowkey.local") args.Cancel = true;
            };
            Browser.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess)
                {
                    MessageBox.Show($"无法载入内置界面：{args.WebErrorStatus}", "FlowKey");
                    Close();
                    return;
                }
                _pageReady = true;
                Publish();
            };
            Browser.Source = new Uri("https://flowkey.local/index.html");
        }
        catch (Exception error) { MessageBox.Show($"无法启动界面：{error.Message}\n请安装 WebView2 Runtime。", "FlowKey"); Close(); }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopHooks();
        _playback?.Cancel();
        if (_handle != 0) Native.UnregisterHotKey(_handle, 1);
        _focusTimer.Stop();
    }

    private nint OnWindowMessage(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == Native.HotkeyMessage && wParam == 1)
        {
            handled = true;
            if (_pendingRecord)
            {
                if (_target is { } waitingTarget && IsTargetReady(waitingTarget)) StartRecording(waitingTarget);
                else { _pendingRecord = false; _message = "已取消等待录制。"; Publish(); }
                return 0;
            }
            if (_pendingRun)
            {
                if (_target is { } waitingTarget && IsTargetReady(waitingTarget)) { _pendingRun = false; StartRun(); }
                else { _pendingRun = false; _message = "已取消等待执行。"; Publish(); }
                return 0;
            }
            switch (_session.ResolveShortcut(_target is not null, _script.Steps.Count > 0))
            {
                case ShortcutAction.SelectTarget:
                    _message = "请先从窗口列表选择并确认目标窗口。";
                    Publish();
                    break;
                case ShortcutAction.StartRecording:
                case ShortcutAction.ResumeRecording:
                    ToggleRecording();
                    Publish();
                    break;
                case ShortcutAction.FinishRecording: FinishRecording(); break;
                case ShortcutAction.StartRun: StartRun(); break;
                case ShortcutAction.StopRun: StopRun("已停止执行。"); break;
            }
        }
        return 0;
    }

    private bool RegisterShortcut(string hotkey)
    {
        var code = 0x70 + int.Parse(hotkey[1..]) - 1;
        if (_registeredHotkey is not null) Native.UnregisterHotKey(_handle, 1);
        if (Native.RegisterHotKey(_handle, 1, 0x4000, (uint)code))
        {
            _hotkeyCode = code;
            _registeredHotkey = hotkey;
            _script.Hotkey = hotkey;
            return true;
        }
        if (_registeredHotkey is not null && !Native.RegisterHotKey(_handle, 1, 0x4000, (uint)_hotkeyCode)) _registeredHotkey = null;
        if (_registeredHotkey is not null) _script.Hotkey = _registeredHotkey;
        _message = $"{hotkey} 已被系统或其他程序占用，请选择其他快捷键。";
        return false;
    }

    private void RefreshWindows()
    {
        _windows.Clear();
        _windows.AddRange(Native.ListWindows(_handle));
        if (_target is { } target && !Native.Matches(target))
        {
            if (_mode == "recording") PauseRecording("目标窗口已关闭，录制已暂停。");
            if (_mode == "running") StopRun("目标窗口已关闭，已停止执行。");
            _target = null;
            _pendingRecord = false;
            _pendingRun = false;
        }
        Publish();
    }

    private void Publish()
    {
        if (!_pageReady || Browser.CoreWebView2 is null) return;
        var state = new
        {
            mode = _mode, message = _message, currentStep = _currentStep,
            currentIteration = _currentIteration, waitingForNextRun = _waitingForNextRun,
            pendingRun = _pendingRun, dirty = _dirty,
            selectedId = _target?.Handle.ToString() ?? "",
            windows = _windows.Select(w => new { id = w.Handle.ToString(), title = w.Title, process = w.ProcessName }),
            savedScripts = _savedScripts.Select(s => new { id = s.Id, name = s.Name, stepCount = s.Steps.Count, mode = s.Execution.Mode.ToString() }),
            script = _script
        };
        Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(state, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || source.Host != "flowkey.local") return;
        try
        {
            using var request = JsonDocument.Parse(e.WebMessageAsJson);
            var root = request.RootElement;
            var action = root.GetProperty("action").GetString();
            switch (action)
            {
                case "refresh": RefreshWindows(); break;
                case "newScript": NewScript(); break;
                case "openScript": OpenScript(root.GetProperty("id").GetString()); break;
                case "deleteScript": DeleteScript(root.GetProperty("id").GetString()); break;
                case "select": SelectWindow(root.GetProperty("value").GetString()); break;
                case "hotkey":
                    if (_mode == "ready" && root.TryGetProperty("value", out var key) && ScriptValidator.Hotkeys.Contains(key.GetString()))
                        if (RegisterShortcut(key.GetString()!)) _dirty = true;
                    break;
                case "name": if (_mode == "ready") SetName(root); break;
                case "execution": if (_mode == "ready") SetExecution(root); break;
                case "record": ToggleRecording(); break;
                case "pause": PauseRecording("录制已暂停，返回目标窗口后可继续。"); break;
                case "finish": FinishRecording(); break;
                case "run": RequestRun(); break;
                case "save": SaveScript(); break;
                case "load": LoadScript(); break;
                case "delete": if (_mode == "ready") { _script.Steps.RemoveAt(root.GetProperty("index").GetInt32()); _dirty = true; } break;
                case "delay": if (_mode == "ready") SetDelay(root); break;
                case "text": if (_mode == "ready") SetText(root); break;
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or ArgumentOutOfRangeException or InvalidOperationException)
        { _message = "操作内容无效，请重试。"; }
        Publish();
    }

    private void SelectWindow(string? id)
    {
        if (_mode != "ready") return;
        _target = WindowSelection.Find(_windows, id, Native.Matches);
        _pendingRecord = false;
        _message = _target is null ? "所选窗口不可用，请刷新列表后重选。" : $"已确认目标窗口：{_target.Value.Title}。";
    }

    private void RefreshCatalog()
    {
        var result = _catalog.List();
        _savedScripts = result.Scripts;
        if (result.Errors.Count > 0) _message = $"有 {result.Errors.Count} 个脚本文件无法载入，请检查本地脚本目录。";
    }

    private void NewScript()
    {
        if (_mode != "ready") return;
        _script = new ScriptDocument { Name = "未命名脚本", Hotkey = _registeredHotkey ?? "F10" };
        _target = null;
        _pendingRecord = _pendingRun = false;
        _dirty = true;
        _message = "已建立新脚本，请选择目标窗口。";
    }

    private void OpenScript(string? id)
    {
        if (_mode != "ready" || id is null) return;
        try
        {
            var script = _catalog.Load(id);
            if (script is null) { _message = "找不到此脚本，请刷新脚本库。"; return; }
            _script = script;
            _target = null;
            _pendingRecord = _pendingRun = false;
            _dirty = false;
            if (RegisterShortcut(script.Hotkey)) _message = $"已载入「{script.Name}」，请重新选择目标窗口。";
            else _dirty = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { _message = $"载入失败：{error.Message}"; }
    }

    private void DeleteScript(string? id)
    {
        if (_mode != "ready" || id is null) return;
        try
        {
            if (!_catalog.Delete(id)) { _message = "脚本已不存在。"; return; }
            if (_script.Id == id) NewScript();
            RefreshCatalog();
            _message = "脚本已从本机删除。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { _message = $"删除失败：{error.Message}"; }
    }

    private void SetDelay(JsonElement root)
    {
        var value = root.GetProperty("value").GetInt32();
        if (value is < 0 or > 60000) throw new InvalidOperationException();
        _script.Steps[root.GetProperty("index").GetInt32()].DelayMs = value;
        _dirty = true;
    }

    private void SetName(JsonElement root)
    {
        var name = root.GetProperty("value").GetString()?.Trim() ?? "";
        if (name.Length is < 1 or > 100) throw new InvalidOperationException();
        _script.Name = name;
        _dirty = true;
    }

    private void SetExecution(JsonElement root)
    {
        var modeName = root.GetProperty("mode").GetString();
        if (!Enum.TryParse<ExecutionMode>(modeName, out var mode) || !Enum.IsDefined(mode))
            throw new InvalidOperationException();
        var count = root.GetProperty("count").GetInt32();
        var interval = root.GetProperty("intervalMs").GetInt32();
        if (count is < 1 or > 10000 || interval is < 100 or > 60000) throw new InvalidOperationException();
        _script.Execution = new ExecutionPlan { Mode = mode, RepeatCount = count, IntervalMs = interval };
        _dirty = true;
        _message = "执行设置已更新，请保存脚本。";
    }

    private void SetText(JsonElement root)
    {
        var text = root.GetProperty("value").GetString() ?? "";
        if (text.Length is < 1 or > 10000) throw new InvalidOperationException();
        var index = root.TryGetProperty("index", out var element) ? element.GetInt32() : -1;
        if (index < 0) _script.Steps.Add(new ScriptStep { Type = StepType.Text, Text = text, DelayMs = 500 });
        else if (_script.Steps[index].Type == StepType.Text) _script.Steps[index].Text = text;
        _dirty = true;
    }

    private void ToggleRecording()
    {
        if (_mode == "recording") { FinishRecording(); return; }
        if (_mode is not ("ready" or "paused")) return;
        if (_target is not { } target) { _message = "请先从窗口列表选择并确认目标窗口。"; return; }
        if (_pendingRecord) { _pendingRecord = false; _message = "已取消等待录制。"; return; }
        if (!Native.Matches(target) || !Native.SameSize(target))
        { _message = "目标窗口已关闭或尺寸变化，请重新选择或录制。"; return; }
        if (!IsTargetReady(target))
        {
            _pendingRecord = true;
            _message = "已准备录制，请切回目标窗口。";
            return;
        }
        StartRecording(target);
    }

    private void StartRecording(WindowInfo target)
    {
        _pendingRecord = false;
        if (_mode == "ready")
        {
            _script.Steps.Clear();
            _script.TargetTitle = target.Title;
            _script.TargetProcessPath = target.ProcessPath;
            _script.ClientWidth = target.Width;
            _script.ClientHeight = target.Height;
            _dirty = true;
        }
        _recorder.Reset();
        var module = Native.GetModuleHandle(null);
        _keyboardHook = Native.SetWindowsHookEx(Native.KeyboardHook, _keyboardCallback, module, 0);
        _mouseHook = Native.SetWindowsHookEx(Native.MouseHook, _mouseCallback, module, 0);
        if (_keyboardHook == 0 || _mouseHook == 0)
        {
            StopHooks(); _session.FinishRecording(); _message = "无法启用输入监听。"; return;
        }
        _session.BeginRecording();
        _message = "正在录制；敏感输入前请暂停。";
        Publish();
    }

    private void FinishRecording()
    {
        if (_mode is not ("recording" or "paused")) return;
        StopHooks(); _pendingRecord = false; _session.FinishRecording();
        _message = "录制结束，请检查并保存步骤。";
        Publish();
    }

    private void PauseRecording(string message)
    {
        if (_mode != "recording") return;
        StopHooks(); _session.PauseRecording(); _message = message; Publish();
    }

    private void StopHooks()
    {
        if (_keyboardHook != 0) { Native.UnhookWindowsHookEx(_keyboardHook); _keyboardHook = 0; }
        if (_mouseHook != 0) { Native.UnhookWindowsHookEx(_mouseHook); _mouseHook = 0; }
    }

    private nint OnKeyboard(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && _mode == "recording" && (wParam == Native.KeyDown || wParam == Native.SysKeyDown))
        {
            var data = Marshal.PtrToStructure<Native.KeyboardData>(lParam);
            var step = KeyboardStepFactory.Create(data.VkCode, data.Flags,
                IsDown(0x11), IsDown(0x12), IsDown(0x10), IsDown(0x5B) || IsDown(0x5C));
            if (step is not null)
            {
                var tick = Stopwatch.GetTimestamp();
                Dispatcher.BeginInvoke(() => AddRecordedStep(step, tick));
            }
        }
        return Native.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private static bool IsDown(int key) => (Native.GetAsyncKeyState(key) & 0x8000) != 0;

    private nint OnMouse(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && _mode == "recording" && wParam is Native.LeftDown or Native.RightDown or Native.MiddleDown or Native.Wheel)
        {
            var data = Marshal.PtrToStructure<Native.MouseData>(lParam);
            if ((data.Flags & Native.InjectedMouse) == 0)
            {
                var tick = Stopwatch.GetTimestamp();
                var point = data.Point;
                var button = wParam == Native.RightDown ? "Right" : wParam == Native.MiddleDown ? "Middle" : "Left";
                var wheel = unchecked((short)(data.MouseInfo >> 16));
                Dispatcher.BeginInvoke(() =>
                {
                    if (_target is not { } target) return;
                    Native.ScreenToClient(target.Handle, ref point);
                    AddRecordedStep(wParam == Native.Wheel
                        ? new ScriptStep { Type = StepType.Scroll, X = point.X, Y = point.Y, WheelDelta = wheel }
                        : new ScriptStep { Type = StepType.Click, X = point.X, Y = point.Y, Button = button }, tick);
                });
            }
        }
        return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void AddRecordedStep(ScriptStep step, long tick)
    {
        if (_mode != "recording" || _target is not { } target || !IsTargetReady(target)) return;
        if (step.Type is StepType.Click or StepType.Scroll &&
            (step.X < 0 || step.Y < 0 || step.X >= target.Width || step.Y >= target.Height)) return;
        _recorder.Add(_script.Steps, step, tick);
        _dirty = true;
        Publish();
    }

    private bool IsTargetReady(WindowInfo target) => Native.Matches(target) && Native.SameSize(target) && Native.GetForegroundWindow() == target.Handle;

    private void CheckFocus()
    {
        if (_target is not { } target) return;
        if (_pendingRecord && IsTargetReady(target)) StartRecording(target);
        if (_pendingRun)
        {
            if (!Native.Matches(target) || !Native.SameSize(target))
            { _pendingRun = false; _message = "目标窗口已关闭或尺寸变化，取消等待执行。"; Publish(); }
            else if (IsTargetReady(target)) { _pendingRun = false; StartRun(); }
        }
        if (_mode == "recording" && !IsTargetReady(target)) PauseRecording("目标窗口失焦、关闭或尺寸变化，录制已暂停。");
        if (_mode == "running" && !IsTargetReady(target)) StopRun("目标窗口失焦、关闭或尺寸变化，已停止执行。");
    }

    private void RequestRun()
    {
        if (_mode == "running") { StopRun("已停止执行。"); return; }
        if (_mode != "ready") return;
        if (_pendingRun) { _pendingRun = false; _message = "已取消等待执行。"; Publish(); return; }
        if (_target is not { } target) { _message = "请先选择目标窗口。"; Publish(); return; }
        if (_script.Steps.Count == 0) { _message = "脚本尚无步骤。"; Publish(); return; }
        var problem = RunPreflight(target);
        if (problem is not null) { _message = problem; Publish(); return; }
        _pendingRecord = false;
        if (Native.GetForegroundWindow() != target.Handle)
        { _pendingRun = true; _message = "已准备执行，请切回目标窗口；再次点击可取消。"; Publish(); return; }
        StartRun();
    }

    private string? RunPreflight(WindowInfo target)
    {
        if (!Native.Matches(target) || !Native.SameSize(target)) return "目标窗口已关闭或尺寸变化。";
        if (_script.ClientWidth != target.Width || _script.ClientHeight != target.Height ||
            (_script.TargetProcessPath.Length > 0 && !string.Equals(_script.TargetProcessPath, target.ProcessPath, StringComparison.OrdinalIgnoreCase)))
            return "目标程序或窗口尺寸与脚本不符。";
        try { ScriptValidator.Validate(_script); }
        catch (InvalidDataException error) { return error.Message; }
        return null;
    }

    private void StartRun()
    {
        if (_mode != "ready" || _playback is not null) return;
        if (_target is not { } target) { _message = "请先选择目标窗口。"; Publish(); return; }
        if (_script.Steps.Count == 0) { _message = "脚本尚无步骤；按快捷键可开始录制。"; Publish(); return; }
        var problem = RunPreflight(target);
        if (problem is not null) { _message = problem; Publish(); return; }
        if (Native.GetForegroundWindow() != target.Handle) { _message = "目标窗口必须位于前台。"; Publish(); return; }
        _playback = new CancellationTokenSource();
        _session.BeginRun(); _currentStep = -1; _currentIteration = 0; _waitingForNextRun = false;
        _message = "正在执行；再按快捷键可停止。";
        Publish();
        _ = RunStepsAsync(target, _playback);
    }

    private async Task RunStepsAsync(WindowInfo target, CancellationTokenSource run)
    {
        try
        {
            await PlaybackLoop.RunAsync(_script.Execution, _script.Steps,
                async (iteration, index, step, cancellation) =>
                {
                    await Task.Yield();
                    cancellation.ThrowIfCancellationRequested();
                    _currentIteration = iteration;
                    _currentStep = index;
                    _waitingForNextRun = false;
                    Publish();
                    await Task.Delay(step.DelayMs, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    if (!IsTargetReady(target))
                    { StopRun("目标窗口发生变化，已停止执行。"); cancellation.ThrowIfCancellationRequested(); }
                    await ExecuteStepAsync(target, step, cancellation);
                },
                async (interval, cancellation) =>
                {
                    _currentStep = -1;
                    _waitingForNextRun = true;
                    Publish();
                    await Task.Delay(interval, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    if (!IsTargetReady(target))
                    { StopRun("目标窗口发生变化，已停止执行。"); cancellation.ThrowIfCancellationRequested(); }
                }, run.Token);
            StopRun($"脚本执行完成，共 {_currentIteration} 轮。", run);
        }
        catch (OperationCanceledException) { /* StopRun already updated the UI. */ }
        catch (Exception error) { StopRun($"执行失败：{error.Message}", run); }
        finally { if (ReferenceEquals(_playback, run)) { _playback = null; run.Dispose(); } }
    }

    private async Task ExecuteStepAsync(WindowInfo target, ScriptStep step, CancellationToken cancellation)
    {
        if (step.Type is StepType.Click or StepType.DoubleClick or StepType.Scroll)
        {
            var point = new Native.Point { X = step.X, Y = step.Y };
            Native.ClientToScreen(target.Handle, ref point);
            if (!Native.SetCursorPos(point.X, point.Y)) throw new InvalidOperationException("无法移动鼠标到目标位置。");
        }
        if (step.Type is StepType.Click or StepType.DoubleClick)
        {
            var (down, up) = step.Button switch { "Right" => (0x0008u, 0x0010u), "Middle" => (0x0020u, 0x0040u), _ => (0x0002u, 0x0004u) };
            for (var i = 0; i < (step.Type == StepType.DoubleClick ? 2 : 1); i++)
            {
                try { Native.SendMouse(down); }
                finally { Native.SendMouse(up); }
            }
        }
        else if (step.Type == StepType.Scroll) Native.SendMouse(0x0800, unchecked((uint)step.WheelDelta));
        else if (step.Type == StepType.Key)
        {
            try { foreach (var key in step.Keys) Native.SendKey((ushort)key); }
            finally { foreach (var key in step.Keys.AsEnumerable().Reverse()) Native.SendKey((ushort)key, true); }
        }
        else if (step.Type == StepType.Text)
        {
            foreach (var character in step.Text)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!IsTargetReady(target)) throw new InvalidOperationException("目标窗口失焦或尺寸变化。");
                try { Native.SendKey(character, unicode: true); }
                finally { Native.SendKey(character, release: true, unicode: true); }
                await Task.Yield();
            }
        }
    }

    private void StopRun(string message, CancellationTokenSource? expected = null)
    {
        if (_mode != "running" || (expected is not null && !ReferenceEquals(_playback, expected))) return;
        _playback?.Cancel();
        _session.StopRun(); _currentStep = -1; _waitingForNextRun = false; _message = message;
        Publish();
    }

    private void SaveScript()
    {
        if (_mode != "ready") return;
        try
        {
            _catalog.Save(_script);
            RefreshCatalog();
            _dirty = false;
            _message = $"已保存「{_script.Name}」到脚本库。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { _message = $"保存失败：{error.Message}"; }
    }

    private void LoadScript()
    {
        if (_mode != "ready") return;
        OpenScript(_script.Id);
    }
}
