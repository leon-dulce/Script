using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using FlowKey.Core;

namespace FlowKey.Desktop;

// Keep the low-level hook's message loop independent from WebView and window focus.
internal sealed class KeyboardCapture : IDisposable
{
    private readonly ConcurrentQueue<(ScriptStep Step, long Tick)> _events = new();
    private readonly Native.HookCallback _callback;
    private readonly Thread _thread;
    private readonly int _controlHotkey;
    private Dispatcher? _dispatcher;
    private nint _hook;
    private readonly KeyTransitionFilter _transitions = new();
    internal long StartedAt { get; private set; }
    internal bool IsAlive => _thread.IsAlive;

    internal KeyboardCapture(int controlHotkey)
    {
        if (controlHotkey is < 0x77 or > 0x7A) throw new ArgumentOutOfRangeException(nameof(controlHotkey));
        _controlHotkey = controlHotkey;
        _callback = OnKeyboard;
        using var started = new ManualResetEventSlim();
        Exception? failure = null;
        _thread = new Thread(() =>
        {
            try
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                StartedAt = Stopwatch.GetTimestamp();
                _hook = Native.SetWindowsHookEx(Native.KeyboardHook, _callback, Native.GetModuleHandle(null), 0);
                if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            catch (Exception error) { Cleanup(); failure = error; }
            finally { started.Set(); }
            if (failure is not null) return;
            try { Dispatcher.Run(); }
            finally
            {
                Cleanup();
            }
        }) { IsBackground = true, Name = "FlowKey keyboard capture" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        started.Wait();
        if (failure is not null)
        {
            _thread.Join();
            throw new InvalidOperationException("无法启用键盘监听。", failure);
        }
    }

    private nint OnKeyboard(int code, nint message, nint pointer)
    {
        if (code >= 0 && message is Native.KeyDown or Native.SysKeyDown or Native.KeyUp or Native.SysKeyUp)
        {
            var tick = Stopwatch.GetTimestamp();
            var data = Marshal.PtrToStructure<Native.KeyboardData>(pointer);
            var step = KeyboardStepFactory.Create(data.VkCode, data.Flags,
                message is Native.KeyUp or Native.SysKeyUp, _controlHotkey, data.ExtraInfo);
            if (step is not null && _transitions.Accept(step)) _events.Enqueue((step, tick));
        }
        return Native.CallNextHookEx(_hook, code, message, pointer);
    }

    internal bool TryDequeue(out (ScriptStep Step, long Tick) item) => _events.TryDequeue(out item);
    private void Cleanup()
    {
        if (_hook != 0) { Native.UnhookWindowsHookEx(_hook); _hook = 0; }
    }

    public void Dispose()
    {
        if (!_thread.IsAlive) return;
        _dispatcher!.BeginInvokeShutdown(DispatcherPriority.Send);
        _thread.Join();
    }
}
