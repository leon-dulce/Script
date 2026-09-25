using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace FlowKey.Desktop;

internal static class WebAssets
{
    internal static readonly string[] Names = ["index.html", "app.js", "desktop.js", "desktop-ui.css", "branding.css", "assets/flowkey.svg", "assets/NotoSerifTC.ttf", "assets/OFL-NotoSerifTC.txt"];

    internal static string ExtractTo(string root)
    {
        var assembly = typeof(WebAssets).Assembly;
        var files = Names.Select(name => (Name: name, Bytes: ReadResource(assembly, name))).ToArray();
        var digest = SHA256.HashData(files.SelectMany(file => file.Bytes).ToArray());
        var directory = Path.Combine(root, Convert.ToHexString(digest)[..16]);
        Directory.CreateDirectory(directory);
        foreach (var file in files)
        {
            var destination = Path.Combine(directory, file.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination) && File.ReadAllBytes(destination).SequenceEqual(file.Bytes)) continue;
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, file.Bytes);
                File.Move(temporary, destination, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return directory;
    }

    private static byte[] ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream("FlowKey.Assets." + name)
            ?? throw new InvalidDataException($"缺少內建介面資源：{name}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
