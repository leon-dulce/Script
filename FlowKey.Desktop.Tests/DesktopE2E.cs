using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Native.Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Native.Point point);

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
        var keyEvents = new List<(Key Key, bool Up)>();
        editor.PreviewKeyDown += (_, e) => { keyEvents.Add((ActualKey(e), false)); if (ActualKey(e) is Key.LeftAlt or Key.RightAlt) e.Handled = true; };
        editor.PreviewKeyUp += (_, e) => { keyEvents.Add((ActualKey(e), true)); if (ActualKey(e) is Key.LeftAlt or Key.RightAlt) e.Handled = true; };
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
            AcquireInitialTargetFocus(target, editor);
            var hotkey = ChooseHotkey(app);
            VerifyShortcutConflictRecovery(app, hotkey);
            VerifyCrossProcessRecording(app);
            VerifyQAndAltReplay(target, editor);
            var catalog = new ScriptCatalog(Path.Combine(root, "scripts"));
            if (EvalBool(app, "!document.getElementById('sidebar-library').hidden"))
                throw new Exception("Saved scripts must be hidden in the recording workspace.");
            if (!EvalBool(app, "['load-button','save-button','new-editor-script','window-select','recording-target-config','recording-window-card','add-text','finish-button'].every(id=>!document.getElementById(id)||document.getElementById(id).hidden) && document.getElementById('run-button').hidden && document.getElementById('recording-progress').hidden"))
                throw new Exception("Recording still exposes removed manual recording, save, reload, window-picker, execution-navigation, or progress controls.");
            if (!EvalBool(app, "!document.getElementById('record-button').hidden && !document.getElementById('record-button').disabled"))
                throw new Exception("Start recording button unavailable.");
            Eval(app, "document.getElementById('nav-execution').click()");
            Until(() => EvalBool(app, "!document.getElementById('execution-view').hidden && document.getElementById('execution-start').disabled"),
                "empty execution library must disable playback");
            Eval(app, "document.getElementById('nav-editor').click()");
            Until(() => EvalBool(app, "!document.getElementById('workspace-view').hidden"), "recording workspace did not open");

            ActivateWindow(app);
            Eval(app, "document.getElementById('record-button').click()");
            Until(() => Text(app, "status-text") == "正在录制", "button did not start global recording");
            ActivateTarget(target, editor);
            AddTestRecordedText(app, "X");
            AddTestRecordedText(app, "Y");
            var firstId = CurrentScript(app).Id;
            PressHotkey(hotkey);
            WaitForNaming(app);
            Until(() => Native.GetForegroundWindow() == new WindowInteropHelper(app).Handle,
                "finishing recording did not bring the naming dialog to the foreground");
            if (catalog.List().Scripts.Count != 0) throw new Exception("Recording was saved before its name was confirmed.");
            app.Close();
            PumpFor(100);
            if (!app.IsVisible || !EvalBool(app, "document.getElementById('recording-name-dialog').open") ||
                CurrentScript(app).Id != firstId || catalog.List().Scripts.Count != 0)
                throw new Exception("Closing the app discarded or persisted a recording whose name was not confirmed.");
            ConfirmRecordingName(app, "   ");
            PumpFor(150);
            if (!EvalBool(app, "document.getElementById('recording-name-dialog').open") ||
                catalog.List().Scripts.Count != 0 || CurrentScript(app).Id != firstId)
                throw new Exception("Blank naming dismissed the dialog or lost the recording.");
            Post(app, "{action:'saveRecording',name:''}");
            if (catalog.List().Scripts.Count != 0 || CurrentScript(app).Steps.Count != 2)
                throw new Exception("Invalid recording name was accepted by the host or discarded its steps.");
            ConfirmRecordingName(app, "E2E XY");
            WaitForNamedSave(app, catalog, firstId, "E2E XY", 1);
            VerifyWindowFlowSettings(app, target, editor);
            VerifyAutomaticCrossProcessPlayback(app);
            Eval(app, "document.getElementById('nav-editor').click()");
            Until(() => EvalBool(app, "!document.getElementById('workspace-view').hidden"), "saved recording did not reopen for editing");
            SetRecordedDelay(app, 0, 400);
            SetRecordedDelay(app, 1, 700);
            Until(() => catalog.Load(firstId)!.Steps.Select(s => s.DelayMs).SequenceEqual([400, 700]),
                "recording edits did not automatically persist");

            ActivateTarget(target, editor);
            PressHotkey(hotkey);
            Until(() => Text(app, "status-text") == "正在录制" && Text(app, "step-count") == "0 个步骤",
                "second recording did not begin a fresh script");
            PumpFor(100);
            SendTestKey(app, 0x5A);
            PumpFor(90);
            ActivateWindow(app);
            if (Text(app, "status-text") != "正在录制") throw new Exception("Switching to app paused recording.");
            SendTestKey(app, 0x5A);
            PumpFor(90);
            ActivateTarget(target, editor);
            target.Width += 30;
            PumpFor(150);
            SendTestKey(app, 0x5A, keyUp: true);
            var otherFunctionKey = ChooseReplayFunctionKey(app, hotkey);
            SendTestKey(app, otherFunctionKey);
            SendTestKey(app, otherFunctionKey, keyUp: true);
            SendTestKey(app, 0x41, flags: Native.InjectedKeyboard);
            SendTestKey(app, 0x41, keyUp: true, flags: Native.InjectedKeyboard);
            Until(() => CurrentScript(app).Steps.Count == 4, "keyboard capture did not suppress held-key repeats or excluded the wrong function key");
            if (!CurrentScript(app).Steps.Select(s => s.KeyAction).SequenceEqual([KeyAction.Down, KeyAction.Up, KeyAction.Down, KeyAction.Up]) ||
                CurrentScript(app).Steps[0].DelayMs < 80 || CurrentScript(app).Steps[1].DelayMs < 250)
                throw new Exception("Keyboard actions or their elapsed delays were not preserved.");
            Until(() => EvalBool(app, "document.querySelectorAll('#steps .step').length===4"),
                "the recording screen did not show every captured key event while recording");
            if (Text(app, "status-text") != "正在录制" ||
                EvalString(app, "Array.from(document.querySelectorAll('#steps .step-copy strong'),e=>e.textContent).join('|')") !=
                    "按下按键|松开按键|按下按键|松开按键" ||
                EvalString(app, "Array.from(document.querySelectorAll('#steps .step-interval'),e=>e.textContent).join('|')") !=
                    string.Join("|", CurrentScript(app).Steps.Select(s => $"间隔 {s.DelayMs} 毫秒")))
                throw new Exception("Live keyboard mismatch: " + Text(app, "status-text") + " labels=" + EvalString(app, "Array.from(document.querySelectorAll('#steps .step-copy strong'),e=>e.textContent).join('|')") + " intervals=" + EvalString(app, "Array.from(document.querySelectorAll('#steps .step-interval'),e=>e.textContent).join('|')") + " expected=" + string.Join("|", CurrentScript(app).Steps.Select(s => $"间隔 {s.DelayMs} 毫秒")));
            var secondId = CurrentScript(app).Id;
            ActivateWindow(app);
            PumpFor(150);
            if (Text(app, "status-text") != "正在录制") throw new Exception("Global recording paused on focus change.");
            PressShortcutInWindow(hotkey, new WindowInteropHelper(app).Handle);
            WaitForNaming(app);
            if (catalog.List().Scripts.Count != 1) throw new Exception("Paused recording saved without naming confirmation.");
            ConfirmRecordingName(app, "E2E keys");
            WaitForNamedSave(app, catalog, secondId, "E2E keys", 2);
            if (!catalog.Load(firstId)!.Steps.Select(s => s.Text).SequenceEqual(["X", "Y"]))
                throw new Exception("Starting a second recording overwrote the first script.");
            if (!catalog.Load(secondId)!.Steps.Select(s => s.Keys.Single()).SequenceEqual([0x5A, 0x5A, (int)otherFunctionKey, (int)otherFunctionKey]))
                throw new Exception("Saved keyboard events differ from the recording callbacks.");
            SetPlan(app, "Count", 2, 250);
            Until(() => catalog.Load(secondId)!.Execution.RepeatCount == 2, "second script execution settings did not save");
            hotkey = ChooseHotkey(app, "execution-hotkey", hotkey, "F" + (otherFunctionKey - 0x70 + 1));
            Until(() => catalog.Load(secondId)!.Hotkey == hotkey, "alternate execution hotkey did not save");
            Eval(app, "document.getElementById('nav-editor').click()");
            Until(() => EvalBool(app, "!document.getElementById('workspace-view').hidden") &&
                CurrentScript(app).Execution.RepeatCount == 2 && CurrentScript(app).Hotkey == hotkey,
                "recording workspace restored stale execution settings or hotkey for its saved script");
            Post(app, "{action:'text',value:'must not append to saved recording'}");
            if (CurrentScript(app).Steps.Count != 4) throw new Exception("Saved recording accepted a new text step.");
            SetRecordedDelay(app, 0, 150);
            Until(() => catalog.Load(secondId)!.Steps[0].DelayMs == 150 && catalog.Load(secondId)!.Hotkey == hotkey,
                "editing the recording overwrote its automatically saved execution hotkey");

            ActivateTarget(target, editor);
            PressHotkey(hotkey);
            Until(() => Text(app, "status-text") == "正在录制", "empty recording did not start");
            Eval(app, "document.getElementById('record-button').click()");
            Until(() => Text(app, "status-text") == "就绪，等待操作", "empty recording did not finish");
            if (catalog.List().Scripts.Count != 2 || EvalBool(app, "document.getElementById('recording-name-dialog').open"))
                throw new Exception("An empty recording created a saved script or requested a name.");
            if (!EvalBool(app, "document.getElementById('add-text').disabled"))
                throw new Exception("An empty unsaved recording enabled adding text outside the recording flow.");
            Post(app, "{action:'text',value:'must not append to empty unsaved recording'}");
            if (CurrentScript(app).Steps.Count != 0 || catalog.List().Scripts.Count != 2 ||
                EvalBool(app, "document.getElementById('recording-name-dialog').open"))
                throw new Exception("A text action appended to or saved an empty unsaved recording.");
            Console.WriteLine("PASS Windows UI: global button start, real hotkey start/finish, required naming, independent recordings, cross-window recording and finish, and empty recording; key events delivered through real Windows SendInput and the installed hook");

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
            editor.Clear();
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
            Until(() => Text(app, "execution-step-count") == "4 个步骤" && Text(app, "execution-steps").Contains("Z"),
                "selecting another saved script did not replace the execution steps");
            hotkey = catalog.Load(secondId)!.Hotkey;
            var conflictingHotkey = "F" + (otherFunctionKey - 0x70 + 1);
            Eval(app, "document.getElementById('execution-hotkey').value='" + conflictingHotkey +
                "';document.getElementById('execution-hotkey').dispatchEvent(new Event('change'))");
            Until(() => Text(app, "execution-message").Contains("已出现在脚本步骤"),
                "a shortcut already recorded in the script was not rejected");
            if (CurrentScript(app).Hotkey != hotkey || catalog.Load(secondId)!.Hotkey != hotkey ||
                EvalString(app, "document.getElementById('execution-hotkey').value") != hotkey)
                throw new Exception("Rejecting a shortcut collision changed the active or saved shortcut.");
            SetPlan(app, "Once", 2, 250);
            var functionKey = KeyInterop.KeyFromVirtualKey((int)otherFunctionKey);
            (Key Key, bool Up)[] RelevantKeys() => keyEvents.Where(e => e.Key == Key.Z || e.Key == functionKey).ToArray();
            keyEvents.Clear();
            ActivateTarget(target, editor);
            PressHotkey(hotkey);
            Until(() => RelevantKeys().Length >= 4 && Text(app, "status-text") == "就绪，等待操作",
                "recorded key actions did not replay as actual Windows key-down and key-up events");
            if (!RelevantKeys().SequenceEqual(new[] { (Key.Z, false), (Key.Z, true), (functionKey, false), (functionKey, true) }))
                throw new Exception("Windows key replay did not preserve the recorded down and up sequence.");

            // Extend only this isolated fixture's release delay so stop is exercised while Z
            // is held. Observe actual routed key events, independent of IME or Caps Lock text.
            var releaseDelay = CurrentScript(app).Steps[1].DelayMs;
            CurrentScript(app).Steps[1].DelayMs = 3000;
            keyEvents.Clear();
            ActivateTarget(target, editor);
            PressHotkey(hotkey);
            Until(() => RelevantKeys().Count(e => e.Key == Key.Z && !e.Up) == 1 &&
                !RelevantKeys().Any(e => e.Up), "held-key replay did not reach the delay before key release");
            PressHotkey(hotkey);
            Until(() => Text(app, "status-text") == "就绪，等待操作" && RelevantKeys().Any(e => e.Key == Key.Z && e.Up),
                "shortcut stop did not release the key held by replay");
            PumpFor(150);
            if (!RelevantKeys().SequenceEqual(new[] { (Key.Z, false), (Key.Z, true) }) ||
                (Native.GetAsyncKeyState(0x5A) & 0x8000) != 0)
                throw new Exception("Stopping held-key replay left a key pressed or executed subsequent steps.");
            CurrentScript(app).Steps[1].DelayMs = releaseDelay;
            Console.WriteLine("PASS Windows UI: recorded-shortcut conflict rejection preserves the working shortcut; actual key-down/up replay and held-key stop cleanup");

            OpenLibraryScript(app, catalog, firstId);
            hotkey = catalog.Load(firstId)!.Hotkey;
            SetPlan(app, "Once", 2, 250);
            var beforeMismatch = editor.Text;
            target.Width += 100;
            ActivateTarget(target, editor);
            PressHotkey(hotkey);
            Until(() => editor.Text == beforeMismatch + "XY", "global script could not replay in resized window");
            Until(() => Text(app, "status-text") == "就绪，等待操作", "selected script did not finish");
            if (!catalog.Load(firstId)!.GlobalKeyboardRecording || !catalog.Load(secondId)!.GlobalKeyboardRecording)
                throw new Exception("Global recording scope was not saved.");
            var current = CurrentScript(app);
            current.GlobalKeyboardRecording = false;
            var windowInfo = Native.ListWindows(new WindowInteropHelper(app).Handle).Single(w => w.Handle == _keyboardTarget);
            var preflight = typeof(MainWindow).GetMethod("RunPreflight", BindingFlags.Instance | BindingFlags.NonPublic)!;
            if (preflight.Invoke(app, [windowInfo]) is not string error || !error.Contains("尺寸与脚本不符"))
                throw new Exception("Legacy target-bound scripts lost dimension validation.");
            current.GlobalKeyboardRecording = true;
            Eval(app, "document.getElementById('nav-editor').click()");
            Until(() => EvalBool(app, "!document.getElementById('workspace-view').hidden"), "recording navigation failed");
            if (EvalBool(app, "!document.getElementById('sidebar-library').hidden"))
                throw new Exception("Saved scripts remained visible in the recording workspace.");
            if (!EvalBool(app, "document.getElementById('run-button').hidden && document.getElementById('recording-progress').hidden"))
                throw new Exception("Removed execution-navigation or progress controls reappeared on return to recording.");
            Console.WriteLine("PASS Windows UI: library selection, shortcut target capture, global resized-window replay and legacy size validation, and separate recording workspace");

            hotkey = CurrentScript(app).Hotkey;
            ActivateTarget(target, editor);
            PressHotkey(hotkey);
            Until(() => Text(app, "status-text") == "正在录制", "save-failure recording did not start");
            AddTestRecordedText(app, "R");
            var recoveryId = CurrentScript(app).Id;
            PressHotkey(hotkey);
            WaitForNaming(app);
            var blocker = Path.Combine(root, "scripts", recoveryId + ".json");
            Directory.CreateDirectory(blocker);
            ConfirmRecordingName(app, "E2E recovered");
            Until(() => Text(app, "status-hint").Contains("保存失败"), "named recording save failure was not shown");
            if (CurrentScript(app).Steps.Count != 1 || catalog.List().Scripts.Count != 2 ||
                !EvalBool(app, "document.getElementById('recording-name-dialog').open"))
                throw new Exception("Naming-save failure lost recorded steps, closed its dialog, or reported a saved file.");
            ActivateTarget(target, editor);
            PressHotkey(hotkey);
            PumpFor(150);
            if (CurrentScript(app).Id != recoveryId || !EvalBool(app, "document.getElementById('recording-name-dialog').open"))
                throw new Exception("A shortcut discarded the pending named recording after save failure.");
            Post(app, "{action:'workspace',value:'execution'}");
            if (!EvalBool(app, "!document.getElementById('workspace-view').hidden && document.getElementById('recording-name-dialog').open"))
                throw new Exception("Navigation discarded a recording awaiting a successful named save.");
            Directory.Delete(blocker);
            ConfirmRecordingName(app, "E2E recovered");
            WaitForNamedSave(app, catalog, recoveryId, "E2E recovered", 3);
            Console.WriteLine("PASS Windows UI: failed named save retains the recording and dialog; confirm retry saves the same ID and opens execution");

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

            Eval(app, "document.getElementById('nav-editor').click()");
            Until(() => EvalBool(app, "!document.getElementById('workspace-view').hidden"), "discard-test recording workspace did not open");
            hotkey = CurrentScript(app).Hotkey;
            ActivateTarget(target, editor);
            PressHotkey(hotkey);
            Until(() => Text(app, "status-text") == "正在录制", "discard-test recording did not start");
            AddTestRecordedText(app, "D");
            PressHotkey(hotkey);
            WaitForNaming(app);
            Eval(app, "window.confirm=()=>true;document.getElementById('recording-name-discard').click()");
            Until(() => EvalBool(app, "!document.getElementById('recording-name-dialog').open"), "explicit discard did not dismiss the naming dialog");
            if (catalog.List().Scripts.Count != 3) throw new Exception("Discarded recording was saved unexpectedly.");
            Console.WriteLine("PASS Windows UI: explicit naming discard leaves the saved library unchanged");

            Eval(app, "document.getElementById('nav-execution').click()");
            Until(() => EvalBool(app, "!document.getElementById('execution-view').hidden"), "close-test execution workspace did not open");
            OpenLibraryScript(app, catalog, secondId);
            hotkey = catalog.Load(secondId)!.Hotkey;
            SetPlan(app, "Once", 2, 250);
            CurrentScript(app).Steps[1].DelayMs = 3000;
            keyEvents.Clear();
            ActivateTarget(target, editor);
            PressHotkey(hotkey);
            Until(() => RelevantKeys().Count(e => e.Key == Key.Z && !e.Up) == 1 && !RelevantKeys().Any(e => e.Up),
                "close-test replay did not hold the key before release");
            var playbackKeyboard = typeof(MainWindow).GetField("_playbackKeyboard", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;
            var heldKeys = (ICollection<ushort>)typeof(KeyboardPlayback).GetField("_held", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(playbackKeyboard)!;
            if (!heldKeys.Contains(0x5A)) throw new Exception("Close test did not begin with a tracked pressed key.");
            app.Close();
            // Assert before pumping the dispatcher: OnClosed must release tracked keys
            // synchronously, even when an async playback continuation cannot run yet.
            if (heldKeys.Count != 0) throw new Exception("Closing the app did not synchronously release its tracked playback keys.");
            Until(() => !app.IsVisible && RelevantKeys().Any(e => e.Key == Key.Z && e.Up) &&
                (Native.GetAsyncKeyState(0x5A) & 0x8000) == 0, "closing the app left its playback key held in Windows");
            if (!RelevantKeys().SequenceEqual(new[] { (Key.Z, false), (Key.Z, true) }))
                throw new Exception("Closing playback emitted extra key events or ran subsequent steps.");
            Console.WriteLine("PASS Windows UI: closing during held-key replay releases keys synchronously and delivers Windows key-up");
        }
        catch (Exception error)
        {
            Console.WriteLine("FAIL Windows UI: " + error);
            throw;
        }
        finally
        {
            _keyboardTarget = 0;
            typeof(MainWindow).GetMethod("StopHooks", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, null);
            var session = (SessionState)typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;
            session.FinishRecording();
            typeof(MainWindow).GetField("_namingRequired", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, false);
            if (app.IsVisible) app.Close();
            if (target.IsVisible) target.Close();
            application.Shutdown();
            var resolved = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath());
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unexpected test data path.");
            try { if (Directory.Exists(resolved)) Directory.Delete(resolved, true); }
            catch (IOException) { /* WebView2 can retain its profile briefly after closing. */ }
        }
    }

    private static void VerifyCrossProcessRecording(MainWindow app)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(DesktopE2E).Assembly.Location);
        start.ArgumentList.Add("--keyboard-target");
        using var child = Process.Start(start) ?? throw new Exception("Cannot launch isolated keyboard target.");
        try
        {
            var ready = child.StandardOutput.ReadLineAsync();
            Until(() => ready.IsCompleted, "separate target window did not open");
            var handle = nint.Parse(ready.GetAwaiter().GetResult()!);
            Native.GetWindowThreadProcessId(handle, out var processId);
            if (processId != child.Id) throw new Exception("Target handle does not belong to the test child.");
            ActivateWindow(app);
            Eval(app, "document.getElementById('record-button').click()");
            Until(() => Text(app, "status-text") == "正在录制", "button did not install capture");
            var capture = (KeyboardCapture)typeof(MainWindow).GetField("_keyboardCapture", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;
            app.WindowState = WindowState.Minimized;
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
            Until(() => Native.GetForegroundWindow() == handle, "separate process could not obtain focus");
            var sending = Task.Run(() =>
            {
                Thread.Sleep(100);
                foreach (var key in new ushort[] { 65, 66 })
                {
                    if (Native.GetForegroundWindow() != handle) throw new Exception("Separate target lost focus; no input sent.");
                    foreach (var up in new[] { false, true })
                    {
                        var input = Native.CreateKeyInput(key, up);
                        input.Key.ExtraInfo = 0;
                        if (Native.SendInput(1, [input], Marshal.SizeOf<Native.Input>()) != 1) throw new Exception("Test input rejected.");
                        Thread.Sleep(60);
                    }
                }
            });
            // Intentionally stall the UI longer than Windows' hook timeout. Capture must remain alive.
            Thread.Sleep(1300);
            if (!sending.IsCompletedSuccessfully) throw new Exception("Input depended on the blocked UI thread.", sending.Exception);
            Until(() => CurrentScript(app).Steps.Count == 4 && EvalBool(app, "document.querySelectorAll('#steps .step').length===4"),
                "real cross-process events were not captured and displayed while minimized");
            if (!CurrentScript(app).Steps.Select(step => step.Keys.Single()).SequenceEqual([65, 65, 66, 66]) ||
                !CurrentScript(app).Steps.Select(step => step.KeyAction).SequenceEqual([KeyAction.Down, KeyAction.Up, KeyAction.Down, KeyAction.Up]) ||
                CurrentScript(app).Steps.Skip(1).Any(step => step.DelayMs < 40))
                throw new Exception("Cross-process keyboard events or original delays were lost.");
            app.WindowState = WindowState.Normal;
            ActivateWindow(app);
            Eval(app, "document.getElementById('record-button').click()");
            WaitForNaming(app);
            if (capture.IsAlive) throw new Exception("Stopped recording left the capture thread alive.");
            Post(app, "{action:'discardRecording'}");
            Until(() => CurrentScript(app).Steps.Count == 0, "isolated test recording was not discarded");
            Console.WriteLine("PASS Windows UI: start button, separate-process real keyboard input, minimized and blocked UI, live events/delays, and capture thread cleanup");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.CloseMainWindow();
                if (!child.WaitForExit(2000)) child.Kill();
            }
            app.WindowState = WindowState.Normal;
        }
    }

    private static string ChooseHotkey(MainWindow app, string control = "hotkey-select", string? exclude = null, string? alsoExclude = null)
    {
        foreach (var key in new[] { "F11", "F9", "F8", "F10" }.Where(key => key != exclude && key != alsoExclude))
        {
            Eval(app, "document.getElementById('" + control + "').value='" + key + "';document.getElementById('" + control + "').dispatchEvent(new Event('change'))");
            PumpFor(80);
            if (EvalString(app, "document.getElementById('" + control + "').value") == key &&
                !Text(app, "status-hint").Contains("占用")) return key;
        }
        throw new Exception("No global shortcut could be registered for the desktop test.");
    }

    private static uint ChooseReplayFunctionKey(MainWindow app, string controlKey)
    {
        var handle = new WindowInteropHelper(app).Handle;
        foreach (var function in new[] { 8, 9, 10, 11 })
        {
            if (controlKey == "F" + function) continue;
            var code = (uint)(0x70 + function - 1);
            if (!Native.RegisterHotKey(handle, 912, 0x4000, code)) continue;
            Native.UnregisterHotKey(handle, 912);
            return code;
        }
        throw new DesktopUnavailableException("no unused non-control function key was available for the playback fixture");
    }

    private static void VerifyShortcutConflictRecovery(MainWindow app, string originalHotkey)
    {
        Native.GetWindowThreadProcessId(_keyboardTarget, out var processId);
        if (_keyboardTarget == 0 || processId != Environment.ProcessId)
            throw new InvalidOperationException("Shortcut-conflict reservation requires the test-owned target window.");
        var occupiedCode = ChooseReplayFunctionKey(app, originalHotkey);
        var occupiedKey = "F" + (occupiedCode - 0x70 + 1);
        if (!Native.RegisterHotKey(_keyboardTarget, 913, 0x4000, occupiedCode))
            throw new DesktopUnavailableException("the test shortcut could not be reserved for the conflict-recovery check");
        try
        {
            Post(app, "{action:'hotkey',value:'" + occupiedKey + "'}");
            if (!Text(app, "status-hint").Contains("占用") || CurrentScript(app).Hotkey != originalHotkey ||
                EvalString(app, "document.getElementById('hotkey-select').value") != originalHotkey)
                throw new Exception("An occupied shortcut did not preserve the previous shortcut and explain the conflict.");
        }
        finally { Native.UnregisterHotKey(_keyboardTarget, 913); }
        Post(app, "{action:'hotkey',value:'" + originalHotkey + "'}");
        if (CurrentScript(app).Hotkey != originalHotkey || Text(app, "status-hint").Contains("占用"))
            throw new Exception("A successful shortcut selection retained the previous occupied-shortcut warning.");
        Console.WriteLine("PASS Windows UI: occupied shortcut preserves the current key and successful selection clears its warning");
    }

    private static Key ActualKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key
    };

    private static ScriptDocument CurrentScript(MainWindow app) =>
        (ScriptDocument)typeof(MainWindow).GetField("_script", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;

    private static void WaitForNaming(MainWindow app) => Until(() =>
        Text(app, "status-text").Contains("命名") && EvalBool(app, "document.getElementById('recording-name-dialog').open"),
        "finishing a nonempty recording did not open its naming dialog");

    private static void ConfirmRecordingName(MainWindow app, string name) => Eval(app,
        "document.getElementById('recording-name-input').value=" + JsonSerializer.Serialize(name) +
        ";document.getElementById('recording-name-input').dispatchEvent(new Event('input'));document.getElementById('recording-name-confirm').click()");

    private static void WaitForNamedSave(MainWindow app, ScriptCatalog catalog, string id, string name, int scriptCount) => Until(() =>
        catalog.List().Scripts.Count == scriptCount && catalog.Load(id)?.Name == name && CurrentScript(app).Id == id &&
        EvalBool(app, "!document.getElementById('recording-name-dialog').open && !document.getElementById('execution-view').hidden"),
        "confirming a recording name did not save that draft and show it in execution");

    private static void VerifyWindowFlowSettings(MainWindow app, Window target, TextBox editor)
    {
        var id = new WindowInteropHelper(target).Handle.ToString();
        foreach (var automatic in new[] { false, true })
        foreach (var back in new[] { false, true })
        {
            Eval(app, "document.getElementById('nav-settings').click()");
            Until(() => EvalBool(app, "!document.getElementById('settings-view').hidden"), "settings page did not open");
            Post(app, "{action:'flowSettings',autoSwitch:" + automatic.ToString().ToLowerInvariant() +
                ",returnToApp:" + back.ToString().ToLowerInvariant() + ",targetId:'" + id + "'}");
            Until(() => EvalBool(app, "document.getElementById('flow-auto').checked===" + automatic.ToString().ToLowerInvariant() +
                " && document.getElementById('flow-return').checked===" + back.ToString().ToLowerInvariant()), "flow settings not confirmed");
            Eval(app, "document.getElementById('nav-execution').click()");
            Until(() => EvalBool(app, "!document.getElementById('execution-view').hidden"), "execution page did not reopen");
            editor.Clear();
            ActivateWindow(app);
            Eval(app, "document.getElementById('execution-start').click()");
            if (!automatic)
            {
                Until(() => EvalBool(app, "document.getElementById('execution-progress-label').textContent==='等待目标窗口'"), "manual flow did not wait");
                if (editor.Text.Length != 0 || Native.GetForegroundWindow() != new WindowInteropHelper(app).Handle)
                    throw new Exception("Manual flow switched or sent keys before the user switched.");
                ActivateTarget(target, editor);
            }
            try { Until(() => editor.Text == "XY" && Text(app, "status-text") == "就绪，等待操作", "flow did not finish playback", 10000); }
            catch (TimeoutException) { throw new Exception($"Flow auto={automatic} return={back}: text={editor.Text}, status={Text(app, "status-text")}, message={Text(app, "execution-message")}"); }
            var expected = back ? new WindowInteropHelper(app).Handle : new WindowInteropHelper(target).Handle;
            Until(() => Native.GetForegroundWindow() == expected, "flow completion left the wrong foreground window");
        }
        var originalDelay = CurrentScript(app).Steps[0].DelayMs;
        CurrentScript(app).Steps[0].DelayMs = 3000;
        ActivateWindow(app);
        editor.Clear();
        Eval(app, "document.getElementById('execution-start').click()");
        Until(() => Text(app, "status-text") == "正在执行脚本", "return-on-stop test did not start");
        Eval(app, "document.getElementById('execution-start').click()");
        Until(() => Native.GetForegroundWindow() == new WindowInteropHelper(app).Handle && Text(app, "status-text") == "就绪，等待操作",
            "manual stop did not return to FlowKey");
        if (editor.Text.Length != 0) throw new Exception("Stopped script continued sending keys.");
        CurrentScript(app).Steps[0].DelayMs = originalDelay;
        Eval(app, "document.getElementById('nav-settings').click()");
        Until(() => EvalBool(app, "!document.getElementById('settings-view').hidden"), "settings page unavailable");
        var closedTarget = new Window { Title = "FlowKey closed flow target", Width = 200, Height = 100 };
        closedTarget.Show();
        Post(app, "{action:'flowSettings',autoSwitch:true,returnToApp:false,targetId:'" + new WindowInteropHelper(closedTarget).Handle + "'}");
        closedTarget.Close();
        Eval(app, "document.getElementById('nav-execution').click()");
        ActivateWindow(app);
        editor.Clear();
        Eval(app, "document.getElementById('execution-start').click()");
        Until(() => Text(app, "execution-message").Contains("未找到指定窗口") && Text(app, "execution-message").Contains("FlowKey closed flow target"), "closed automatic target did not identify the missing window");
        if (editor.Text.Length != 0 || Text(app, "status-text") != "就绪，等待操作") throw new Exception("Closed target started execution.");
        Eval(app, "document.getElementById('nav-settings').click()");
        Post(app, "{action:'flowSettings',autoSwitch:false,returnToApp:false,targetId:''}");
        Eval(app, "document.getElementById('nav-execution').click()");
        Console.WriteLine("PASS Windows settings page and four start/end flows; closed automatic target never sends keys");
    }

    private static void VerifyAutomaticCrossProcessPlayback(MainWindow app)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(DesktopE2E).Assembly.Location);
        start.ArgumentList.Add("--keyboard-target");
        using var child = Process.Start(start) ?? throw new Exception("Cannot launch separate playback target.");
        try
        {
            var opened = child.StandardOutput.ReadLineAsync();
            Until(() => opened.IsCompleted, "separate playback target did not open");
            var handle = nint.Parse(opened.GetAwaiter().GetResult()!);
            Native.GetWindowThreadProcessId(handle, out var owner);
            if (owner != child.Id) throw new Exception("Playback target identity mismatch.");
            ActivateWindow(app);
            Eval(app, "document.getElementById('nav-settings').click()");
            Post(app, "{action:'flowSettings',autoSwitch:true,returnToApp:true,targetId:'" + handle + "'}");
            Eval(app, "document.getElementById('nav-execution').click()");
            var received = Task.Run(async () =>
            {
                while (await child.StandardOutput.ReadLineAsync() is { } line)
                    if (line == "TEXT:XY") return true;
                return false;
            });
            Native.ShowWindow(handle, 6); // The configured target can be minimized when Start is clicked.
            ActivateWindow(app);
            Eval(app, "document.getElementById('execution-start').click()");
            Until(() => EvalBool(app, "document.getElementById('execution-help').textContent.includes('正在自动切换')"),
                "automatic start did not expose cancellable activation wait");
            Eval(app, "document.getElementById('execution-start').click()");
            PumpFor(350);
            if (received.IsCompleted || Text(app, "status-text") != "就绪，等待操作")
                throw new Exception("Cancelled automatic activation still executed the script.");
            ActivateWindow(app);
            Eval(app, "document.getElementById('execution-start').click()");
            Until(() => received.IsCompleted && received.GetAwaiter().GetResult() &&
                Text(app, "status-text") == "就绪，等待操作" && Native.GetForegroundWindow() == new WindowInteropHelper(app).Handle,
                "automatic switch did not deliver script to the separate process and return", 15000);
            Eval(app, "document.getElementById('nav-settings').click()");
            Post(app, "{action:'flowSettings',autoSwitch:false,returnToApp:false,targetId:''}");
            Eval(app, "document.getElementById('nav-execution').click()");
            Console.WriteLine("PASS automatic cross-process window activation, actual script delivery and return to FlowKey");
        }
        finally
        {
            if (!child.HasExited) { child.CloseMainWindow(); if (!child.WaitForExit(2000)) child.Kill(); }
        }
    }

    private static void VerifyQAndAltReplay(Window target, TextBox editor)
    {
        ActivateTarget(target, editor);
        var observed = new List<(uint Key, uint Scan, bool Up)>();
        Native.HookCallback hookCallback = (code, message, pointer) =>
        {
            if (code >= 0)
            {
                var data = Marshal.PtrToStructure<Native.KeyboardData>(pointer);
                if (data.ExtraInfo == Native.ReplayInputTag)
                    observed.Add((data.VkCode, data.ScanCode, message is Native.KeyUp or Native.SysKeyUp));
            }
            return Native.CallNextHookEx(0, code, message, pointer);
        };
        var hook = Native.SetWindowsHookEx(Native.KeyboardHook, hookCallback, Native.GetModuleHandle(null), 0);
        if (hook == 0) throw new Exception("Scan-code observation hook unavailable.");
        var layout = Native.GetKeyboardLayout(Native.GetWindowThreadProcessId(_keyboardTarget, out _));
        var keyboard = new KeyboardPlayback((key, up) => Native.SendKey(key, up, layout: layout));
        try
        {
            foreach (var key in new ushort[] { 0x51, 0xA4, 0xA5 })
            {
                if (Native.GetForegroundWindow() != _keyboardTarget) throw new DesktopUnavailableException("Scan-code test target lost focus.");
                keyboard.Execute(new ScriptStep { Type = StepType.Key, Keys = [key], KeyAction = KeyAction.Down });
                PumpFor(130);
                if ((Native.GetAsyncKeyState(key) & 0x8000) == 0) throw new Exception($"Windows did not hold key {key:X2}.");
                keyboard.Execute(new ScriptStep { Type = StepType.Key, Keys = [key], KeyAction = KeyAction.Up });
                PumpFor(80);
                if ((Native.GetAsyncKeyState(key) & 0x8000) != 0) throw new Exception($"Windows did not release key {key:X2}.");
            }
            foreach (var pair in new (uint Key, uint Scan)[] { (0x51, 0x10), (0xA4, 0x38), (0xA5, 0x38) })
            {
                var events = observed.Where(e => e.Key == pair.Key).ToArray();
                if (events.Length != 2 || events[0] != (pair.Key, pair.Scan, false) || events[1] != (pair.Key, pair.Scan, true))
                    throw new Exception("Windows did not receive the expected Q/Alt scan-code transitions.");
            }
            Console.WriteLine("PASS Windows Q and left/right Alt scan-code delivery, held state and release");
        }
        finally { keyboard.ReleaseAll(); Native.UnhookWindowsHookEx(hook); GC.KeepAlive(hookCallback); }
    }

    private static void SendTestKey(MainWindow app, uint key, bool keyUp = false, uint flags = 0)
    {
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var foregroundProcess);
        if (foregroundProcess != Environment.ProcessId)
            throw new DesktopUnavailableException("test-owned window lost focus before the test keyboard input");
        // Real Windows input reaches the installed hook; no callback is invoked directly.
        var input = Native.CreateKeyInput((ushort)key, keyUp);
        if (flags == 0) input.Key.ExtraInfo = 0; // Input from an external keyboard provider.
        if (Native.SendInput(1, [input], Marshal.SizeOf<Native.Input>()) != 1)
            throw new Exception("Windows rejected the test keyboard input.");
        PumpFor(40);
    }

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

    private static void AcquireInitialTargetFocus(Window target, TextBox editor)
    {
        try { ActivateTarget(target, editor); return; }
        catch (DesktopUnavailableException)
        {
            if (TryClickInitialTarget(target, editor)) { ActivateTarget(target, editor); return; }
            if (Environment.GetEnvironmentVariable("FLOWKEY_E2E_MANUAL_FOCUS") != "1") throw;
            WaitForInitialTargetFocus(target);
            ActivateTarget(target, editor);
        }
    }

    private static bool TryClickInitialTarget(Window target, TextBox editor)
    {
        // Initial acquisition only, before recording: click the center of this test's blank
        // text box after verifying the window physically under that point belongs to us.
        // Never apply this fallback to the app window or after a later focus interruption.
        var handle = new WindowInteropHelper(target).Handle;
        Native.GetWindowThreadProcessId(handle, out var processId);
        if (handle == 0 || handle != _keyboardTarget || processId != Environment.ProcessId ||
            !ReferenceEquals(target.Content, editor) || editor.Text.Length != 0) { Console.WriteLine($"Focus setup: handle={handle}, expected={_keyboardTarget}, pid={processId}/{Environment.ProcessId}, text={editor.Text.Length}"); return false; }
        var hadCursor = GetCursorPos(out var originalCursor);
        var wasTopmost = target.Topmost;
        target.Topmost = true;
        try
        {
            PumpFor(100);
            if (!Native.GetClientRect(handle, out var rect) || rect.Width <= 0 || rect.Height <= 0) { Console.WriteLine("Focus setup: no client area"); return false; }
            var point = new Native.Point { X = rect.Width / 2, Y = rect.Height / 2 };
            if (!Native.ClientToScreen(handle, ref point) || !PointBelongsToTestTarget(point, handle)) { Console.WriteLine($"Focus acquisition: test point {point.X},{point.Y} is covered by another window."); return false; }
            if (!Native.SetCursorPos(point.X, point.Y)) { Console.WriteLine("Focus setup: Windows denied cursor positioning"); return false; }
            if (!PointBelongsToTestTarget(point, handle)) { Console.WriteLine("Focus setup: test point became covered"); return false; }
            try { Native.SendMouse(0x0002); }
            finally { Native.SendMouse(0x0004); }
            PumpFor(150);
            Console.WriteLine($"Focus acquisition: foreground={Native.GetForegroundWindow()}, expected={handle}");
            return Native.GetForegroundWindow() == handle;
        }
        catch (System.ComponentModel.Win32Exception error) { Console.WriteLine(error.Message); return false; }
        finally
        {
            if (hadCursor) Native.SetCursorPos(originalCursor.X, originalCursor.Y);
            target.Topmost = wasTopmost;
        }
    }

    private static bool PointBelongsToTestTarget(Native.Point point, nint expectedHandle)
    {
        var root = GetAncestor(WindowFromPoint(point), 2); // GA_ROOT
        if (root != expectedHandle) return false;
        Native.GetWindowThreadProcessId(root, out var processId);
        return processId == Environment.ProcessId;
    }

    private static void WaitForInitialTargetFocus(Window target)
    {
        Console.WriteLine("WAIT Windows UI: click the input area in '" + target.Title + "' within 120 seconds to allow the desktop test to begin.");
        var handle = new WindowInteropHelper(target).Handle;
        var watch = Stopwatch.StartNew();
        target.Topmost = true;
        try
        {
            target.Activate();
            Until(() => Native.GetForegroundWindow() == handle || watch.ElapsedMilliseconds >= 120000,
                "initial manual-focus wait did not finish", 47000);
            if (Native.GetForegroundWindow() != handle)
                throw new DesktopUnavailableException("initial manual focus timed out after 120 seconds; no test input was sent");
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
        PressShortcutInWindow(key, _keyboardTarget, callerLine);
    }

    private static void PressShortcutInWindow(string key, nint expectedWindow, [CallerLineNumber] int callerLine = 0)
    {
        Native.GetWindowThreadProcessId(expectedWindow, out var processId);
        if (expectedWindow == 0 || processId != Environment.ProcessId || Native.GetForegroundWindow() != expectedWindow)
            throw new DesktopUnavailableException($"test-owned window lost focus before shortcut at DesktopE2E.cs:{callerLine}; no input was sent");
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
