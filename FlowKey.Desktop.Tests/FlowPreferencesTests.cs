using System.IO;
using System.Text.Json;
using FlowKey.Desktop;

internal static class FlowPreferencesTests
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "FlowKey-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            if (FlowPreferences.Load(path) != new FlowPreferences()) throw new Exception("Default settings changed.");
            foreach (var auto in new[] { false, true })
            foreach (var back in new[] { false, true })
            {
                var settings = new FlowPreferences { AutoSwitch = auto, ReturnToApp = back, TargetTitle = "target", TargetProcess = "app", TargetPath = "C:\\app.exe" };
                settings.Save(path);
                if (FlowPreferences.Load(path) != settings) throw new Exception("Window flow did not persist.");
                var window = new WindowInfo(12, 1, "target", "app", "c:\\app.exe", 100, 100);
                if (settings.Resolve([window]) != window || settings.Resolve([]) is not null ||
                    settings.Resolve([window, window with { Handle = 13 }]) is not null ||
                    settings.Resolve([window with { ProcessPath = "other" }]) is not null)
                    throw new Exception("Missing/ambiguous/replaced target matching failed.");
                if (settings.Resolve([window, window with { Handle = 13 }], window) != window ||
                    settings.Resolve([window, window with { Handle = 13 }], window with { ProcessId = 999 }) is not null)
                    throw new Exception("Explicit selection or recycled window identity was mishandled.");
            }
            var defaults = new FlowPreferences();
            if (!defaults.CompletionAlertsEnabled || !defaults.CompletionBanner || !defaults.CompletionSound ||
                defaults.CompletionDialog || defaults.CompletionBorder || defaults.CompletionDurationSeconds != 4)
                throw new Exception("Completion alert defaults changed.");
            File.WriteAllText(path, "{\"AutoSwitch\":true}");
            var migrated = FlowPreferences.Load(path);
            if (!migrated.AutoSwitch || !migrated.CompletionAlertsEnabled || !migrated.CompletionBanner || !migrated.CompletionSound)
                throw new Exception("Older settings did not receive completion alert defaults.");
            foreach (var enabled in new[] { false, true })
            foreach (var banner in new[] { false, true })
            foreach (var sound in new[] { false, true })
            foreach (var dialog in new[] { false, true })
            foreach (var border in new[] { false, true })
            foreach (var duration in new[] { 2, 4, 10 })
            {
                var settings = defaults with { CompletionAlertsEnabled = enabled, CompletionBanner = banner,
                    CompletionSound = sound, CompletionDialog = dialog, CompletionBorder = border,
                    CompletionDurationSeconds = duration };
                settings.Save(path);
                if (FlowPreferences.Load(path) != settings) throw new Exception("Completion alert settings did not persist.");
            }
            var original = File.ReadAllText(path);
            try { new FlowPreferences { TargetTitle = null! }.Save(path); throw new Exception("Invalid setting accepted."); }
            catch (InvalidDataException) { }
            foreach (var duration in new[] { 1, 3, 11 })
            {
                try { (defaults with { CompletionDurationSeconds = duration }).Save(path); throw new Exception("Invalid alert duration accepted."); }
                catch (InvalidDataException) { }
            }
            if (File.ReadAllText(path) != original) throw new Exception("Failed settings save damaged prior data.");
            Directory.CreateDirectory(path + ".tmp");
            try { new FlowPreferences().Save(path); throw new Exception("Expected settings write failure."); }
            catch (UnauthorizedAccessException) { }
            if (File.ReadAllText(path) != original) throw new Exception("Write failure changed saved settings.");
            Directory.Delete(path + ".tmp");
            File.WriteAllText(path, "{");
            try { FlowPreferences.Load(path); throw new Exception("Corrupt settings accepted."); }
            catch (JsonException) { }
            Console.WriteLine("PASS all four window flows persist; missing/ambiguous targets, invalid/corrupt settings and write failure");
        }
        finally { Directory.Delete(root, true); }
    }
}
