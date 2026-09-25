using System.Diagnostics;
using System.IO;
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
            ActivateTarget(target, editor);
            Post(app, "{action:'refresh'}");
            var handle = new WindowInteropHelper(target).Handle;
            Until(() => Native.ListWindows(new WindowInteropHelper(app).Handle).Any(w => w.Handle == handle), "test target not listed");
            Post(app, "{action:'select',value:'" + handle + "'}");
            Until(() => Text(app, "window-name") == targetName, "target selection was not confirmed");

            var hotkey = ChooseHotkey(app);
            Post(app, "{action:'record'}");
            ActivateTarget(target, editor);
            Until(() => Text(app, "status-text") == "正在录制", "recording did not begin in selected window");
            PressHotkey(hotkey);
            Until(() => Text(app, "status-text") == "就绪，等待操作", "hotkey did not finish recording");
            Eval(app, "window.prompt=()=> 'X'; document.getElementById('add-text').click()");
            Until(() => Text(app, "step-count") == "1 个步骤", "text step was not added");
            Eval(app, "document.getElementById('script-name').value='E2E repeat';document.getElementById('script-name').dispatchEvent(new Event('change'))");
            Until(() => Text(app, "execution-script-name").Contains("E2E repeat"), "script name did not update");
            if (EvalBool(app, "!document.getElementById('sidebar-library').hidden"))
                throw new Exception("Saved scripts must be hidden in the recording workspace.");
            Eval(app, "document.getElementById('nav-execution').click()");
            Until(() => EvalBool(app, "!document.getElementById('execution-view').hidden && !document.getElementById('sidebar-library').hidden"),
                "execution workspace or saved scripts did not open");

            SetPlan(app, "Count", 3, 100);
            Eval(app, "document.getElementById('save-execution').click()");
            var catalog = new ScriptCatalog(Path.Combine(root, "scripts"));
            Until(() => catalog.List().Scripts.Count == 1, "script was not saved to the library");
            Until(() => Text(app, "execution-message").Contains("已保存"), "execution save did not show confirmation");
            if (!EvalBool(app, "!document.getElementById('execution-view').hidden"))
                throw new Exception("Saving execution settings returned to the recording workspace.");
            var saved = catalog.List().Scripts.Single();
            if (saved.Execution.Mode != ExecutionMode.Count || saved.Execution.RepeatCount != 3 || saved.Execution.IntervalMs != 100)
                throw new Exception("Saved repeat plan differs from the execution screen.");
            if (Text(app, "saved-scripts").Contains("E2E repeat") == false)
                throw new Exception("Saved script is absent from the sidebar.");
            ActivateTarget(target, editor);
            Eval(app, "document.getElementById('execution-start').click()");
            Until(() => editor.Text == "XXX", "count mode did not run three times", 12000);
            Until(() => Text(app, "status-text") == "就绪，等待操作", "count mode did not finish");
            if (!EvalBool(app, "!document.getElementById('execution-view').hidden"))
                throw new Exception("Starting execution returned to the recording workspace.");
            Console.WriteLine("PASS Windows UI: select, record hotkey, save, sidebar, and three execution rounds");

            SetPlan(app, "Continuous", 3, 100);
            ActivateTarget(target, editor);
            Eval(app, "document.getElementById('execution-start').click()");
            Until(() => editor.Text.Length >= 5, "continuous mode did not repeat", 10000);
            PressHotkey(hotkey);
            Until(() => Text(app, "status-text") == "就绪，等待操作", "hotkey did not stop continuous execution");
            var stoppedLength = editor.Text.Length;
            PumpFor(900);
            if (editor.Text.Length != stoppedLength) throw new Exception("Continuous execution continued after stop.");
            Console.WriteLine("PASS Windows UI: continuous execution and hotkey stop");

            SetPlan(app, "Once", 3, 100);
            ActivateTarget(target, editor);
            Eval(app, "document.getElementById('execution-start').click()");
            Until(() => editor.Text.Length == stoppedLength + 1, "once mode did not execute exactly once", 10000);
            Until(() => Text(app, "status-text") == "就绪，等待操作", "once mode did not finish");
            PumpFor(300);
            if (editor.Text.Length != stoppedLength + 1) throw new Exception("Once mode repeated unexpectedly.");
            Console.WriteLine("PASS Windows UI: one-shot execution");

            Eval(app, "document.getElementById('save-execution').click()");
            Until(() => Text(app, "execution-message").Contains("已保存"), "one-shot plan did not save");
            Eval(app, "document.getElementById('new-script').click()");
            Until(() => EvalString(app, "document.getElementById('script-name').value") == "未命名脚本", "new script was not created");
            if (EvalBool(app, "!document.getElementById('sidebar-library').hidden"))
                throw new Exception("Saved scripts remained visible after returning to recording.");
            Eval(app, "document.getElementById('script-name').value='E2E second';document.getElementById('script-name').dispatchEvent(new Event('change'))");
            Eval(app, "document.getElementById('save-button').click()");
            Until(() => catalog.List().Scripts.Count == 2, "second script was not saved");
            Eval(app, "document.getElementById('nav-execution').click()");
            Eval(app, "Array.from(document.querySelectorAll('.saved-script')).find(b=>b.textContent.includes('E2E repeat')).click()");
            Until(() => EvalString(app, "document.getElementById('script-name').value") == "E2E repeat", "library script did not open");
            if (EvalString(app, "document.getElementById('execution-window-select').value") != "")
                throw new Exception("Opening a saved script must require target confirmation.");
            Eval(app, "document.getElementById('execution-start').click()");
            Until(() => Text(app, "execution-message").Contains("请先选择目标窗口"), "missing target did not explain why execution could not start");
            Eval(app, "document.getElementById('execution-window-select').value='" + handle + "';document.getElementById('execution-window-select').dispatchEvent(new Event('change'))");
            Until(() => EvalString(app, "document.getElementById('execution-window-select').value") == handle.ToString(), "execution target was not confirmed");
            ActivateTarget(target, editor);
            var afterReopen = editor.Text.Length;
            Eval(app, "document.getElementById('execution-start').click()");
            Until(() => editor.Text.Length == afterReopen + 1, "saved script did not execute from execution workspace", 10000);
            Until(() => Text(app, "status-text") == "就绪，等待操作", "saved script run did not finish");
            Eval(app, "Array.from(document.querySelectorAll('.saved-script')).find(b=>b.textContent.includes('E2E second')).click()");
            Until(() => EvalString(app, "document.getElementById('script-name').value") == "E2E second", "second library script did not open");
            Eval(app, "window.confirm=()=>true;document.getElementById('delete-script').click()");
            Until(() => catalog.List().Scripts.Count == 1, "sidebar delete did not remove script");
            Console.WriteLine("PASS Windows UI: execution-only library, target reselection, save/run feedback, and script management");
        }
        finally
        {
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

    private static string ChooseHotkey(MainWindow app)
    {
        foreach (var key in new[] { "F11", "F9", "F8", "F10" })
        {
            Post(app, "{action:'hotkey',value:'" + key + "'}");
            if (EvalString(app, "document.getElementById('hotkey-select').value") == key &&
                !Text(app, "status-hint").Contains("占用")) return key;
        }
        throw new Exception("No global shortcut could be registered for the desktop test.");
    }

    private static void SetPlan(MainWindow app, string mode, int count, int interval)
    {
        Eval(app, "document.getElementById('repeat-count').value='" + count + "';" +
            "document.getElementById('repeat-interval').value='" + interval + "';" +
            "document.getElementById('mode-" + mode.ToLowerInvariant() + "').checked=true;" +
            "document.getElementById('mode-" + mode.ToLowerInvariant() + "').dispatchEvent(new Event('change'))");
        Until(() => EvalBool(app, "document.getElementById('mode-" + mode.ToLowerInvariant() + "').checked && " +
            "document.getElementById('repeat-count').value==='" + count + "' && " +
            "document.getElementById('repeat-interval').value==='" + interval + "'"), "execution mode did not update");
    }

    private static void ActivateTarget(Window target, TextBox editor)
    {
        var handle = new WindowInteropHelper(target).Handle;
        target.Activate();
        editor.Focus();
        var watch = Stopwatch.StartNew();
        Until(() => Native.GetForegroundWindow() == handle || watch.ElapsedMilliseconds >= 1000,
            "foreground check did not return", 2000);
        if (Native.GetForegroundWindow() != handle)
            throw new DesktopUnavailableException("the test window could not obtain foreground focus");
    }

    private static void PressHotkey(string key)
    {
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
