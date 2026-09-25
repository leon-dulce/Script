using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace FlowKey.Desktop;

// A plain RegisterHotKey(F10) does not fire while replay is holding Alt/Ctrl/Shift.
// Observe the physical stop key independently of the current modifier state.
internal sealed class PlaybackStopCapture : IDisposable
{
    private readonly Native.HookCallback _callback;
    private readonly Thread _thread;
    private readonly int _key;
    private readonly Action _stop;
    private Dispatcher? _dispatcher;
    private nint _hook;
    private bool _stopQueued;

    internal PlaybackStopCapture(int key, Action stop)
    {
        if (key is < 0x77 or > 0x7A) throw new ArgumentOutOfRangeException(nameof(key));
        _key = key;
        _stop = stop;
        _callback = OnKeyboard;
        using var started = new ManualResetEventSlim();
        Exception? failure = null;
        _thread = new Thread(() =>
        {
            try
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                _hook = Native.SetWindowsHookEx(Native.KeyboardHook, _callback, Native.GetModuleHandle(null), 0);
                if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            catch (Exception error) { failure = error; }
            finally { started.Set(); }
            if (failure is null) try { Dispatcher.Run(); }
                finally { if (_hook != 0) Native.UnhookWindowsHookEx(_hook); }
        }) { IsBackground = true, Name = "FlowKey playback stop" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        started.Wait();
        if (failure is not null)
        {
            _thread.Join();
            throw new InvalidOperationException("無法啟用停止快捷鍵監聽。", failure);
        }
    }

    private nint OnKeyboard(int code, nint message, nint pointer)
    {
        if (code >= 0 && message is Native.KeyDown or Native.SysKeyDown or Native.KeyUp or Native.SysKeyUp)
        {
            var data = Marshal.PtrToStructure<Native.KeyboardData>(pointer);
            if (ShouldSuppress(code, message, data, _key))
            {
                if (ShouldStop(code, message, data, _key) && !_stopQueued)
                {
                    _stopQueued = true;
                    _stop();
                }
                // This key belongs to the stop command; do not pass Alt+F10 or
                // an unmatched F10 release into the target application.
                return 1;
            }
        }
        return Native.CallNextHookEx(_hook, code, message, pointer);
    }

    internal static bool ShouldSuppress(int code, nint message, Native.KeyboardData data, int key) =>
        code >= 0 && message is Native.KeyDown or Native.SysKeyDown or Native.KeyUp or Native.SysKeyUp &&
        data.VkCode == key && data.ExtraInfo != Native.ReplayInputTag;

    internal static bool ShouldStop(int code, nint message, Native.KeyboardData data, int key) =>
        ShouldSuppress(code, message, data, key) && message is Native.KeyDown or Native.SysKeyDown;

    public void Dispose()
    {
        if (!_thread.IsAlive) return;
        _dispatcher!.BeginInvokeShutdown(DispatcherPriority.Send);
        _thread.Join();
    }
}
