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
            ? "你正在操作的程式使用管理員權限，FlowKey 可能錄不到按鍵。請先停止並儲存，再以管理員模式重新開啟 FlowKey。"
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
            { error = "沒有成功開啟管理員模式，你目前的視窗會保留。"; return false; }
            error = "";
            return true;
        }
        catch (Win32Exception e)
        {
            error = e.NativeErrorCode == 1223 ? "已取消管理員授權，你目前的視窗會保留。" : "無法啟動管理員模式：" + e.Message;
            return false;
        }
    }
}
