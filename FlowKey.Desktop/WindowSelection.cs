namespace FlowKey.Desktop;

internal static class WindowSelection
{
    internal static WindowInfo? Find(IEnumerable<WindowInfo> windows, string? id, Func<WindowInfo, bool> isAlive)
    {
        foreach (var window in windows)
            if (window.Handle.ToString() == id && isAlive(window)) return window;
        return null;
    }
}
