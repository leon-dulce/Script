using System.Runtime.InteropServices;
using FlowKey.Desktop;

if (Marshal.SizeOf<Native.Input>() != 40) throw new Exception("SendInput layout must be 40 bytes on x64.");
if (Marshal.SizeOf<Native.KeyboardData>() != 24) throw new Exception("Keyboard hook layout mismatch.");
if (Marshal.SizeOf<Native.MouseData>() != 32) throw new Exception("Mouse hook layout mismatch.");
if (Native.GetModuleHandle(null) == 0) throw new Exception("Current module handle is missing.");
Native.ListWindows(0);
Native.HookCallback callback = (code, key, data) => Native.CallNextHookEx(0, code, key, data);
var module = Native.GetModuleHandle(null);
var keyboardHook = Native.SetWindowsHookEx(Native.KeyboardHook, callback, module, 0);
var mouseHook = Native.SetWindowsHookEx(Native.MouseHook, callback, module, 0);
try
{
    if (keyboardHook == 0 || mouseHook == 0) throw new Exception("Global input hooks could not be installed.");
}
finally
{
    if (keyboardHook != 0) Native.UnhookWindowsHookEx(keyboardHook);
    if (mouseHook != 0) Native.UnhookWindowsHookEx(mouseHook);
}
Console.WriteLine("PASS Win32 layouts, module handle, window enumeration, and input hooks");

var assetsRoot = Path.Combine(Path.GetTempPath(), "FlowKey-assets-test-" + Guid.NewGuid().ToString("N"));
try
{
    var assets = WebAssets.ExtractTo(assetsRoot);
    foreach (var name in new[] { "index.html", "app.js", "desktop.js" })
        if (new FileInfo(Path.Combine(assets, name)).Length == 0) throw new Exception($"Embedded asset missing: {name}");
    var original = File.ReadAllText(Path.Combine(assets, "desktop.js"));
    File.WriteAllText(Path.Combine(assets, "desktop.js"), "corrupted");
    if (WebAssets.ExtractTo(assetsRoot) != assets || File.ReadAllText(Path.Combine(assets, "desktop.js")) != original)
        throw new Exception("Embedded assets were not restored.");
    Console.WriteLine("PASS embedded UI assets extract and self-repair");
}
finally
{
    var tempRoot = Path.GetFullPath(Path.GetTempPath());
    var resolved = Path.GetFullPath(assetsRoot);
    if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unexpected test directory.");
    if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
}
