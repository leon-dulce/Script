using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace FlowKey.Desktop;

internal readonly record struct WindowInfo(nint Handle, int ProcessId, string Title, string ProcessName, string ProcessPath, int Width, int Height);

internal static class Native
{
    internal const int KeyboardHook = 13, MouseHook = 14, HotkeyMessage = 0x0312;
    internal const int KeyDown = 0x0100, SysKeyDown = 0x0104;
    internal const int LeftDown = 0x0201, RightDown = 0x0204, MiddleDown = 0x0207, Wheel = 0x020A;
    internal const uint InjectedKeyboard = 0x10, InjectedMouse = 0x01;

    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardData { public uint VkCode, ScanCode, Flags, Time; public nint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseData { public Point Point; public uint MouseInfo, Flags, Time; public nint ExtraInfo; }
    internal delegate nint HookCallback(int code, nint wParam, nint lParam);
    internal delegate bool WindowCallback(nint handle, nint parameter);

    [DllImport("user32.dll")] internal static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint handle, StringBuilder buffer, int maxCount);
    [DllImport("user32.dll")] internal static extern int GetWindowTextLength(nint handle);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint handle, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint handle, ref Point point);
    [DllImport("user32.dll")] internal static extern bool ScreenToClient(nint handle, ref Point point);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(nint handle, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool UnregisterHotKey(nint handle, int id);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookEx(int id, HookCallback callback, nint module, uint threadId);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? moduleName);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, [In] Input[] input, int size);

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MouseInput Mouse;
        [FieldOffset(8)] public KeyInput Key;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public nint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyInput { public ushort VirtualKey, Scan, Flags; public uint Time; public nint ExtraInfo; }

    internal static List<WindowInfo> ListWindows(nint ownHandle)
    {
        var found = new List<WindowInfo>();
        EnumWindows((handle, _) =>
        {
            if (handle == ownHandle || !IsWindowVisible(handle)) return true;
            var length = GetWindowTextLength(handle);
            if (length == 0 || length > 512 || !GetClientRect(handle, out var rect) || rect.Width <= 0 || rect.Height <= 0) return true;
            var title = new StringBuilder(length + 1);
            GetWindowText(handle, title, title.Capacity);
            GetWindowThreadProcessId(handle, out var processId);
            try
            {
                using var process = Process.GetProcessById((int)processId);
                var path = "";
                try { path = process.MainModule?.FileName ?? ""; } catch { /* Protected processes have no accessible path. */ }
                found.Add(new(handle, (int)processId, title.ToString(), process.ProcessName, path, rect.Width, rect.Height));
            }
            catch (ArgumentException) { /* Window closed during enumeration. */ }
            return true;
        }, 0);
        return found;
    }

    internal static bool Matches(WindowInfo target)
    {
        if (!IsWindow(target.Handle)) return false;
        GetWindowThreadProcessId(target.Handle, out var processId);
        return processId == target.ProcessId;
    }

    internal static bool SameSize(WindowInfo target) =>
        GetClientRect(target.Handle, out var rect) && rect.Width == target.Width && rect.Height == target.Height;

    internal static void SendKey(ushort key, bool release = false, bool unicode = false)
    {
        var input = new Input { Type = 1, Key = new KeyInput { VirtualKey = unicode ? (ushort)0 : key, Scan = unicode ? key : (ushort)0, Flags = (ushort)((release ? 2 : 0) | (unicode ? 4 : 0)) } };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1) throw new Win32Exception("无法向目标窗口发送按键。请确认窗口没有更高权限。");
    }

    internal static void SendMouse(uint flags, uint data = 0)
    {
        var input = new Input { Type = 0, Mouse = new MouseInput { Flags = flags, MouseData = data } };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1) throw new Win32Exception("无法向目标窗口发送鼠标操作。请确认窗口没有更高权限。");
    }
}
