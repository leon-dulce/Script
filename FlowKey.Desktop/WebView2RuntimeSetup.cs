using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Microsoft.Web.WebView2.Core;

namespace FlowKey.Desktop;

internal static class WebView2RuntimeSetup
{
    internal const string DownloadPage = "https://developer.microsoft.com/microsoft-edge/webview2/";
    internal static readonly Uri Bootstrapper = new("https://go.microsoft.com/fwlink/p/?LinkId=2124703");

    internal static bool IsAvailable()
    {
        try { return !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
        catch (WebView2RuntimeNotFoundException) { return false; }
    }

    internal static async Task EnsureAsync(Action<string>? status = null, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(IsAvailable, DownloadAsync, InstallAsync, cancellationToken, status);
    }

    // Separate the detection, transfer and installer steps so every startup outcome can be tested
    // without changing the computer's installed runtime.
    internal static async Task EnsureAsync(
        Func<bool> isAvailable,
        Func<string, CancellationToken, Task> download,
        Func<string, CancellationToken, Task<int>> install,
        CancellationToken cancellationToken = default,
        Action<string>? status = null)
    {
        if (isAvailable()) return;
        var path = Path.Combine(Path.GetTempPath(), "FlowKey-WebView2-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            status?.Invoke("正在下載 Microsoft Edge WebView2 執行環境…");
            try { await download(path, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { throw new InvalidOperationException("無法下載 Microsoft Edge WebView2 執行環境。請檢查網路連線後重新開啟 FlowKey。", error); }

            int exitCode;
            status?.Invoke("正在安裝 Microsoft Edge WebView2 執行環境…");
            try { exitCode = await install(path, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { throw new InvalidOperationException("無法啟動 WebView2 安裝程式。請檢查 Windows 權限後重新開啟 FlowKey。", error); }
            if (exitCode != 0)
                throw new InvalidOperationException($"WebView2 安裝未完成（代碼 {exitCode}）。請重新開啟 FlowKey，或手動安裝執行環境。");
            if (!isAvailable())
                throw new InvalidOperationException("WebView2 安裝後仍無法使用。請重新啟動 Windows 後再開啟 FlowKey。");
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task DownloadAsync(string path, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var response = await client.GetAsync(Bootstrapper, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 20_000_000)
            throw new InvalidDataException("WebView2 安裝檔超過預期大小。");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long length = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            length += count;
            if (length > 20_000_000) throw new InvalidDataException("WebView2 安裝檔超過預期大小。");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        if (length == 0) throw new InvalidDataException("WebView2 安裝檔是空的。");
    }

    private static async Task<int> InstallAsync(string path, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = "/silent /install"
        }) ?? throw new InvalidOperationException("安裝程式無法啟動。");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
}
