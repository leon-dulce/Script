namespace FlowKey.Core;

public sealed record ScriptCatalogResult(IReadOnlyList<ScriptDocument> Scripts, IReadOnlyList<string> Errors);

public sealed class ScriptCatalog(string directory)
{
    public string DirectoryPath { get; } = directory;

    public ScriptCatalogResult List()
    {
        if (!Directory.Exists(DirectoryPath)) return new([], []);
        var scripts = new List<ScriptDocument>();
        var errors = new List<string>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var script = new ScriptStore(path).Load() ?? throw new InvalidDataException("脚本文件不存在。");
                if (!string.Equals(Path.GetFileNameWithoutExtension(path), script.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("脚本编号与文件名不符。");
                scripts.Add(script);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                errors.Add($"{Path.GetFileName(path)}：{error.Message}");
            }
        }
        return new(scripts, errors);
    }

    public ScriptDocument? Load(string id) => new ScriptStore(PathFor(id)).Load();

    public void Save(ScriptDocument script)
    {
        ScriptValidator.Validate(script);
        new ScriptStore(PathFor(script.Id)).Save(script);
    }

    public bool Delete(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public ScriptDocument? MigrateLegacy(string legacyPath)
    {
        if (List().Scripts.Count > 0 || !File.Exists(legacyPath)) return null;
        var script = new ScriptStore(legacyPath).Load();
        if (script is not null) Save(script);
        return script;
    }

    private string PathFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("脚本编号无效。");
        return Path.Combine(DirectoryPath, id + ".json");
    }
}
