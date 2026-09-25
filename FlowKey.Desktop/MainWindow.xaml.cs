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
    private KeyboardCapture? _keyboardCapture;
    private readonly List<WindowInfo> _windows = [];
    private readonly StepRecorder _recorder = new();
    private readonly Queue<(ScriptStep Step, long Tick)> _recordedInputs = new();
    private readonly SessionState _session = new();
    private ScriptDocument _script = new();
    private ScriptDocument _recordingDraft;
    private ScriptDocument? _executionScript;
    private string _workspace = "editor";
    private FlowPreferences _flowPreferences = new();
    private WindowInfo? _flowTargetIdentity;
    private WindowActivationWait? _activationWait;
    private string _flowMessage = "修改後自動儲存，適用於所有腳本。";
    private string FlowSettingsPath => Path.Combine(_dataDirectory, "settings.json");
    private string _recordingHotkey = "F10";
    private bool _namingRequired;
    private string _namingError = "";
    private IReadOnlyList<ScriptDocument> _savedScripts = [];
    private WindowInfo? _target;
    private nint _handle;
    private int _hotkeyCode = 121;
    private string? _registeredHotkey;
    private string _mode => _session.Mode;
    private string _message = "設定快捷鍵後即可開始錄製。";
    private int _currentStep = -1;
    private long _currentIteration;
    private CancellationTokenSource? _playback;
    private KeyboardPlayback? _playbackKeyboard;
    private readonly bool _elevated = ProcessAccess.IsElevated(Environment.ProcessId) == true;
    private string _recordingAccessWarning = "";
    private nint _accessForeground;
    private bool _pageReady;
    private bool _pendingRecord, _pendingRun, _waitingForNextRun, _dirty;

    public MainWindow() : this(null) { }

    public MainWindow(string? dataDirectory)
    {
        _recordingDraft = _script;
        _dataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowKey");
        _catalog = new ScriptCatalog(Path.Combine(_dataDirectory, "scripts"));
        InitializeComponent();
        try { _flowPreferences = FlowPreferences.Load(FlowSettingsPath); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { _flowMessage = "設定未能載入，暫用手動切換：" + error.Message; }
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
            var launchArguments = Environment.GetCommandLineArgs();
            var hotkeyArgument = Array.IndexOf(launchArguments, "--recording-hotkey");
            if (hotkeyArgument >= 0 && hotkeyArgument + 1 < launchArguments.Length && ScriptValidator.Hotkeys.Contains(launchArguments[hotkeyArgument + 1]))
                _recordingHotkey = launchArguments[hotkeyArgument + 1];
            _script.Hotkey = _recordingHotkey;
            if (!_message.StartsWith("有 ", StringComparison.Ordinal))
                _message = $"按 {_recordingHotkey} 或點選「開始錄製」開始；再次按下結束並命名儲存。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { _message = $"載入腳本庫失敗：{error.Message}"; }
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
                    MessageBox.Show($"無法載入內建介面：{args.WebErrorStatus}", "FlowKey");
                    Close();
                    return;
                }
                _pageReady = true;
                Publish();
            };
            Browser.Source = new Uri("https://flowkey.local/index.html");
        }
        catch (Exception error) { MessageBox.Show($"無法啟動介面：{error.Message}\n請安裝 WebView2 Runtime。", "FlowKey"); Close(); }
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
        _namingError = "請先確認儲存，或放棄本次錄製後再關閉。";
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
            if (_workspace == "settings") return 0;
            if (_namingRequired) { ShowNamingPrompt(); return 0; }
            if (_workspace == "execution" && _mode == "ready")
            {
                if (!_flowPreferences.AutoSwitch && !_pendingRun) ConfirmForegroundTarget();
                RequestRun(); Publish(); return 0;
            }
            if (_mode == "ready")
            {
                _pendingRecord = _pendingRun = false;
                if (_workspace == "execution") ConfirmForegroundTarget();
            }
            switch (_session.ResolveShortcut(_target is not null, _script.Steps.Count > 0, _workspace == "editor"))
            {
                case ShortcutAction.SelectTarget:
                    _message = $"請切到要操作的視窗後按 {_script.Hotkey}。";
                    Publish();
                    break;
                case ShortcutAction.SelectScript:
                    _message = "請先從左側選擇已儲存的腳本。";
                    Publish();
                    break;
                case ShortcutAction.StartRecording:
                case ShortcutAction.ResumeRecording:
                    ToggleRecording();
                    Publish();
                    break;
                case ShortcutAction.FinishRecording: FinishRecording(); break;
                case ShortcutAction.StartRun: StartRun(); break;
                case ShortcutAction.StopRun: StopRun("已停止執行。"); break;
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
        _message = $"{hotkey} 已被系統或其他程式佔用，請選擇其他快捷鍵。";
        return false;
    }

    private void RefreshWindows()
    {
        _windows.Clear();
        _windows.AddRange(Native.ListWindows(_handle));
        if (_target is { } target && !Native.Matches(target))
        {
            if (_mode == "running") StopRun("目標視窗已關閉，已停止執行。");
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
            flow = new { autoSwitch = _flowPreferences.AutoSwitch, returnToApp = _flowPreferences.ReturnToApp,
                targetTitle = _flowPreferences.TargetTitle, targetId = _flowPreferences.Resolve(_windows, _flowTargetIdentity)?.Handle.ToString() ?? "", message = _flowMessage },
            currentIteration = _currentIteration, waitingForNextRun = _waitingForNextRun,
            pendingRecord = _pendingRecord, pendingRun = _pendingRun, dirty = _dirty,
            switchingWindow = _pendingRun && _activationWait is not null,
            namingRequired = _namingRequired, namingError = _namingError,
            elevated = _elevated, recordingAccessWarning = _recordingAccessWarning,
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
                _namingError = "請先輸入名稱並確認儲存，或放棄本次錄製。";
                Publish();
                return;
            }
            switch (action)
            {
                case "refresh": RefreshWindows(); break;
                case "saveRecording": SaveRecording(root.GetProperty("name").GetString()); break;
                case "discardRecording": DiscardRecording(); break;
                case "workspace": SetWorkspace(root.GetProperty("value").GetString()); break;
                case "flowSettings": SetFlowSettings(root); break;
                case "openScript": OpenScript(root.GetProperty("id").GetString()); break;
                case "deleteScript": DeleteScript(root.GetProperty("id").GetString()); break;
                case "select": SelectWindow(root.GetProperty("value").GetString()); break;
                case "hotkey":
                    if (_mode == "ready" && root.TryGetProperty("value", out var key) && ScriptValidator.Hotkeys.Contains(key.GetString()))
                    {
                        var code = 0x70 + int.Parse(key.GetString()![1..]) - 1;
                        if (_script.Steps.Any(step => step.Type == StepType.Key && step.Keys.Contains(code)))
                        {
                            _message = "此快捷鍵已出現在腳本步驟中，請選擇其他按鍵。原快捷鍵已保留。";
                            break;
                        }
                        if (RegisterShortcut(key.GetString()!))
                        {
                            if (_workspace == "editor") _recordingHotkey = _script.Hotkey;
                            _message = $"快捷鍵已設定為 {_script.Hotkey}。";
                            _dirty = true;
                            AutoSaveEdits();
                        }
                    }
                    break;
                case "name": if (_mode == "ready" && _workspace == "editor") { SetName(root); AutoSaveEdits(); } break;
                case "execution": if (_mode == "ready") SetExecution(root); break;
                case "restartAdmin": RestartAsAdministrator(); break;
                case "record": ToggleRecording(); break;
                case "pause": break;
                case "finish": FinishRecording(); break;
                case "run": RequestRun(); break;
                case "delete": if (_mode == "ready" && _workspace == "editor") { _script.Steps.RemoveAt(root.GetProperty("index").GetInt32()); _dirty = true; AutoSaveEdits(); } break;
                case "delay": if (_mode == "ready" && _workspace == "editor") { SetDelay(root); AutoSaveEdits(); } break;
                case "text": if (_mode == "ready" && _workspace == "editor") { SetText(root); AutoSaveEdits(); } break;
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or ArgumentOutOfRangeException or InvalidOperationException)
        { _message = "操作內容無效，請重試。"; }
        Publish();
    }

    private void SelectWindow(string? id)
    {
        if (_mode != "ready") return;
        _target = WindowSelection.Find(_windows, id, Native.Matches);
        _pendingRecord = false;
        _pendingRun = false;
        _message = _target is null ? "所選視窗不可用，請重新整理列表後重選。" : $"已確認目標視窗：{_target.Value.Title}。";
    }

    private void SetWorkspace(string? workspace)
    {
        if (_mode != "ready" || _pendingRun || _namingRequired || workspace is not ("editor" or "execution" or "settings") || workspace == _workspace) return;
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
                _message = $"設定快捷鍵後，按 {_recordingHotkey} 開始錄製；再次按下結束並命名。";
        }
        else if (workspace == "execution")
        {
            RefreshCatalog();
            _executionScript = _savedScripts.FirstOrDefault(s => s.Id == _executionScript?.Id) ?? _savedScripts.FirstOrDefault();
            _script = _executionScript ?? new ScriptDocument { Hotkey = _recordingHotkey };
            if (RegisterShortcut(_script.Hotkey))
                _message = _executionScript is null ? "請先完成一次錄製，已儲存腳本會顯示在左側。" : $"已選擇「{_script.Name}」，切到目標視窗按 {_script.Hotkey} 執行。";
        }
        _dirty = false;
    }

    private void SetFlowSettings(JsonElement root)
    {
        if (_workspace != "settings" || _mode != "ready" || _pendingRun || _namingRequired) return;
        var next = _flowPreferences with { AutoSwitch = root.GetProperty("autoSwitch").GetBoolean(),
            ReturnToApp = root.GetProperty("returnToApp").GetBoolean() };
        var nextTarget = _flowTargetIdentity;
        if (root.TryGetProperty("targetId", out var targetId))
        {
            var id = targetId.GetString();
            if (string.IsNullOrEmpty(id)) { next = next with { TargetTitle = "", TargetProcess = "", TargetPath = "" }; nextTarget = null; }
            else
            {
                var selected = WindowSelection.Find(Native.ListWindows(_handle), id, Native.Matches);
                if (selected is not { } target) { _flowMessage = "視窗已關閉，請重新整理後重新選擇。"; return; }
                next = next with { TargetTitle = target.Title, TargetProcess = target.ProcessName, TargetPath = target.ProcessPath };
                nextTarget = target;
            }
        }
        try { next.Save(FlowSettingsPath); _flowPreferences = next; _flowTargetIdentity = nextTarget; _flowMessage = "已自動儲存，適用於所有腳本。"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _flowMessage = "儲存失敗，仍使用先前設定：" + error.Message; }
    }

    private void RefreshCatalog()
    {
        var result = _catalog.List();
        _savedScripts = result.Scripts;
        if (result.Errors.Count > 0) _message = $"有 {result.Errors.Count} 個腳本檔案無法載入，請檢查本地腳本目錄。";
    }

    private void OpenScript(string? id)
    {
        if (_mode != "ready" || _workspace != "execution" || id is null) return;
        try
        {
            var script = _catalog.Load(id);
            if (script is null) { _message = "找不到此腳本，請重新整理腳本庫。"; return; }
            _script = script;
            _executionScript = script;
            _target = null;
            _pendingRecord = _pendingRun = false;
            _dirty = false;
            if (RegisterShortcut(script.Hotkey)) _message = $"已選擇「{script.Name}」，切到目標視窗按 {_script.Hotkey} 執行。";
            else _dirty = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { _message = $"載入失敗：{error.Message}"; }
    }

    private void DeleteScript(string? id)
    {
        if (_mode != "ready" || _workspace != "execution" || id is null) return;
        try
        {
            if (!_catalog.Delete(id)) { _message = "腳本已不存在。"; return; }
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
            _message = "腳本已從本機刪除。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { _message = $"刪除失敗：{error.Message}"; }
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
        { _message = "請先選擇已儲存的腳本。"; return; }
        var modeName = root.GetProperty("mode").GetString();
        if (!Enum.TryParse<ExecutionMode>(modeName, out var mode) || !Enum.IsDefined(mode))
            throw new InvalidOperationException();
        var count = root.GetProperty("count").GetInt32();
        var interval = root.GetProperty("intervalMs").GetInt32();
        if (count is < 1 or > 10000 || interval is < 100 or > 60000) throw new InvalidOperationException();
        _script.Execution = new ExecutionPlan { Mode = mode, RepeatCount = count, IntervalMs = interval };
        _dirty = true;
        SaveScript("執行方式已自動儲存。");
    }

    private void SetText(JsonElement root)
    {
        if (!_savedScripts.Any(s => s.Id == _script.Id))
        {
            _message = "請先完成錄製並命名儲存，再編輯文字步驟。";
            return;
        }
        var text = root.GetProperty("value").GetString() ?? "";
        if (text.Length is < 1 or > 10000) throw new InvalidOperationException();
        var index = root.TryGetProperty("index", out var element) ? element.GetInt32() : -1;
        if (index < 0 || index >= _script.Steps.Count) return;
        if (_script.Steps[index].Type == StepType.Text) _script.Steps[index].Text = text;
        _dirty = true;
    }

    private void RestartAsAdministrator()
    {
        if (_elevated || _mode != "ready" || _namingRequired) return;
        if (_dirty && (_script.Steps.Count > 0 || _savedScripts.Any(s => s.Id == _script.Id)))
        {
            AutoSaveEdits();
            if (_dirty) return;
        }
        if (ProcessAccess.TryRestart(Environment.ProcessPath!, _recordingHotkey, out var error)) Close();
        else _message = error;
    }

    private void ToggleRecording()
    {
        if (_workspace != "editor" || _namingRequired) return;
        if (_mode is "recording" or "paused") { FinishRecording(); return; }
        if (_mode != "ready") return;
        StartRecording();
    }

    private void StartRecording()
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
                GlobalKeyboardRecording = true
            };
            _recordingDraft = _script;
            _dirty = true;
        }
        _target = null;
        _accessForeground = 0;
        _recordingAccessWarning = "";
        _recordedInputs.Clear();
        try { _keyboardCapture = new KeyboardCapture(_hotkeyCode); }
        catch (InvalidOperationException error) { _message = error.Message; Publish(); return; }
        _recorder.Reset(_keyboardCapture.StartedAt);
        _session.BeginRecording();
        _message = $"正在錄製每個按鍵與間隔；再按 {_recordingHotkey} 停止並命名儲存。";
        Publish();
    }

    private void FinishRecording()
    {
        if (_mode is not ("recording" or "paused")) return;
        StopHooks();
        DrainRecordedInputs();
        _pendingRecord = false; _session.FinishRecording();
        if (_script.Steps.Count == 0)
        {
            _dirty = false;
            _message = "錄製已結束，本次沒有操作步驟，未建立腳本。";
        }
        else
        {
            _namingRequired = true;
            _namingError = "";
            _message = "錄製已停止，請輸入腳本名稱，確認後儲存到執行腳本。";
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
            _namingError = "請輸入 1–100 個字元的腳本名稱。";
            return;
        }
        _script.Name = value;
        if (!SaveScript()) { _namingError = _message; return; }
        _namingRequired = false;
        _namingError = "";
        SetWorkspace("execution");
        _message = $"已儲存「{value}」，可選擇執行方式後執行。";
    }

    private void DiscardRecording()
    {
        if (!_namingRequired || _mode != "ready") return;
        _script = new ScriptDocument { Hotkey = _recordingHotkey };
        _recordingDraft = _script;
        _namingRequired = false;
        _namingError = "";
        _dirty = false;
        _message = "已放棄本次錄製。快捷鍵設定已保留。";
    }

    private void StopHooks()
    {
        if (_keyboardCapture is not { } capture) return;
        capture.Dispose();
        while (capture.TryDequeue(out var item)) _recordedInputs.Enqueue(item);
        _keyboardCapture = null;
    }

    private void AddRecordedStep(ScriptStep step, long tick)
    {
        QueueRecordedStep(step, tick);
        DrainRecordedInputs();
    }

    private void QueueRecordedStep(ScriptStep step, long tick)
    {
        if (_mode != "recording") return;
        _recordedInputs.Enqueue((step, tick));

    }

    private void DrainRecordedInputs()
    {
        if (_keyboardCapture is { } capture)
            while (capture.TryDequeue(out var item)) _recordedInputs.Enqueue(item);
        if (_mode != "recording" || _recordedInputs.Count == 0) return;
        while (_recordedInputs.TryDequeue(out var captured))
            _recorder.Add(_script.Steps, captured.Step, captured.Tick);
        _dirty = true;
        Publish();
    }

    private bool IsTargetReady(WindowInfo target) => Native.Matches(target) && Native.SameSize(target) && Native.GetForegroundWindow() == target.Handle;

    private void CheckFocus()
    {
        DrainRecordedInputs();
        if (_mode == "recording")
        {
            var foreground = Native.GetForegroundWindow();
            if (_accessForeground != foreground)
            {
                _accessForeground = foreground;
                Native.GetWindowThreadProcessId(foreground, out var processId);
                _recordingAccessWarning = ProcessAccess.RecordingWarning(_elevated, ProcessAccess.IsElevated((int)processId));
                Publish();
            }
        }
        if (_pendingRun && _activationWait is { } activation)
        {
            var exists = _target is { } candidate && Native.Matches(candidate);
            var result = activation.Observe(Environment.TickCount64, exists,
                exists && Native.GetForegroundWindow() == _target!.Value.Handle && !Native.IsIconic(_target.Value.Handle));
            if (result == ActivationStatus.Waiting) return;
            _activationWait = null;
            _pendingRun = false;
            if (result == ActivationStatus.Missing)
            { _message = MissingFlowTargetMessage(); Publish(); return; }
            if (result == ActivationStatus.TimedOut)
            { _message = "視窗切換未完成，本次沒有執行。請確認目標視窗可用後重試。"; Publish(); return; }
            // Restore can change the client size; capture the settled dimensions before preflight.
            var activated = _target!.Value;
            if (Native.GetClientRect(activated.Handle, out var rect))
                _target = activated with { Width = rect.Width, Height = rect.Height };
            StartRun(); Publish(); return;
        }
        if (_pendingRun && _target is null) ConfirmForegroundTarget();
        if (_target is not { } target) return;
        if (_pendingRun)
        {
            if (!Native.Matches(target) || !Native.SameSize(target))
            { _pendingRun = false; _message = "目標視窗已關閉或尺寸變化，取消等待執行。"; Publish(); }
            else if (IsTargetReady(target)) { _pendingRun = false; StartRun(); }
        }
        if (_mode == "running" && !IsTargetReady(target)) StopRun("目標視窗失焦、關閉或尺寸變化，已停止執行。");
    }

    private void RequestRun()
    {
        if (_mode == "running") { StopRun("已停止執行。"); return; }
        if (_mode != "ready" || _workspace != "execution") return;
        if (_pendingRun) { _pendingRun = false; _activationWait = null; _message = "已取消等待執行。"; Publish(); return; }
        if (_playback is not null) { _message = "上一輪執行正在結束，請稍後再啟動。"; Publish(); return; }
        if (_script.Steps.Count == 0 || !_savedScripts.Any(s => s.Id == _script.Id))
        { _message = "請先從左側選擇有操作步驟的已儲存腳本。"; Publish(); return; }
        if (_flowPreferences.AutoSwitch)
        {
            RefreshWindows();
            _target = _flowPreferences.Resolve(_windows, _flowTargetIdentity);
            if (_target is not { } automaticTarget)
            { _message = MissingFlowTargetMessage(); Publish(); return; }
            var automaticProblem = Native.IsIconic(automaticTarget.Handle) ? null : RunPreflight(automaticTarget);
            if (automaticProblem is not null) { _message = automaticProblem; Publish(); return; }
            _activationWait = new WindowActivationWait(Environment.TickCount64);
            _pendingRun = true;
            _message = $"正在切換到「{automaticTarget.Title}」，視窗就緒後自動開始。";
            Publish();
            Native.RequestWindowActivation(automaticTarget.Handle);
            return;
        }
        if (_target is null) ConfirmForegroundTarget();
        if (_target is not { } target)
        {
            _pendingRun = true;
            _pendingRecord = false;
            _message = "已準備執行，切到腳本對應的目標視窗即可開始；再次點選可取消。";
            Publish();
            return;
        }
        var problem = RunPreflight(target);
        if (problem is not null) { _message = problem; Publish(); return; }
        _pendingRecord = false;
        if (Native.GetForegroundWindow() != target.Handle)
        { _pendingRun = true; _message = "已準備執行，請切回目標視窗；再次點選可取消。"; Publish(); return; }
        StartRun();
    }

    private string MissingFlowTargetMessage() => _flowPreferences.TargetTitle.Length == 0
        ? "尚未設定指定視窗，請到設定頁選擇執行視窗。"
        : $"未找到指定視窗「{_flowPreferences.TargetTitle}」。請先開啟該程式；若已開啟，請重新整理設定中的視窗列表並重新選擇（同名視窗也需重選）。";

    private string? RunPreflight(WindowInfo target)
    {
        if (!Native.Matches(target) || !Native.SameSize(target)) return "目標視窗已關閉或尺寸變化。";
        if (!_script.GlobalKeyboardRecording && (_script.ClientWidth != target.Width || _script.ClientHeight != target.Height ||
            (_script.TargetProcessPath.Length > 0 && !string.Equals(_script.TargetProcessPath, target.ProcessPath, StringComparison.OrdinalIgnoreCase))))
            return "目標程式或視窗尺寸與腳本不符。";
        try { ScriptValidator.Validate(_script); }
        catch (InvalidDataException error) { return error.Message; }
        return null;
    }

    private void StartRun()
    {
        if (_mode != "ready" || _workspace != "execution" || _playback is not null) return;
        if (_target is not { } target) { _message = "請先選擇目標視窗。"; Publish(); return; }
        if (_script.Steps.Count == 0 || !_savedScripts.Any(s => s.Id == _script.Id))
        { _message = "請先選擇有操作步驟的已儲存腳本。"; Publish(); return; }
        var problem = RunPreflight(target);
        if (problem is not null) { _message = problem; Publish(); return; }
        if (Native.GetForegroundWindow() != target.Handle) { _message = "目標視窗必須位於前臺。"; Publish(); return; }
        _playback = new CancellationTokenSource();
        _session.BeginRun(); _currentStep = -1; _currentIteration = 0; _waitingForNextRun = false;
        _message = "正在執行；再按快捷鍵可停止。";
        Publish();
        _ = RunStepsAsync(target, _playback);
    }

    private async Task RunStepsAsync(WindowInfo target, CancellationTokenSource run)
    {
        var returnToApp = _flowPreferences.ReturnToApp;
        // Use one target layout for both down and up, including cleanup after focus changes.
        var keyboardLayout = Native.GetKeyboardLayout(Native.GetWindowThreadProcessId(target.Handle, out _));
        var keyboard = new KeyboardPlayback((key, release) => Native.SendKey(key, release, layout: keyboardLayout));
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
                    { StopRun("目標視窗發生變化，已停止執行。"); cancellation.ThrowIfCancellationRequested(); }
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
                    { StopRun("目標視窗發生變化，已停止執行。"); cancellation.ThrowIfCancellationRequested(); }
                }, run.Token);
            StopRun($"腳本執行完成，共 {_currentIteration} 輪。", run);
        }
        catch (OperationCanceledException) { /* StopRun already updated the UI. */ }
        catch (Exception error) { StopRun($"執行失敗：{error.Message}", run); }
        finally
        {
            try { keyboard.ReleaseAll(); }
            catch (Exception error)
            {
                _message = $"釋放執行按鍵失敗：{error.Message}";
                Publish();
            }
            // SendInput queues events. Let the target process the final key/up before
            // switching focus, otherwise a short script can lose its final actions.
            if (returnToApp)
            {
                await Task.Delay(100);
                if (ReferenceEquals(_playback, run) && IsVisible && !Native.ActivateWindow(_handle))
                { _message += " 無法自動返回 FlowKey，請點選工作列開啟。"; Publish(); }
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
            if (!Native.SetCursorPos(point.X, point.Y)) throw new InvalidOperationException("無法移動滑鼠到目標位置。");
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
                if (!IsTargetReady(target)) throw new InvalidOperationException("目標視窗失焦或尺寸變化。");
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
        catch (Win32Exception error) { return $"釋放執行按鍵失敗：{error.Message}"; }
    }

    private void AutoSaveEdits()
    {
        if (_namingRequired) return;
        if (_savedScripts.Any(s => s.Id == _script.Id) ||
            (_workspace == "editor" && _script.ClientWidth > 0 && _script.Steps.Count > 0))
            SaveScript("修改已自動儲存。");
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
            _message = successMessage ?? $"已儲存「{_script.Name}」到腳本庫。";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _dirty = true;
            _message = $"儲存失敗：{error.Message}";
            return false;
        }
    }

}
