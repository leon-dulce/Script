using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FlowKey.Core;
using FlowKey.Desktop;

internal static class DesktopE2E
{
    private sealed class DesktopUnavailableException(string message) : Exception(message);
    private static nint _keyboardTarget;

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AttachThreadInput(uint currentThread, uint foregroundThread, bool attach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint handle);

    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunOnDesktop(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is DesktopUnavailableException unavailable)
        {
            Console.WriteLine($"SKIP Windows UI end-to-end: {unavailable.Message}");
            return;
        }
        if (failure is not null) throw new Exception("Windows desktop end-to-end test failed.", failure);
    }

    private static void RunOnDesktop()
    {
        var root = Path.Combine(Path.GetTempPath(), "FlowKey-e2e-" + Guid.NewGuid().ToString("N"));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var targetName = "FlowKey E2E target " + Guid.NewGuid().ToString("N");
        var editor = new TextBox { FontSize = 18, AcceptsReturn = true };
        var target = new Window { Title = targetName, Width = 650, Height = 400, Content = editor };
        var app = new MainWindow(root);
        var loaded = false;
        app.Browser.NavigationCompleted += (_, e) => loaded = e.IsSuccess;
        try
        {
            app.Show();
            Until(() => loaded, "embedded interface did not load", 30000);
            target.Show();
            _keyboardTarget = new WindowInteropHelper(target).Handle;
            if (Environment.GetEnvironmentVariable("FLOWKEY_E2E_MANUAL_FOCUS") == "1")
                WaitForInitialTargetFocus(target);
            ActivateTarget(target, editor);
            var hotkey = ChooseHotkey(app);
            var catalog = new ScriptCatalog(Path.Combine(root, "scripts"));
            if (EvalBool(app, "!document.getElementById('sidebar-library').hidden"))
                throw new Exception("Saved scripts must be hidden in the recording workspace.");
            if (!EvalBool(app, "['load-button','save-button','new-editor-script','window-select'].every(id=>!document.getElementById(id)||document.getElementById(id).hidden) && document.getElementById('run-button').hidden && document.getElementById('recording-progress').hidden"))
                throw new Exception("Recording still exposes removed save, reload, new-script, window-picker, execution-navigation, or progress controls.");
            Eval(app, "document.getElementById('nav-execution').click()");
            Until(() => EvalBool(app, "!document.getElementById('execution-view').hidden && document.getElementById('execution-start').disabled"),
                "empty execution library must disable playback");
            Eval(app, "document.getElementById('nav-editor').click()");
            Until(() => EvalBool(app, "!document.getElementById('workspace-view').hidden"), "recording workspace did not open");

            ActivateTarget(target, editor);
            PressHotkey(hotkey);
            Until(() => Text(app, "status-text") == "正在录制" && Text(app, "window-name") == targetName,
                "hotkey did not capture the foreground target and start recording without a picker");
            AddTestRecordedText(app, "X");
            AddTestRecordedText(app, "Y");
            PressHotkey(hotkey);
            Until(() => Text(app, "status-text") == "就绪，等待操作" && catalog.List().Scripts.Count == 1,
                "recording-end hotkey did not automatically save the script");
            var firstId = catalog.List().Scripts.Single().Id;
            SetRecordedDelay(app, 0, 400);
            SetRecordedDelay(app, 1, 700);
            Until(() => catalog.Load(firstId)!.Steps.Select(s => s.DelayMs).SequenceEqual([400, 700]),
                "recording edits did not automatically persist");

            ActivateTarget(target, editor);
            PressHotkey(hotkey);
            Until(() => Text(app, "status-text") == "正在录制" && Text(app, "step-count") == "0 个步骤",
                "second recording did not begin a fresh script");
            AddTestRecordedText(app, "Z");
            ActivateWindow(app);
            Until(() => Text(app, "status-text") == "录制已暂停", "recording did not pause when its target lost focus");
            Eval(app, "document.getElementById('finish-button').click()");
            Until(() => Text(app, "status-text") == "就绪，等待操作" && catalog.List().Scripts.Count == 2,
                "finish button did not save a paused recording");
            var secondId = catalog.List().Scripts.Single(s => s.Id != firstId).Id;
            if (!catalog.Load(firstId)!.Steps.Select(s => s.Text).SequenceEqual(["X", "Y"]))
                throw new Exception("Starting a second recording overwrote the first script.");
            Eval(app, "document.getElementById('nav-execution').click()");
            Until(() => EvalBool(app, "!document.getElementById('execution-view').hidden") && CurrentScript(app).Id == secondId,
                "latest completed recording was not selected for execution");
            SetPlan(app, "Count", 2, 250);
            Until(() => catalog.Load(secondId)!.Execution.RepeatCount == 2, "second script execution settings did not save");
            hotkey = ChooseHotkey(app, "execution-hotkey", hotkey);
            Until(() => catalog.Load(secondId)!.Hotkey == hotkey, "alternate execution hotkey did not save");
            Eval(app, "document.getElementById('nav-editor').click()");
            Until(() => EvalBool(app, "!document.getElementById('workspace-view').hidden") &&
                CurrentScript(app).Execution.RepeatCount == 2 && CurrentScript(app).Hotkey == hotkey,
                "recording workspace restored stale execution settings or hotkey for its saved script");
            SetRecordedDelay(app, 0, 150);
            Until(() => catalog.Load(secondId) is { Steps: [{ DelayMs: 150 }] } && catalog.Load(secondId)!.Hotkey == hotkey,
                "editing the recording overwrote its automatically saved execution hotkey");

            ActivateTarget(target, editor);
            PressHotkey(hotkey);
            Until(() => Text(app, "status-text") == "正在录制", "empty recording did not start");
            PressHotkey(hotkey);
            Until(() => Text(app, "status-text") == "就绪，等待操作", "empty recording did not finish");
            if (catalog.List().Scripts.Count != 2) throw new Exception("An empty recording created a saved script.");
            Console.WriteLine("PASS Windows UI: real foreground hotkeys, automatic save, independent recordings, paused finish, and empty recording; step input seeded via recording callback");

            Eval(app, "document.getElementById('nav-execution').click()");
            Until(() => EvalBool(app, "!document.getElementById('execution-view').hidden && !document.getElementById('sidebar-library').hidden && document.querySelectorAll('.saved-script').length===2"),
                "opening scripts did not show the execution workspace and every saved script");
            if (!EvalBool(app, "!document.getElementById('save-execution') && !document.getElementById('new-script')"))
                throw new Exception("Execution still exposes recording or save-settings controls.");
            OpenLibraryScript(app, catalog, firstId);
            if (!EvalBool(app, "document.querySelectorAll('#execution-steps .step').length===2 && !document.querySelector('#execution-steps input, #execution-steps button')"))
                throw new Exception("Execution must display every step read-only.");
            SetPlan(app, "Count", 3, 250);
            Until(() => catalog.Load(firstId) is { Execution.Mode: ExecutionMode.Count, Execution.RepeatCount: 3, Execution.IntervalMs: 250 },
                "execution mode did not automatically save");
            Post(app, "{action:'execution',mode:'Count',count:0,intervalMs:250}");
            if (catalog.Load(firstId)!.Execution.RepeatCount != 3) throw new Exception("Invalid repeat count changed the saved plan.");
            hotkey = ChooseHotkey(app, "execution-hotkey");
            Until(() => catalog.Load(firstId)!.Hotkey == hotkey, "execution hotkey was not automatically saved");
            ActivateTarget(target, editor);
            PressHotkey(hotkey);
            Until(() => EvalBool(app, "document.querySelector('#execution-steps .step[aria-current=step] .step-number')?.textContent==='01'") &&
                Text(app, "execution-current-action").Contains("X"), "execution did not highlight and describe its first step");
            Until(() => EvalBool(app, "document.querySelector('#execution-steps .step[aria-current=step] .step-number')?.textContent==='02'") &&
                Text(app, "execution-current-action").Contains("Y"), "execution did not follow its second step");
            Until(() => Text(app, "execution-current-action").Contains("等待 250 毫秒"), "execution did not display the interval between rounds");
            Until(() => editor.Text == "XYXYXY", "count mode did not run three complete rounds", 12000);
            Until(() => Text(app, "status-text") == "就绪，等待操作", "count mode did not finish");
            if (!EvalBool(app, "!document.getElementById('execution-view').hidden"))
                throw new Exception("Starting execution returned to the recording workspace.");
            if (EvalBool(app, "!!document.querySelector('#execution-steps [aria-current=step]')"))
                throw new Exception("A finished execution retained an active-step highlight.");
            Console.WriteLine("PASS Windows UI: all saved scripts, automatic execution settings, actual three-round replay, and live read-only step highlights");

            SetPlan(app, "Continuous", 3, 250);
            ActivateTarget(target, editor);
            PressHotkey(hotkey);
            Until(() => editor.Text.Length >= 10, "continuous mode did not repeat", 10000);
            PressHotkey(hotkey);
            Until(() => Text(app, "status-text") == "就绪，等待操作", "hotkey did not stop continuous execution");
            var stoppedLength = editor.Text.Length;
            PumpFor(1400);
            if (editor.Text.Length != stoppedLength) throw new Exception("Continuous execution continued after stop.");
            Console.WriteLine("PASS Windows UI: continuous execution and hotkey stop");

            SetPlan(app, "Once", 3, 250);
            ActivateWindow(app);
            Eval(app, "document.getElementById('execution-start').click()");
            Until(() => Text(app, "execution-progress-label") == "等待目标窗口", "start button did not wait for the target window");
            ActivateTarget(target, editor);
            Until(() => editor.Text.Length == stoppedLength + 2, "once mode did not execute exactly once", 10000);
            Until(() => Text(app, "status-text") == "就绪，等待操作", "once mode did not finish");
            PumpFor(1400);
            if (editor.Text.Length != stoppedLength + 2 || !editor.Text.EndsWith("XY")) throw new Exception("Once mode repeated unexpectedly.");
            Console.WriteLine("PASS Windows UI: start button waits for target focus and executes exactly once");

            OpenLibraryScript(app, catalog, secondId);
            hotkey = catalog.Load(secondId)!.Hotkey;
            Until(() => Text(app, "execution-step-count") == "1 个步骤" && Text(app, "execution-steps").Contains("Z"),
                "selecting another saved script did not replace the execution steps");
            SetPlan(app, "Once", 2, 250);
            var beforeMismatch = editor.Text;
            var width = target.Width;
            target.Width += 100;
            ActivateTarget(target, editor);
            PressHotkey(hotkey);
            Until(() => Text(app, "execution-message").Contains("尺寸与脚本不符"), "mismatched target dimensions were not rejected");
            PumpFor(300);
            if (editor.Text != beforeMismatch) throw new Exception("Replay sent input to an incompatible target window.");
            target.Width = width;
            ActivateTarget(target, editor);
            PressHotkey(hotkey);
            Until(() => editor.Text == beforeMismatch + "Z", "selected saved script did not execute by foreground shortcut");
            Until(() => Text(app, "status-text") == "就绪，等待操作", "selected script did not finish");
            Eval(app, "document.getElementById('nav-editor').click()");
            Until(() => EvalBool(app, "!document.getElementById('workspace-view').hidden"), "recording navigation failed");
            if (EvalBool(app, "!document.getElementById('sidebar-library').hidden"))
                throw new Exception("Saved scripts remained visible in the recording workspace.");
            if (!EvalBool(app, "document.getElementById('run-button').hidden && document.getElementById('recording-progress').hidden"))
                throw new Exception("Removed execution-navigation or progress controls reappeared on return to recording.");
            Console.WriteLine("PASS Windows UI: library selection, shortcut target capture, target-size failure, and separate recording workspace");

            ActivateWindow(app);
            Eval(app, "document.getElementById('record-button').click()");
            Until(() => Text(app, "record-button").Contains("取消等待录制"), "record button did not wait for the target window");
            ActivateTarget(target, editor);
            Until(() => Text(app, "status-text") == "正在录制", "save-failure recording did not start");
            AddTestRecordedText(app, "R");
            var recoveryId = CurrentScript(app).Id;
            var blocker = Path.Combine(root, "scripts", recoveryId + ".json");
            Directory.CreateDirectory(blocker);
            PressHotkey(hotkey);
            Until(() => Text(app, "status-hint").Contains("保存失败"), "automatic-save failure was not shown");
            if (CurrentScript(app).Steps.Count != 1 || catalog.List().Scripts.Count != 2)
                throw new Exception("Automatic-save failure lost recorded steps or reported a saved file.");
            Post(app, "{action:'hotkey',value:'" + hotkey + "'}");
            if (!(bool)typeof(MainWindow).GetField("_dirty", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!)
                throw new Exception("A hotkey edit cleared the dirty state after automatic-save failure.");
            ActivateTarget(target, editor);
            PressHotkey(hotkey);
            PumpFor(150);
            if (CurrentScript(app).Id != recoveryId || Text(app, "status-text") != "就绪，等待操作")
                throw new Exception("Starting another recording discarded an unsaved draft after save failure.");
            Eval(app, "document.getElementById('nav-execution').click()");
            Until(() => EvalBool(app, "!document.getElementById('workspace-view').hidden"), "save failure did not keep the recording draft visible");
            Directory.Delete(blocker);
            Eval(app, "document.getElementById('nav-execution').click()");
            Until(() => catalog.List().Scripts.Count == 3 && EvalBool(app, "!document.getElementById('execution-view').hidden"),
                "retry after save failure did not persist the draft and open execution");
            Console.WriteLine("PASS Windows UI: automatic-save failure retains the draft and navigation retries saving");

            Eval(app, "document.getElementById('nav-editor').click()");
            Until(() => EvalBool(app, "!document.getElementById('workspace-view').hidden") && CurrentScript(app).Id == recoveryId,
                "recovered recording draft did not reopen");
            var temporaryBlocker = Path.Combine(root, "scripts", recoveryId + ".json.tmp");
            Directory.CreateDirectory(temporaryBlocker);
            Eval(app, "document.querySelector('#steps .step-delete[title=\"删除步骤\"]').click()");
            Until(() => CurrentScript(app).Steps.Count == 0 && Text(app, "status-hint").Contains("保存失败"),
                "deleting the final step did not expose the save failure");
            Eval(app, "document.getElementById('nav-execution').click()");
            Until(() => EvalBool(app, "!document.getElementById('workspace-view').hidden"),
                "navigation discarded an unsaved final-step deletion");
            if (catalog.Load(recoveryId)!.Steps.Count != 1)
                throw new Exception("A failed final-step deletion unexpectedly altered the saved file.");
            Directory.Delete(temporaryBlocker);
            Eval(app, "document.getElementById('nav-execution').click()");
            Until(() => catalog.Load(recoveryId)!.Steps.Count == 0 &&
                EvalBool(app, "!document.getElementById('execution-view').hidden && document.getElementById('execution-start').disabled"),
                "retry did not save the final-step deletion and disable empty-script playback");
            Console.WriteLine("PASS Windows UI: failed deletion of the last step remains recoverable and empty saved scripts cannot run");
        }
        finally
        {
            _keyboardTarget = 0;
            app.Close();
            target.Close();
            application.Shutdown();
            var resolved = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath());
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unexpected test data path.");
            try { if (Directory.Exists(resolved)) Directory.Delete(resolved, true); }
            catch (IOException) { /* WebView2 can retain its profile briefly after closing. */ }
        }
    }

    private static string ChooseHotkey(MainWindow app, string control = "hotkey-select", string? exclude = null)
    {
        foreach (var key in new[] { "F11", "F9", "F8", "F10" }.Where(key => key != exclude))
        {
            Eval(app, "document.getElementById('" + control + "').value='" + key + "';document.getElementById('" + control + "').dispatchEvent(new Event('change'))");
            PumpFor(80);
            if (EvalString(app, "document.getElementById('" + control + "').value") == key &&
                !Text(app, "status-hint").Contains("占用")) return key;
        }
        throw new Exception("No global shortcut could be registered for the desktop test.");
    }

    private static ScriptDocument CurrentScript(MainWindow app) =>
        (ScriptDocument)typeof(MainWindow).GetField("_script", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;

    private static void AddTestRecordedText(MainWindow app, string text)
    {
        // SendInput keyboard events are deliberately excluded by the physical-input recorder.
        // Seed deterministic steps through its recording callback while the real window is focused
        // and real hooks are installed; hotkeys, focus transitions, storage, UI, and replay use Windows.
        typeof(MainWindow).GetMethod("AddRecordedStep", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(app, [new ScriptStep { Type = StepType.Text, Text = text }, Stopwatch.GetTimestamp()]);
    }

    private static void SetRecordedDelay(MainWindow app, int index, int milliseconds)
    {
        Eval(app, "(()=>{const input=document.querySelectorAll('#steps input.delay')[" + index + "];input.value='" + milliseconds + "';input.dispatchEvent(new Event('change'));})()");
        Until(() => CurrentScript(app).Steps[index].DelayMs == milliseconds, "recorded step delay did not update");
    }

    private static void OpenLibraryScript(MainWindow app, ScriptCatalog catalog, string id)
    {
        var scripts = catalog.List().Scripts;
        var index = scripts.ToList().FindIndex(script => script.Id == id);
        if (index < 0) throw new Exception("Expected script missing from the catalog.");
        Eval(app, "document.querySelectorAll('.saved-script')[" + index + "].click()");
        Until(() => CurrentScript(app).Id == id && Text(app, "execution-step-count") == scripts[index].Steps.Count + " 个步骤",
            "saved script did not open in the execution workspace");
    }

    private static void SetPlan(MainWindow app, string mode, int count, int interval)
    {
        Eval(app, "document.getElementById('repeat-count').value='" + count + "';" +
            "document.getElementById('repeat-interval').value='" + interval + "';" +
            "document.querySelectorAll('input[name=execution-mode]').forEach(input=>input.checked=false);" +
            "document.getElementById('mode-" + mode.ToLowerInvariant() + "').checked=true;" +
            "document.getElementById('mode-" + mode.ToLowerInvariant() + "').dispatchEvent(new Event('change'))");
        Until(() => EvalBool(app, "document.getElementById('mode-" + mode.ToLowerInvariant() + "').checked && " +
            "document.getElementById('repeat-count').value==='" + count + "' && " +
            "document.getElementById('repeat-interval').value==='" + interval + "'"), "execution mode did not update");
    }

    private static void ActivateTarget(Window target, TextBox editor, [CallerLineNumber] int callerLine = 0)
    {
        ActivateWindow(target, callerLine);
        editor.Focus();
    }

    private static void WaitForInitialTargetFocus(Window target)
    {
        Console.WriteLine("WAIT Windows UI: click the input area in '" + target.Title + "' within 45 seconds to allow the desktop test to begin.");
        var handle = new WindowInteropHelper(target).Handle;
        var watch = Stopwatch.StartNew();
        target.Topmost = true;
        try
        {
            target.Activate();
            Until(() => Native.GetForegroundWindow() == handle || watch.ElapsedMilliseconds >= 45000,
                "initial manual-focus wait did not finish", 47000);
            if (Native.GetForegroundWindow() != handle)
                throw new DesktopUnavailableException("initial manual focus timed out after 45 seconds; no test input was sent");
        }
        finally { target.Topmost = false; }
    }

    private static void ActivateWindow(Window window, [CallerLineNumber] int callerLine = 0)
    {
        var handle = new WindowInteropHelper(window).Handle;
        Native.GetWindowThreadProcessId(handle, out var processId);
        if (handle == 0 || processId != Environment.ProcessId)
            throw new InvalidOperationException("Only windows owned by this test may be activated.");
        window.Activate();
        if (Native.GetForegroundWindow() != handle)
        {
            // A background test runner may not initially have foreground permission. Briefly
            // share the current foreground input queue while activating our own test window;
            // never send a key/click to an unrelated window, and always detach before pumping.
            var currentThread = GetCurrentThreadId();
            var foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
            var attached = foregroundThread != 0 && currentThread != foregroundThread &&
                AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                BringWindowToTop(handle);
                SetForegroundWindow(handle);
            }
            finally
            {
                if (attached) AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
        var watch = Stopwatch.StartNew();
        Until(() => Native.GetForegroundWindow() == handle || watch.ElapsedMilliseconds >= 1000,
            "foreground check did not return", 2000);
        if (Native.GetForegroundWindow() != handle)
            throw new DesktopUnavailableException($"test-owned window '{window.Title}' could not obtain foreground focus at DesktopE2E.cs:{callerLine}");
    }

    private static void PressHotkey(string key, [CallerLineNumber] int callerLine = 0)
    {
        if (_keyboardTarget == 0 || Native.GetForegroundWindow() != _keyboardTarget)
            throw new DesktopUnavailableException($"test target lost focus before shortcut at DesktopE2E.cs:{callerLine}; no input was sent");
        var code = (ushort)(0x70 + int.Parse(key[1..]) - 1);
        Native.SendKey(code);
        Native.SendKey(code, true);
    }

    private static void Post(MainWindow app, string body)
    {
        Eval(app, "window.chrome.webview.postMessage(" + body + ");true");
        PumpFor(70);
    }

    private static string Text(MainWindow app, string id) => EvalString(app, "document.getElementById('" + id + "').textContent");
    private static bool EvalBool(MainWindow app, string script) => Eval(app, script) == "true";
    private static string EvalString(MainWindow app, string script) => JsonSerializer.Deserialize<string>(Eval(app, script)) ?? "";
    private static string Eval(MainWindow app, string script)
    {
        var operation = app.Browser.CoreWebView2.ExecuteScriptAsync(script);
        Until(() => operation.IsCompleted, "browser script timed out");
        return operation.GetAwaiter().GetResult();
    }

    private static void PumpFor(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        Until(() => watch.ElapsedMilliseconds >= milliseconds, "dispatcher timer did not advance", milliseconds + 3000);
    }

    private static void Until(Func<bool> condition, string error, int timeout = 8000)
    {
        var watch = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (condition() || watch.ElapsedMilliseconds >= timeout) frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        if (!condition()) throw new TimeoutException(error);
    }
}
