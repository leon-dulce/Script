using System.Runtime.InteropServices;
using System.IO;
using FlowKey.Desktop;
using FlowKey.Core;

if (args.Contains("--keyboard-target")) { KeyboardTarget.Run(); return; }

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

// The stateless decoder preserves event identity; the capture filter below removes held-key repeats.
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
    if (KeyboardStepFactory.Create(0x41, Native.InjectedKeyboard, keyUp, 0x79, Native.ReplayInputTag) is not null ||
        KeyboardStepFactory.Create(0x41, Native.InjectedKeyboard | 0x80, keyUp, 0x79, Native.ReplayInputTag) is not null)
        throw new Exception("FlowKey playback input was recorded.");
    if (KeyboardStepFactory.Create(0x41, Native.InjectedKeyboard, keyUp, 0x79) is null)
        throw new Exception("External keyboard input was incorrectly discarded.");
}
Console.WriteLine("PASS stateless key decoder preserves down/up events, modifiers and function keys");
Console.WriteLine("PASS only the configured recording hotkey and own playback/invalid input are excluded");

foreach (var key in new ushort[] { 0x03, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0x90, 0xA3, 0xA5 })
{
    var down = Native.CreateKeyInput(key);
    var up = Native.CreateKeyInput(key, release: true);
    if (down.Key.ExtraInfo != Native.ReplayInputTag || down.Type != 1 || down.Key.VirtualKey != key || down.Key.Scan != 0 || down.Key.Flags != 1 || up.Key.Flags != 3)
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

for (var attempt = 0; attempt < 3; attempt++)
{
    using var capture = new KeyboardCapture(0x79);
    if (!capture.IsAlive || capture.StartedAt <= 0) throw new Exception("Dedicated capture did not start.");
    capture.Dispose();
    capture.Dispose();
    if (capture.IsAlive) throw new Exception("Capture thread leaked after repeated stop.");
}
try { using var invalidCapture = new KeyboardCapture(0); throw new Exception("Invalid shortcut accepted."); }
catch (ArgumentOutOfRangeException) { }
Console.WriteLine("PASS dedicated hook start/stop/restart, idempotent disposal, and invalid-shortcut rejection");

if (ProcessAccess.IsElevated(Environment.ProcessId) is null || ProcessAccess.IsElevated(int.MaxValue) is not null)
    throw new Exception("Limited process privilege query failed.");
if (ProcessAccess.RecordingWarning(false, true).Length == 0 || ProcessAccess.RecordingWarning(true, true).Length != 0 ||
    ProcessAccess.RecordingWarning(false, false).Length != 0 || ProcessAccess.RecordingWarning(false, null).Length != 0)
    throw new Exception("Privilege mismatch warning incorrect.");
var restart = ProcessAccess.RestartInfo(@"C:\test folder\FlowKey.exe", "F9");
if (restart.Verb != "runas" || !restart.UseShellExecute || restart.Arguments != "--recording-hotkey F9")
    throw new Exception("Administrator restart request incorrect.");
if (ProcessAccess.TryRestart("FlowKey.exe", "F10", out var cancelError, _ => throw new System.ComponentModel.Win32Exception(1223)) || !cancelError.Contains("取消"))
    throw new Exception("UAC cancellation did not retain the current instance.");
if (ProcessAccess.TryRestart("FlowKey.exe", "F10", out _, _ => false) ||
    ProcessAccess.TryRestart("FlowKey.exe", "F10", out _, _ => throw new System.ComponentModel.Win32Exception(2)) ||
    !ProcessAccess.TryRestart("FlowKey.exe", "F10", out _, _ => true))
    throw new Exception("Restart success/failure handling incorrect.");
Console.WriteLine("PASS limited privilege inspection, mismatch warning, UAC request, cancellation, and launch failure handling");

KeyboardPlaybackTests.Run();

var transitions = new KeyTransitionFilter();
bool Transition(uint key, bool up = false) => transitions.Accept(KeyboardStepFactory.Create(key, 0, up, 0x79)!);
if (Transition(0x27, true) || !Transition(0x27)) throw new Exception("Initial key transition incorrect.");
for (var repeatIndex = 0; repeatIndex < 100; repeatIndex++)
    if (Transition(0x27)) throw new Exception("Held key auto-repeat was recorded.");
if (!Transition(0x41) || Transition(0x27) || !Transition(0x27, true) || Transition(0x27, true) ||
    !Transition(0x41, true) || !Transition(0x27) || !Transition(0x27, true))
    throw new Exception("Overlapping keys, release or repress lost its transition.");
if (!Transition(0xA0) || !Transition(0xA1) || !Transition(0xA0, true) || !Transition(0xA1, true))
    throw new Exception("Left and right modifiers were merged.");
Transition(0x41);
if (!new KeyTransitionFilter().Accept(KeyboardStepFactory.Create(0x41, 0, false, 0x79)!))
    throw new Exception("New recording inherited held keys.");
Console.WriteLine("PASS held-key repeat suppression, overlapping keys, release/repress, modifier sides and recording reset");

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
