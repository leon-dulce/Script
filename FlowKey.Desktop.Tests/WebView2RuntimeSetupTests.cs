using System.IO;
using System.Net.Http;
using FlowKey.Desktop;

internal static class WebView2RuntimeSetupTests
{
    internal static async Task Run()
    {
        var downloads = 0;
        var installs = 0;
        var available = true;
        string? installer = null;

        Task Download(string path, CancellationToken _)
        {
            downloads++;
            installer = path;
            File.WriteAllText(path, "test installer");
            return Task.CompletedTask;
        }
        Task<int> Install(string path, CancellationToken _)
        {
            installs++;
            if (path != installer || !File.Exists(path)) throw new Exception("Installer path was not passed through.");
            available = true;
            return Task.FromResult(0);
        }
        await WebView2RuntimeSetup.EnsureAsync(() => available, Download, Install);
        if (downloads != 0 || installs != 0) throw new Exception("Installed runtime triggered setup.");

        available = false;
        var messages = new List<string>();
        await WebView2RuntimeSetup.EnsureAsync(() => available, Download, Install, status: messages.Add);
        if (downloads != 1 || installs != 1 || installer is null || File.Exists(installer))
            throw new Exception("Missing runtime was not installed and cleaned up.");
        if (messages.Count != 2 || !messages[0].Contains("下載") || !messages[1].Contains("安裝"))
            throw new Exception("Startup did not show setup progress.");

        available = false;
        await ExpectFailure("下載", () => WebView2RuntimeSetup.EnsureAsync(() => available,
            (_, _) => throw new HttpRequestException("offline"), Install));
        if (installs != 1) throw new Exception("Installer ran after download failure.");

        await ExpectFailure("啟動", () => WebView2RuntimeSetup.EnsureAsync(() => available,
            Download, (_, _) => throw new IOException("blocked")));
        if (installer is null || File.Exists(installer)) throw new Exception("Failed install left a downloaded file.");

        await ExpectFailure("代碼 9", () => WebView2RuntimeSetup.EnsureAsync(() => available,
            Download, (_, _) => Task.FromResult(9)));
        await ExpectFailure("仍無法使用", () => WebView2RuntimeSetup.EnsureAsync(() => available,
            Download, (_, _) => Task.FromResult(0)));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await WebView2RuntimeSetup.EnsureAsync(() => false,
                (_, token) => Task.FromCanceled(token), Install, cancelled.Token);
            throw new Exception("Cancelled startup was treated as an install failure.");
        }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS WebView2 setup: installed, missing, download/install failures, unchanged runtime and cancellation");
    }

    private static async Task ExpectFailure(string expected, Func<Task> run)
    {
        try { await run(); }
        catch (InvalidOperationException error) when (error.Message.Contains(expected, StringComparison.Ordinal)) { return; }
        throw new Exception($"WebView2 setup did not report '{expected}'.");
    }
}
