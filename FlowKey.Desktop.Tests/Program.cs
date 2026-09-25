using System.Runtime.InteropServices;
using System.IO;
using System.Reflection;
using FlowKey.Desktop;
using FlowKey.Core;

if (args.Contains("--keyboard-target")) { KeyboardTarget.Run(); return; }

if (typeof(MainWindow).GetConstructor(Type.EmptyTypes) is null)
    throw new Exception("WPF StartupUri requires a real parameterless MainWindow constructor.");
Console.WriteLine("PASS WPF startup can construct MainWindow");

var desktopAssembly = typeof(MainWindow).Assembly;
if (desktopAssembly.GetName().Version != new Version(1, 0, 0, 0) ||
    desktopAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion != "1.0.0 Stable" ||
    desktopAssembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version != "1.0.0.0")
    throw new Exception("Desktop assembly does not identify the 1.0.0 Stable release.");
Console.WriteLine("PASS desktop assembly carries 1.0.0 Stable version metadata");

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

var sampleWindow = new WindowInfo((nint)123, 1, "測試視窗", "test", "", 800, 600);
var selectedWindow = WindowSelection.Find([sampleWindow], "123", _ => true);
if (selectedWindow is null || selectedWindow.Value.Handle != sampleWindow.Handle) throw new Exception("Window selection failed.");
if (WindowSelection.Find([sampleWindow], "123", _ => false) is not null) throw new Exception("Closed window was selected.");
Console.WriteLine("PASS window selection retains the chosen handle");

// The decoder and capture filter preserve every Down, including typematic repeats.
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

foreach (var key in new ushort[] { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0x90, 0xA3, 0xA5 })
{
    var down = Native.CreateKeyInput(key);
    var up = Native.CreateKeyInput(key, release: true);
    if (down.Key.ExtraInfo != Native.ReplayInputTag || down.Type != 1 || down.Key.VirtualKey != 0 || down.Key.Scan == 0 || down.Key.Flags != 9 || up.Key.Flags != 11)
        throw new Exception($"Extended key {key:X2} lost its down/up flags: vk={down.Key.VirtualKey:X}, scan={down.Key.Scan:X}, down={down.Key.Flags:X}, up={up.Key.Flags:X}.");
}
foreach (var key in new ushort[] { 0x41, 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA4, 0x0D, 0x61 })
    if (Native.CreateKeyInput(key).Key.Flags != 8 || Native.CreateKeyInput(key, release: true).Key.Flags != 10)
        throw new Exception($"Normal key {key:X2} was incorrectly marked extended.");
foreach (var character in new ushort[] { 0xA3, 0x4E2D })
{
    var down = Native.CreateKeyInput(character, unicode: true);
    var up = Native.CreateKeyInput(character, release: true, unicode: true);
    if (down.Key.VirtualKey != 0 || down.Key.Scan != character || down.Key.Flags != 4 || up.Key.Flags != 6)
        throw new Exception("Unicode input was incorrectly treated as an extended virtual key.");
}
Console.WriteLine("PASS replay input preserves extended right-modifier/navigation keys and Unicode flags");
foreach (var sample in new (ushort Key, ushort Scan, ushort Flags)[] { (0x51, 0x10, 8), (0xA4, 0x38, 8), (0xA5, 0x38, 9), (0x25, 0x4B, 9), (0x27, 0x4D, 9) })
{
    var down = Native.CreateKeyInput(sample.Key);
    var up = Native.CreateKeyInput(sample.Key, release: true);
    if (down.Key.VirtualKey != 0 || down.Key.Scan != sample.Scan || down.Key.Flags != sample.Flags ||
        up.Key.Scan != sample.Scan || up.Key.Flags != (sample.Flags | 2))
        throw new Exception("Q/Alt/arrows did not use physical scan codes for both transitions.");
}
foreach (var key in new ushort[] { 0x13, 0xE7 })
{
    var input = Native.CreateKeyInput(key);
    if (input.Key.VirtualKey != key || input.Key.Scan != 0 || input.Key.Flags != 0)
        throw new Exception("E1/unmapped key did not preserve virtual-key fallback.");
}
Console.WriteLine("PASS Q, left/right Alt and arrows use scan codes; Pause/unmapped fallback and Unicode remain supported");


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
    if (!Transition(0x27)) throw new Exception("Held key auto-repeat was discarded.");
if (!Transition(0x41) || !Transition(0x27) || !Transition(0x27, true) || Transition(0x27, true) ||
    !Transition(0x41, true) || !Transition(0x27) || !Transition(0x27, true))
    throw new Exception("Overlapping keys, release or repress lost its transition.");
if (!Transition(0xA4) || !Transition(0xA4) || !Transition(0xA4, true) || Transition(0xA4, true))
    throw new Exception("Held Alt repeat or release was lost.");
if (!Transition(0xA0) || !Transition(0xA1) || !Transition(0xA0, true) || !Transition(0xA1, true))
    throw new Exception("Left and right modifiers were merged.");
Transition(0x41);
if (!new KeyTransitionFilter().Accept(KeyboardStepFactory.Create(0x41, 0, false, 0x79)!))
    throw new Exception("New recording inherited held keys.");
Console.WriteLine("PASS held-key repeats, overlapping keys, release/repress, modifier sides and recording reset");

FlowPreferencesTests.Run();
var activation = new WindowActivationWait(0);
if (activation.Observe(0, true, false) != ActivationStatus.Waiting ||
    activation.Observe(100, true, true) != ActivationStatus.Waiting ||
    activation.Observe(250, true, false) != ActivationStatus.Waiting ||
    activation.Observe(400, true, true) != ActivationStatus.Waiting ||
    activation.Observe(599, true, true) != ActivationStatus.Waiting ||
    activation.Observe(600, true, true) != ActivationStatus.Ready ||
    new WindowActivationWait(0).Observe(5000, true, false) != ActivationStatus.TimedOut ||
    new WindowActivationWait(0).Observe(50, false, false) != ActivationStatus.Missing)
    throw new Exception("Delayed/transient/denied/closed window activation was handled incorrectly.");
Console.WriteLine("PASS delayed foreground activation, stable focus, timeout and closed-window rejection");
await WebView2RuntimeSetupTests.Run();
DesktopE2E.Run();

var assetsRoot = Path.Combine(Path.GetTempPath(), "FlowKey-assets-test-" + Guid.NewGuid().ToString("N"));
try
{
    var assets = WebAssets.ExtractTo(assetsRoot);
    foreach (var name in WebAssets.Names)
        if (new FileInfo(Path.Combine(assets, name)).Length == 0) throw new Exception($"Embedded asset missing: {name}");
    if (File.Exists(Path.Combine(assets, "app.js")) || WebAssets.Names.Contains("app.js"))
        throw new Exception("Demo script was included in production assets.");
    var original = File.ReadAllText(Path.Combine(assets, "desktop.js"));
    File.WriteAllText(Path.Combine(assets, "desktop.js"), "corrupted");
    if (WebAssets.ExtractTo(assetsRoot) != assets || File.ReadAllText(Path.Combine(assets, "desktop.js")) != original)
        throw new Exception("Embedded assets were not restored.");
    var font = Path.Combine(assets, "assets/NotoSerifTC.ttf");
    var fontBytes = File.ReadAllBytes(font);
    File.WriteAllBytes(font, [0]);
    File.Delete(Path.Combine(assets, "assets/flowkey.svg"));
    WebAssets.ExtractTo(assetsRoot);
    if (!File.ReadAllBytes(font).SequenceEqual(fontBytes) || !File.Exists(Path.Combine(assets, "assets/flowkey.svg")))
        throw new Exception("Offline branding assets did not self-repair.");
    Console.WriteLine("PASS embedded UI assets extract and self-repair");
}
finally
{
    var tempRoot = Path.GetFullPath(Path.GetTempPath());
    var resolved = Path.GetFullPath(assetsRoot);
    if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unexpected test directory.");
    if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
}
