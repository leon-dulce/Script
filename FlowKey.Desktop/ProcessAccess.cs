using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace FlowKey.Desktop;

internal static class ProcessAccess
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int id);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int infoClass, out int value, int size, out int needed);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);

    internal static bool? IsElevated(int id)
    {
        var process = OpenProcess(0x1000, false, id); // QUERY_LIMITED_INFORMATION, not all-access.
        if (process == 0) return null;
        try
        {
            if (!OpenProcessToken(process, 8, out var token)) return null;
            try { return GetTokenInformation(token, 20, out var value, sizeof(int), out _) ? value != 0 : null; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    internal static string RecordingWarning(bool elevated, bool? foregroundElevated) =>
        !elevated && foregroundElevated == true
            ? "前台程序以管理员权限运行，当前 FlowKey 可能无法录到按键。请停止并保存本次录制，再以管理员模式重新开启。"
            : "";

    internal static ProcessStartInfo RestartInfo(string executable, string hotkey) => new(executable)
    {
        UseShellExecute = true,
        Verb = "runas",
        Arguments = "--recording-hotkey " + (FlowKey.Core.ScriptValidator.Hotkeys.Contains(hotkey) ? hotkey : "F10"),
        WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
    };

    internal static bool TryRestart(string executable, string hotkey, out string error,
        Func<ProcessStartInfo, bool>? start = null)
    {
        try
        {
            if (!(start ?? (info => Process.Start(info) is not null))(RestartInfo(executable, hotkey)))
            { error = "未能启动管理员模式，当前窗口已保留。"; return false; }
            error = "";
            return true;
        }
        catch (Win32Exception e)
        {
            error = e.NativeErrorCode == 1223 ? "已取消管理员授权，当前窗口已保留。" : "无法启动管理员模式：" + e.Message;
            return false;
        }
    }
}
