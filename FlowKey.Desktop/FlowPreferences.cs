using System.IO;
using System.Text.Json;

namespace FlowKey.Desktop;

internal sealed record FlowPreferences
{
    public bool AutoSwitch { get; init; }
    public bool ReturnToApp { get; init; }
    public bool CompletionAlertsEnabled { get; init; } = true;
    public bool CompletionBanner { get; init; } = true;
    public bool CompletionSound { get; init; } = true;
    public bool CompletionDialog { get; init; }
    public bool CompletionBorder { get; init; }
    public int CompletionDurationSeconds { get; init; } = 4;
    public string TargetTitle { get; init; } = "";
    public string TargetProcess { get; init; } = "";
    public string TargetPath { get; init; } = "";

    internal WindowInfo? Resolve(IEnumerable<WindowInfo> windows, WindowInfo? selected = null)
    {
        var matches = windows.Where(w => w.Title == TargetTitle && w.ProcessName == TargetProcess &&
            (TargetPath.Length == 0 || string.Equals(w.ProcessPath, TargetPath, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (selected is { } current)
            foreach (var match in matches)
                if (match.Handle == current.Handle && match.ProcessId == current.ProcessId) return match;
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static FlowPreferences Load(string path)
    {
        if (!File.Exists(path)) return new();
        var value = JsonSerializer.Deserialize<FlowPreferences>(File.ReadAllText(path)) ?? throw new InvalidDataException("設定檔案為空。");
        Validate(value);
        return value;
    }

    internal void Save(string path)
    {
        Validate(this);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(FlowPreferences value)
    {
        if (value.TargetTitle is null || value.TargetProcess is null || value.TargetPath is null ||
            value.TargetTitle.Length > 512 || value.TargetProcess.Length > 256 || value.TargetPath.Length > 32768 ||
            value.CompletionDurationSeconds is not (2 or 4 or 6 or 10))
            throw new InvalidDataException("視窗或執行提示設定無效。");
    }
}
