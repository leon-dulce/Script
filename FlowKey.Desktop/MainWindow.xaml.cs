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
    private readonly ScriptStore _store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowKey", "script.json"));
    private readonly DispatcherTimer _focusTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Native.HookCallback _keyboardCallback;
    private readonly Native.HookCallback _mouseCallback;
    private readonly List<WindowInfo> _windows = [];
    private readonly StepRecorder _recorder = new();
    private readonly SessionState _session = new();
    private ScriptDocument _script = new();
    private WindowInfo? _target;
    private nint _handle, _keyboardHook, _mouseHook;
    private int _hotkeyCode = 121;
    private string? _registeredHotkey;
    private string _mode => _session.Mode;
    private string _message = "请选择目标窗口。";
    private int _currentStep = -1;
    private CancellationTokenSource? _playback;
    private bool _pageReady;
    private bool _pendingRecord;

    public MainWindow()
    {
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
            _script = _store.Load() ?? new ScriptDocument();
            _message = _script.Steps.Count > 0 ? "已载入本地脚本，请重新选择目标窗口。" : "请选择目标窗口开始录制。";
        }
        catch (InvalidDataException error) { _message = error.Message; }
        RegisterShortcut(_script.Hotkey);
        RefreshWindows();
        try
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping("flowkey.local", Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.DenyCors);
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.IsWebMessageEnabled = true;
            Browser.CoreWebView2.WebMessageReceived += OnWebMessage;
            Browser.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var destination) || destination.Host != "flowkey.local") args.Cancel = true;
            };
            Browser.NavigationCompleted += (_, _) => { _pageReady = true; Publish(); };
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
            if (_mode == "running") StopRun("已停止执行。");
            else if (_mode is "recording" or "paused") FinishRecording();
            else if (_pendingRecord) { _pendingRecord = false; _message = "已取消等待录制。"; Publish(); }
            else if (_mode == "ready") StartRun();
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
        }
        Publish();
    }

    private void Publish()
    {
        if (!_pageReady || Browser.CoreWebView2 is null) return;
        var state = new
        {
            mode = _mode, message = _message, currentStep = _currentStep,
            selectedId = _target?.Handle.ToString() ?? "",
            windows = _windows.Select(w => new { id = w.Handle.ToString(), title = w.Title, process = w.ProcessName }),
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
                case "select": SelectWindow(root.GetProperty("value").GetString()); break;
                case "hotkey":
                    if (_mode == "ready" && root.TryGetProperty("value", out var key) && ScriptValidator.Hotkeys.Contains(key.GetString())) RegisterShortcut(key.GetString()!);
                    break;
                case "name": if (_mode == "ready") SetName(root); break;
                case "record": ToggleRecording(); break;
                case "pause": PauseRecording("录制已暂停，返回目标窗口后可继续。"); break;
                case "finish": FinishRecording(); break;
                case "run": if (_mode == "running") StopRun("已停止执行。"); else StartRun(); break;
                case "save": SaveScript(); break;
                case "load": LoadScript(); break;
                case "delete": if (_mode == "ready") _script.Steps.RemoveAt(root.GetProperty("index").GetInt32()); break;
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
        _target = _windows.Where(w => w.Handle.ToString() == id).Cast<WindowInfo?>().FirstOrDefault();
        _pendingRecord = false;
        _message = _target is null ? "请选择目标窗口。" : $"已选择 {_target.Value.Title}。";
    }

    private void SetDelay(JsonElement root)
    {
        var value = root.GetProperty("value").GetInt32();
        if (value is < 0 or > 60000) throw new InvalidOperationException();
        _script.Steps[root.GetProperty("index").GetInt32()].DelayMs = value;
    }

    private void SetName(JsonElement root)
    {
        var name = root.GetProperty("value").GetString()?.Trim() ?? "";
        if (name.Length is < 1 or > 100) throw new InvalidOperationException();
        _script.Name = name;
    }

    private void SetText(JsonElement root)
    {
        var text = root.GetProperty("value").GetString() ?? "";
        if (text.Length is < 1 or > 10000) throw new InvalidOperationException();
        var index = root.TryGetProperty("index", out var element) ? element.GetInt32() : -1;
        if (index < 0) _script.Steps.Add(new ScriptStep { Type = StepType.Text, Text = text, DelayMs = 500 });
        else if (_script.Steps[index].Type == StepType.Text) _script.Steps[index].Text = text;
    }

    private void ToggleRecording()
    {
        if (_mode == "recording") { FinishRecording(); return; }
        if (_mode is not ("ready" or "paused") || _target is not { } target) return;
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
        }
        _recorder.Reset();
        _keyboardHook = Native.SetWindowsHookEx(Native.KeyboardHook, _keyboardCallback, 0, 0);
        _mouseHook = Native.SetWindowsHookEx(Native.MouseHook, _mouseCallback, 0, 0);
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
            if ((data.Flags & Native.InjectedKeyboard) == 0 && data.VkCode is not (>= 0x77 and <= 0x7A) && data.VkCode is not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C))
            {
                var tick = Stopwatch.GetTimestamp();
                var keys = new List<int>();
                foreach (var modifier in new[] { 0x11, 0x12, 0x10, 0x5B })
                    if ((Native.GetAsyncKeyState(modifier) & 0x8000) != 0) keys.Add(modifier);
                keys.Add((int)data.VkCode);
                Dispatcher.BeginInvoke(() => AddRecordedStep(new ScriptStep { Type = StepType.Key, Keys = keys }, tick));
            }
        }
        return Native.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

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
        Publish();
    }

    private bool IsTargetReady(WindowInfo target) => Native.Matches(target) && Native.SameSize(target) && Native.GetForegroundWindow() == target.Handle;

    private void CheckFocus()
    {
        if (_target is not { } target) return;
        if (_pendingRecord && IsTargetReady(target)) StartRecording(target);
        if (_mode == "recording" && !IsTargetReady(target)) PauseRecording("目标窗口失焦、关闭或尺寸变化，录制已暂停。");
        if (_mode == "running" && !IsTargetReady(target)) StopRun("目标窗口失焦、关闭或尺寸变化，已停止执行。");
    }

    private void StartRun()
    {
        if (_mode != "ready" || _playback is not null) return;
        if (_target is not { } target || _script.Steps.Count == 0) { _message = "请先选择窗口并准备至少一个步骤。"; Publish(); return; }
        if (!IsTargetReady(target)) { _message = "目标窗口必须位于前台，且尺寸与录制时一致。"; Publish(); return; }
        if (_script.ClientWidth != target.Width || _script.ClientHeight != target.Height ||
            (_script.TargetProcessPath.Length > 0 && !string.Equals(_script.TargetProcessPath, target.ProcessPath, StringComparison.OrdinalIgnoreCase)))
        { _message = "目标程序或窗口尺寸与脚本不符。"; Publish(); return; }
        try { ScriptValidator.Validate(_script); }
        catch (InvalidDataException error) { _message = error.Message; Publish(); return; }
        _playback = new CancellationTokenSource();
        _session.BeginRun(); _currentStep = -1; _message = "正在执行；再按快捷键可停止。";
        Publish();
        _ = RunStepsAsync(target, _playback);
    }

    private async Task RunStepsAsync(WindowInfo target, CancellationTokenSource run)
    {
        try
        {
            for (var index = 0; index < _script.Steps.Count; index++)
            {
                await Task.Yield();
                run.Token.ThrowIfCancellationRequested();
                _currentStep = index; Publish();
                await Task.Delay(_script.Steps[index].DelayMs, run.Token);
                run.Token.ThrowIfCancellationRequested();
                if (!IsTargetReady(target)) { StopRun("目标窗口发生变化，已停止执行。"); return; }
                await ExecuteStepAsync(target, _script.Steps[index], run.Token);
            }
            StopRun("脚本执行完成。", run);
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
        _session.StopRun(); _currentStep = -1; _message = message;
        Publish();
    }

    private void SaveScript()
    {
        if (_mode != "ready") return;
        try { _store.Save(_script); _message = $"脚本已保存到 {_store.FilePath}"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { _message = $"保存失败：{error.Message}"; }
    }

    private void LoadScript()
    {
        if (_mode != "ready") return;
        try
        {
            _script = _store.Load() ?? new ScriptDocument();
            RegisterShortcut(_script.Hotkey);
            _message = "已重新载入脚本，请确认目标窗口。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { _message = $"载入失败：{error.Message}"; }
    }
}
