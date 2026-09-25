using System.Diagnostics;
using System.ComponentModel;
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
    private readonly Queue<(ScriptStep Step, long Tick)> _recordedInputs = new();
    private readonly SessionState _session = new();
    private ScriptDocument _script = new();
    private ScriptDocument _recordingDraft;
    private ScriptDocument? _executionScript;
    private string _workspace = "editor";
    private string _recordingHotkey = "F10";
    private string _recordingTargetTitle = "";
    private bool _namingRequired, _inputDrainScheduled;
    private string _namingError = "";
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
    private KeyboardPlayback? _playbackKeyboard;
    private bool _pageReady;
    private bool _pendingRecord, _pendingRun, _waitingForNextRun, _dirty;

    public MainWindow() : this(null) { }

    public MainWindow(string? dataDirectory)
    {
        _recordingDraft = _script;
        _dataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowKey");
        _catalog = new ScriptCatalog(Path.Combine(_dataDirectory, "scripts"));
        InitializeComponent();
        _keyboardCallback = OnKeyboard;
        _mouseCallback = OnMouse;
        Loaded += OnLoaded;
        Closing += OnClosing;
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
            _executionScript = migrated ?? _savedScripts.FirstOrDefault();
            _recordingHotkey = _executionScript?.Hotkey ?? "F10";
            _recordingTargetTitle = _executionScript?.TargetTitle ?? "";
            _script.Hotkey = _recordingHotkey;
            if (!_message.StartsWith("有 ", StringComparison.Ordinal))
                _message = $"设置目标窗口名称后，切到该窗口按 {_recordingHotkey} 开始录制；再次按下结束并命名保存。";
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

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_mode is "recording" or "paused")
        {
            e.Cancel = true;
            FinishRecording();
        }
        if (!_namingRequired) return;
        e.Cancel = true;
        _namingError = "请先确认保存，或放弃本次录制后再关闭。";
        Dispatcher.BeginInvoke(ShowNamingPrompt);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopHooks();
        _playback?.Cancel();
        ReleasePlaybackKeys();
        if (_handle != 0) Native.UnregisterHotKey(_handle, 1);
        _focusTimer.Stop();
    }

    private nint OnWindowMessage(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == Native.HotkeyMessage && wParam == 1)
        {
            handled = true;
            if (_namingRequired) { ShowNamingPrompt(); return 0; }
            if (_mode == "ready")
            {
                _pendingRecord = _pendingRun = false;
                if (_workspace == "editor")
                {
                    if (!ConfirmRecordingTarget()) { Publish(); return 0; }
                }
                else ConfirmForegroundTarget();
            }
            switch (_session.ResolveShortcut(_target is not null, _script.Steps.Count > 0, _workspace == "editor"))
            {
                case ShortcutAction.SelectTarget:
                    _message = $"请切到要操作的窗口后按 {_script.Hotkey}。";
                    Publish();
                    break;
                case ShortcutAction.SelectScript:
                    _message = "请先从左侧选择已保存的脚本。";
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

    private bool ConfirmForegroundTarget()
    {
        var foreground = Native.GetForegroundWindow();
        _windows.Clear();
        _windows.AddRange(Native.ListWindows(_handle));
        _target = WindowSelection.Find(_windows, foreground.ToString(), Native.Matches);
        return _target is not null;
    }

    private bool ConfirmRecordingTarget()
    {
        if (_recordingTargetTitle.Length == 0)
        {
            _message = "请先设置要录制的窗口名称。";
            return false;
        }
        if (!ConfirmForegroundTarget() || !string.Equals(_target!.Value.Title, _recordingTargetTitle, StringComparison.OrdinalIgnoreCase))
        {
            _target = null;
            _message = $"当前窗口与「{_recordingTargetTitle}」不符，请切到指定窗口后按 {_recordingHotkey}。";
            return false;
        }
        return true;
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
            mode = _mode, workspace = _workspace, message = _message, currentStep = _currentStep,
            currentIteration = _currentIteration, waitingForNextRun = _waitingForNextRun,
            pendingRecord = _pendingRecord, pendingRun = _pendingRun, dirty = _dirty,
            recordingTargetTitle = _recordingTargetTitle, namingRequired = _namingRequired, namingError = _namingError,
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
            if (_namingRequired && action is not ("saveRecording" or "discardRecording" or "refresh"))
            {
                _namingError = "请先输入名称并确认保存，或放弃本次录制。";
                Publish();
                return;
            }
            switch (action)
            {
                case "refresh": RefreshWindows(); break;
                case "recordingTarget": SetRecordingTarget(root.GetProperty("value").GetString()); break;
                case "saveRecording": SaveRecording(root.GetProperty("name").GetString()); break;
                case "discardRecording": DiscardRecording(); break;
                case "workspace": SetWorkspace(root.GetProperty("value").GetString()); break;
                case "openScript": OpenScript(root.GetProperty("id").GetString()); break;
                case "deleteScript": DeleteScript(root.GetProperty("id").GetString()); break;
                case "select": SelectWindow(root.GetProperty("value").GetString()); break;
                case "hotkey":
                    if (_mode == "ready" && root.TryGetProperty("value", out var key) && ScriptValidator.Hotkeys.Contains(key.GetString()))
                    {
                        var code = 0x70 + int.Parse(key.GetString()![1..]) - 1;
                        if (_script.Steps.Any(step => step.Type == StepType.Key && step.Keys.Contains(code)))
                        {
                            _message = "此快捷键已出现在脚本步骤中，请选择其他按键。原快捷键已保留。";
                            break;
                        }
                        if (RegisterShortcut(key.GetString()!))
                        {
                            if (_workspace == "editor") _recordingHotkey = _script.Hotkey;
                            _message = $"快捷键已设置为 {_script.Hotkey}。";
                            _dirty = true;
                            AutoSaveEdits();
                        }
                    }
                    break;
                case "name": if (_mode == "ready" && _workspace == "editor") { SetName(root); AutoSaveEdits(); } break;
                case "execution": if (_mode == "ready") SetExecution(root); break;
                case "record": ToggleRecording(); break;
                case "pause": PauseRecording("录制已暂停，返回目标窗口后可继续。"); break;
                case "finish": FinishRecording(); break;
                case "run": RequestRun(); break;
                case "delete": if (_mode == "ready" && _workspace == "editor") { _script.Steps.RemoveAt(root.GetProperty("index").GetInt32()); _dirty = true; AutoSaveEdits(); } break;
                case "delay": if (_mode == "ready" && _workspace == "editor") { SetDelay(root); AutoSaveEdits(); } break;
                case "text": if (_mode == "ready" && _workspace == "editor") { SetText(root); AutoSaveEdits(); } break;
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
        _pendingRun = false;
        _message = _target is null ? "所选窗口不可用，请刷新列表后重选。" : $"已确认目标窗口：{_target.Value.Title}。";
    }

    private void SetRecordingTarget(string? title)
    {
        if (_mode != "ready" || _workspace != "editor" || _namingRequired) return;
        var value = title?.Trim() ?? "";
        if (value.Length > 512) throw new InvalidOperationException();
        _recordingTargetTitle = value;
        _target = null;
        _pendingRecord = false;
        _message = value.Length == 0 ? "请设置要录制的窗口名称。" : $"已指定「{value}」，切到该窗口按 {_recordingHotkey} 开始录制。";
    }

    private void SetWorkspace(string? workspace)
    {
        if (_mode != "ready" || _namingRequired || workspace is not ("editor" or "execution") || workspace == _workspace) return;
        if (_dirty && (_script.Steps.Count > 0 || _savedScripts.Any(s => s.Id == _script.Id)))
        {
            AutoSaveEdits();
            if (_dirty) return;
        }
        _workspace = workspace;
        _target = null;
        _pendingRecord = _pendingRun = false;
        _currentStep = -1;
        _currentIteration = 0;
        if (workspace == "editor")
        {
            if (_savedScripts.FirstOrDefault(s => s.Id == _recordingDraft.Id) is { } savedDraft)
            {
                _recordingDraft = savedDraft;
                _recordingHotkey = savedDraft.Hotkey;
            }
            _script = _recordingDraft;
            if (RegisterShortcut(_recordingHotkey))
                _message = $"设置目标窗口名称，切到指定窗口按 {_recordingHotkey} 开始录制；再次按下结束并命名。";
        }
        else
        {
            RefreshCatalog();
            _executionScript = _savedScripts.FirstOrDefault(s => s.Id == _executionScript?.Id) ?? _savedScripts.FirstOrDefault();
            _script = _executionScript ?? new ScriptDocument { Hotkey = _recordingHotkey };
            if (RegisterShortcut(_script.Hotkey))
                _message = _executionScript is null ? "请先完成一次录制，已保存脚本会显示在左侧。" : $"已选择「{_script.Name}」，切到目标窗口按 {_script.Hotkey} 执行。";
        }
        _dirty = false;
    }

    private void RefreshCatalog()
    {
        var result = _catalog.List();
        _savedScripts = result.Scripts;
        if (result.Errors.Count > 0) _message = $"有 {result.Errors.Count} 个脚本文件无法载入，请检查本地脚本目录。";
    }

    private void OpenScript(string? id)
    {
        if (_mode != "ready" || _workspace != "execution" || id is null) return;
        try
        {
            var script = _catalog.Load(id);
            if (script is null) { _message = "找不到此脚本，请刷新脚本库。"; return; }
            _script = script;
            _executionScript = script;
            _target = null;
            _pendingRecord = _pendingRun = false;
            _dirty = false;
            if (RegisterShortcut(script.Hotkey)) _message = $"已选择「{script.Name}」，切到目标窗口按 {_script.Hotkey} 执行。";
            else _dirty = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { _message = $"载入失败：{error.Message}"; }
    }

    private void DeleteScript(string? id)
    {
        if (_mode != "ready" || _workspace != "execution" || id is null) return;
        try
        {
            if (!_catalog.Delete(id)) { _message = "脚本已不存在。"; return; }
            RefreshCatalog();
            if (_recordingDraft.Id == id) _recordingDraft = new ScriptDocument { Hotkey = _recordingHotkey };
            if (_executionScript?.Id == id) _executionScript = _savedScripts.FirstOrDefault();
            if (_script.Id == id)
            {
                _script = _executionScript ?? new ScriptDocument { Hotkey = _registeredHotkey ?? "F10" };
                _target = null;
                _pendingRun = false;
                RegisterShortcut(_script.Hotkey);
            }
            _message = "脚本已从本机删除。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { _message = $"删除失败：{error.Message}"; }
    }

    private void SetDelay(JsonElement root)
    {
        var value = root.GetProperty("value").GetInt32();
        if (value < 0) throw new InvalidOperationException();
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
        if (_workspace != "execution" || !_savedScripts.Any(s => s.Id == _script.Id))
        { _message = "请先选择已保存的脚本。"; return; }
        var modeName = root.GetProperty("mode").GetString();
        if (!Enum.TryParse<ExecutionMode>(modeName, out var mode) || !Enum.IsDefined(mode))
            throw new InvalidOperationException();
        var count = root.GetProperty("count").GetInt32();
        var interval = root.GetProperty("intervalMs").GetInt32();
        if (count is < 1 or > 10000 || interval is < 100 or > 60000) throw new InvalidOperationException();
        _script.Execution = new ExecutionPlan { Mode = mode, RepeatCount = count, IntervalMs = interval };
        _dirty = true;
        SaveScript("执行方式已自动保存。");
    }

    private void SetText(JsonElement root)
    {
        if (!_savedScripts.Any(s => s.Id == _script.Id))
        {
            _message = "请先完成录制并命名保存，再编辑文字步骤。";
            return;
        }
        var text = root.GetProperty("value").GetString() ?? "";
        if (text.Length is < 1 or > 10000) throw new InvalidOperationException();
        var index = root.TryGetProperty("index", out var element) ? element.GetInt32() : -1;
        if (index < 0) _script.Steps.Add(new ScriptStep { Type = StepType.Text, Text = text, DelayMs = 500 });
        else if (_script.Steps[index].Type == StepType.Text) _script.Steps[index].Text = text;
        _dirty = true;
    }

    private void ToggleRecording()
    {
        if (_workspace != "editor" || _namingRequired) return;
        if (_mode is "recording" or "paused") { FinishRecording(); return; }
        if (_mode != "ready" || !ConfirmRecordingTarget() || _target is not { } target) return;
        StartRecording(target);
    }

    private void StartRecording(WindowInfo target)
    {
        _pendingRecord = false;
        if (_mode == "ready")
        {
            if (_dirty && (_script.Steps.Count > 0 || _savedScripts.Any(s => s.Id == _script.Id)))
            {
                AutoSaveEdits();
                if (_dirty) { Publish(); return; }
            }
            _script = new ScriptDocument
            {
                Name = "",
                Hotkey = _recordingHotkey,
                TargetTitle = target.Title,
                TargetProcessPath = target.ProcessPath,
                ClientWidth = target.Width,
                ClientHeight = target.Height
            };
            _recordingDraft = _script;
            _dirty = true;
        }
        _recordedInputs.Clear();
        _inputDrainScheduled = false;
        var module = Native.GetModuleHandle(null);
        _keyboardHook = Native.SetWindowsHookEx(Native.KeyboardHook, _keyboardCallback, module, 0);
        _mouseHook = Native.SetWindowsHookEx(Native.MouseHook, _mouseCallback, module, 0);
        if (_keyboardHook == 0 || _mouseHook == 0)
        {
            StopHooks(); _session.FinishRecording(); _message = "无法启用输入监听。"; return;
        }
        _recorder.Reset(Stopwatch.GetTimestamp());
        _session.BeginRecording();
        _message = $"正在录制每个按键与间隔；再按 {_recordingHotkey} 停止并命名保存。";
        Publish();
    }

    private void FinishRecording()
    {
        if (_mode is not ("recording" or "paused")) return;
        DrainRecordedInputs();
        StopHooks(); _pendingRecord = false; _session.FinishRecording();
        if (_script.Steps.Count == 0)
        {
            _dirty = false;
            _message = "录制已结束，本次没有操作步骤，未建立脚本。";
        }
        else
        {
            _namingRequired = true;
            _namingError = "";
            _message = "录制已停止，请输入脚本名称，确认后保存到执行脚本。";
            ShowNamingPrompt();
        }
        Publish();
    }

    private void ShowNamingPrompt()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Browser.Focus();
        Publish();
    }

    private void SaveRecording(string? name)
    {
        if (!_namingRequired || _mode != "ready" || _workspace != "editor") return;
        var value = name?.Trim() ?? "";
        if (value.Length is < 1 or > 100)
        {
            _namingError = "请输入 1–100 个字符的脚本名称。";
            return;
        }
        _script.Name = value;
        if (!SaveScript()) { _namingError = _message; return; }
        _namingRequired = false;
        _namingError = "";
        SetWorkspace("execution");
        _message = $"已保存「{value}」，可选择执行方式后运行。";
    }

    private void DiscardRecording()
    {
        if (!_namingRequired || _mode != "ready") return;
        _script = new ScriptDocument { Hotkey = _recordingHotkey };
        _recordingDraft = _script;
        _namingRequired = false;
        _namingError = "";
        _dirty = false;
        _message = "已放弃本次录制。目标窗口与快捷键设置已保留。";
    }

    private void PauseRecording(string message)
    {
        if (_mode != "recording") return;
        DrainRecordedInputs();
        StopHooks(); _session.PauseRecording(); _message = message; Publish();
    }

    private void StopHooks()
    {
        if (_keyboardHook != 0) { Native.UnhookWindowsHookEx(_keyboardHook); _keyboardHook = 0; }
        if (_mouseHook != 0) { Native.UnhookWindowsHookEx(_mouseHook); _mouseHook = 0; }
    }

    private nint OnKeyboard(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && _mode == "recording" &&
            wParam is Native.KeyDown or Native.SysKeyDown or Native.KeyUp or Native.SysKeyUp)
        {
            var data = Marshal.PtrToStructure<Native.KeyboardData>(lParam);
            var step = KeyboardStepFactory.Create(data.VkCode, data.Flags,
                wParam is Native.KeyUp or Native.SysKeyUp, _hotkeyCode);
            if (step is not null)
            {
                var tick = Stopwatch.GetTimestamp();
                QueueRecordedStep(step, tick);
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
                if (_target is { } target)
                {
                    Native.ScreenToClient(target.Handle, ref point);
                    QueueRecordedStep(wParam == Native.Wheel
                        ? new ScriptStep { Type = StepType.Scroll, X = point.X, Y = point.Y, WheelDelta = wheel }
                        : new ScriptStep { Type = StepType.Click, X = point.X, Y = point.Y, Button = button }, tick);
                }
            }
        }
        return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void AddRecordedStep(ScriptStep step, long tick)
    {
        QueueRecordedStep(step, tick);
        DrainRecordedInputs();
    }

    private void QueueRecordedStep(ScriptStep step, long tick)
    {
        if (_mode != "recording" || _target is not { } target || !IsTargetReady(target)) return;
        if (step.Type is StepType.Click or StepType.Scroll &&
            (step.X < 0 || step.Y < 0 || step.X >= target.Width || step.Y >= target.Height)) return;
        _recordedInputs.Enqueue((step, tick));
        if (_inputDrainScheduled) return;
        _inputDrainScheduled = true;
        Dispatcher.BeginInvoke(DrainRecordedInputs);
    }

    private void DrainRecordedInputs()
    {
        _inputDrainScheduled = false;
        if (_mode != "recording" || _recordedInputs.Count == 0) return;
        while (_recordedInputs.TryDequeue(out var captured))
            _recorder.Add(_script.Steps, captured.Step, captured.Tick);
        _dirty = true;
        Publish();
    }

    private bool IsTargetReady(WindowInfo target) => Native.Matches(target) && Native.SameSize(target) && Native.GetForegroundWindow() == target.Handle;

    private void CheckFocus()
    {
        if (_pendingRun && _target is null) ConfirmForegroundTarget();
        if (_target is not { } target) return;
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
        if (_mode != "ready" || _workspace != "execution") return;
        if (_pendingRun) { _pendingRun = false; _message = "已取消等待执行。"; Publish(); return; }
        if (_script.Steps.Count == 0 || !_savedScripts.Any(s => s.Id == _script.Id))
        { _message = "请先从左侧选择有操作步骤的已保存脚本。"; Publish(); return; }
        if (_target is null) ConfirmForegroundTarget();
        if (_target is not { } target)
        {
            _pendingRun = true;
            _pendingRecord = false;
            _message = "已准备执行，切到脚本对应的目标窗口即可开始；再次点击可取消。";
            Publish();
            return;
        }
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
        if (_mode != "ready" || _workspace != "execution" || _playback is not null) return;
        if (_target is not { } target) { _message = "请先选择目标窗口。"; Publish(); return; }
        if (_script.Steps.Count == 0 || !_savedScripts.Any(s => s.Id == _script.Id))
        { _message = "请先选择有操作步骤的已保存脚本。"; Publish(); return; }
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
        var keyboard = new KeyboardPlayback((key, release) => Native.SendKey(key, release));
        _playbackKeyboard = keyboard;
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
                    await ExecuteStepAsync(target, step, keyboard, cancellation);
                    if (index == _script.Steps.Count - 1) keyboard.ReleaseAll();
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
        finally
        {
            try { keyboard.ReleaseAll(); }
            catch (Exception error)
            {
                _message = $"释放执行按键失败：{error.Message}";
                Publish();
            }
            if (ReferenceEquals(_playback, run)) { _playback = null; run.Dispose(); }
            if (ReferenceEquals(_playbackKeyboard, keyboard)) _playbackKeyboard = null;
        }
    }

    private async Task ExecuteStepAsync(WindowInfo target, ScriptStep step, KeyboardPlayback keyboard, CancellationToken cancellation)
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
        else if (step.Type == StepType.Key) keyboard.Execute(step);
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
        var releaseError = ReleasePlaybackKeys();
        _session.StopRun(); _currentStep = -1; _waitingForNextRun = false; _message = releaseError ?? message;
        Publish();
    }

    private string? ReleasePlaybackKeys()
    {
        try { _playbackKeyboard?.ReleaseAll(); return null; }
        catch (Win32Exception error) { return $"释放执行按键失败：{error.Message}"; }
    }

    private void AutoSaveEdits()
    {
        if (_namingRequired) return;
        if (_savedScripts.Any(s => s.Id == _script.Id) ||
            (_workspace == "editor" && _script.ClientWidth > 0 && _script.Steps.Count > 0))
            SaveScript("修改已自动保存。");
    }

    private bool SaveScript(string? successMessage = null)
    {
        if (_mode != "ready") return false;
        try
        {
            _catalog.Save(_script);
            if (_workspace == "editor") _recordingDraft = _script;
            _executionScript = _workspace == "execution" ? _script : _catalog.Load(_script.Id);
            RefreshCatalog();
            _dirty = false;
            _message = successMessage ?? $"已保存「{_script.Name}」到脚本库。";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _dirty = true;
            _message = $"保存失败：{error.Message}";
            return false;
        }
    }

}
