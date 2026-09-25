using System.Text.Json;

namespace FlowKey.Core;

public sealed class ScriptStore(string filePath)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string FilePath { get; } = filePath;

    public ScriptDocument? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var script = JsonSerializer.Deserialize<ScriptDocument>(File.ReadAllText(FilePath), Options);
            ScriptValidator.Validate(script);
            return script;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            throw new InvalidDataException("脚本文件损坏或包含无效数据。", error);
        }
    }

    public void Save(ScriptDocument script)
    {
        ScriptValidator.Validate(script);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var temporary = FilePath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(script, Options));
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
            else File.Move(temporary, FilePath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
