using FlowKey.Core;
using FlowKey.Desktop;

internal static class KeyboardPlaybackTests
{
    public static void Run()
    {
        var events = new List<(ushort Key, bool Up)>();
        var keyboard = new KeyboardPlayback((key, up) => events.Add((key, up)));
        keyboard.Execute(Key(0xA2, KeyAction.Down));
        keyboard.Execute(Key(0x41, KeyAction.Down));
        keyboard.Execute(Key(0x41, KeyAction.Down));
        keyboard.Execute(Key(0x41, KeyAction.Up));
        keyboard.ReleaseAll();
        Expect(events, [(0xA2, false), (0x41, false), (0x41, false), (0x41, true), (0xA2, true)]);
        events.Clear();
        keyboard.ReleaseAll();
        keyboard.Execute(Key(0x41, KeyAction.Up));
        Expect(events, []);

        keyboard.Execute(Key(0x11, KeyAction.Down));
        keyboard.Execute(new ScriptStep { Type = StepType.Key, Keys = [0x11, 0x53] });
        keyboard.ReleaseAll();
        Expect(events, [(0x11, false), (0x11, false), (0x53, false), (0x53, true), (0x11, true)]);

        events.Clear();
        var failPress = true;
        keyboard = new KeyboardPlayback((key, up) =>
        {
            events.Add((key, up));
            if (!up && key == 0x41 && failPress) throw new InvalidOperationException("test send failure");
        });
        keyboard.Execute(Key(0x11, KeyAction.Down));
        try { keyboard.Execute(Key(0x41, KeyAction.Down)); throw new Exception("Expected input failure"); }
        catch (InvalidOperationException) { }
        failPress = false;
        keyboard.ReleaseAll();
        Expect(events, [(0x11, false), (0x41, false), (0x41, true), (0x11, true)]);

        events.Clear();
        var failRelease = true;
        keyboard = new KeyboardPlayback((key, up) =>
        {
            events.Add((key, up));
            if (up && key == 0x41 && failRelease) throw new InvalidOperationException("test release failure");
        });
        keyboard.Execute(Key(0x11, KeyAction.Down));
        keyboard.Execute(Key(0x41, KeyAction.Down));
        try { keyboard.ReleaseAll(); throw new Exception("Expected release failure"); }
        catch (InvalidOperationException) { }
        failRelease = false;
        keyboard.ReleaseAll();
        Expect(events, [(0x11, false), (0x41, false), (0x41, true), (0x11, true), (0x41, true)]);
        Console.WriteLine("PASS keyboard down/up, repeats, legacy combinations, and held-key cleanup after stop/failure");
    }

    private static ScriptStep Key(int key, KeyAction action) => new() { Type = StepType.Key, Keys = [key], KeyAction = action };
    private static void Expect(List<(ushort Key, bool Up)> actual, (ushort Key, bool Up)[] expected)
    {
        if (!actual.SequenceEqual(expected)) throw new Exception("Keyboard event order/release mismatch.");
    }
}
