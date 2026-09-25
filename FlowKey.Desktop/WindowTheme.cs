using System.Runtime.InteropServices;

namespace FlowKey.Desktop;

internal static class WindowTheme
{
    internal const int CaptionColor = 0x00201A17; // COLORREF: #171A20
    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    internal static int Apply(nint window)
    {
        // Older Windows versions may reject individual attributes; keep native window controls usable.
        int dark = 1, caption = CaptionColor, text = 0x00F0E9E5, border = 0x00463A34;
        DwmSetWindowAttribute(window, 20, ref dark, sizeof(int));
        var result = DwmSetWindowAttribute(window, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(window, 36, ref text, sizeof(int));
        DwmSetWindowAttribute(window, 34, ref border, sizeof(int));
        return result;
    }
}
