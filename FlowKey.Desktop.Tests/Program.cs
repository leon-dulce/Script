using System.Runtime.InteropServices;
using System.IO;
using FlowKey.Desktop;
using FlowKey.Core;

if (typeof(MainWindow).GetConstructor(Type.EmptyTypes) is null)
    throw new Exception("WPF StartupUri requires a real parameterless MainWindow constructor.");
Console.WriteLine("PASS WPF startup can construct MainWindow");

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

var sampleWindow = new WindowInfo((nint)123, 1, "测试窗口", "test", "", 800, 600);
var selectedWindow = WindowSelection.Find([sampleWindow], "123", _ => true);
if (selectedWindow is null || selectedWindow.Value.Handle != sampleWindow.Handle) throw new Exception("Window selection failed.");
if (WindowSelection.Find([sampleWindow], "123", _ => false) is not null) throw new Exception("Closed window was selected.");
Console.WriteLine("PASS window selection retains the chosen handle");

// Every physical event remains separate, including repeated downs and left/right modifiers.
foreach (var key in new uint[] { 0x41, 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x77, 0x78, 0x7A, 0x7B })
{
    var down = KeyboardStepFactory.Create(key, 0, false, 0x79);
    var repeat = KeyboardStepFactory.Create(key, 0, false, 0x79);
    var up = KeyboardStepFactory.Create(key, 0, true, 0x79);
    if (down is not { Type: StepType.Key, KeyAction: KeyAction.Down } || !down.Keys.SequenceEqual([(int)key]) ||
        repeat is not { KeyAction: KeyAction.Down } || !repeat.Keys.SequenceEqual([(int)key]) || ReferenceEquals(down, repeat) ||
        up is not { KeyAction: KeyAction.Up } || !up.Keys.SequenceEqual([(int)key]))
        throw new Exception($"Physical key {key:X2} did not retain separate down/repeat/up events.");
}
foreach (var controlHotkey in new[] { 0x77, 0x78, 0x79, 0x7A })
{
    foreach (var key in new uint[] { 0x77, 0x78, 0x79, 0x7A })
    foreach (var keyUp in new[] { false, true })
    {
        var step = KeyboardStepFactory.Create(key, 0, keyUp, controlHotkey);
        if ((step is null) != (key == controlHotkey)) throw new Exception("Only the configured control shortcut may be excluded.");
    }
}
foreach (var keyUp in new[] { false, true })
{
    foreach (var invalid in new uint[] { 0, 255, uint.MaxValue })
        if (KeyboardStepFactory.Create(invalid, 0, keyUp, 0x79) is not null) throw new Exception("Invalid key accepted.");
    if (KeyboardStepFactory.Create(0x41, Native.InjectedKeyboard, keyUp, 0x79) is not null ||
        KeyboardStepFactory.Create(0x41, Native.InjectedKeyboard | 0x80, keyUp, 0x79) is not null)
        throw new Exception("Injected input was recorded.");
}
Console.WriteLine("PASS physical key downs, repeats, releases, modifiers and function keys retain separate events");
Console.WriteLine("PASS only the configured recording hotkey and injected/invalid input are excluded");

foreach (var key in new ushort[] { 0x03, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0x90, 0xA3, 0xA5 })
{
    var down = Native.CreateKeyInput(key);
    var up = Native.CreateKeyInput(key, release: true);
    if (down.Type != 1 || down.Key.VirtualKey != key || down.Key.Scan != 0 || down.Key.Flags != 1 || up.Key.Flags != 3)
        throw new Exception($"Extended key {key:X2} lost its down/up flags.");
}
foreach (var key in new ushort[] { 0x41, 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA4, 0x0D, 0x61 })
    if (Native.CreateKeyInput(key).Key.Flags != 0 || Native.CreateKeyInput(key, release: true).Key.Flags != 2)
        throw new Exception($"Normal key {key:X2} was incorrectly marked extended.");
foreach (var character in new ushort[] { 0xA3, 0x4E2D })
{
    var down = Native.CreateKeyInput(character, unicode: true);
    var up = Native.CreateKeyInput(character, release: true, unicode: true);
    if (down.Key.VirtualKey != 0 || down.Key.Scan != character || down.Key.Flags != 4 || up.Key.Flags != 6)
        throw new Exception("Unicode input was incorrectly treated as an extended virtual key.");
}
Console.WriteLine("PASS replay input preserves extended right-modifier/navigation keys and Unicode flags");

KeyboardPlaybackTests.Run();

DesktopE2E.Run();

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
