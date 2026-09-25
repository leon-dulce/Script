using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace FlowKey.Desktop;

internal enum RunResult { Completed, Stopped, Interrupted, Failed }

internal sealed class CompletionOverlay : Window
{
    private readonly DispatcherTimer _timer;

    internal RunResult Result { get; }
    internal bool HasBanner { get; }
    internal bool HasBorder { get; }

    internal CompletionOverlay(nint target, RunResult result, string scriptName, long iterations, string explanation,
        bool banner, bool border, int durationSeconds)
    {
        Result = result;
        HasBanner = banner;
        HasBorder = border;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        Width = Height = 1;

        var accent = result switch
        {
            RunResult.Completed => Color.FromRgb(63, 221, 170),
            RunResult.Failed => Color.FromRgb(255, 112, 130),
            _ => Color.FromRgb(255, 194, 96)
        };
        var grid = new Grid { IsHitTestVisible = false };
        if (border)
            grid.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(accent), BorderThickness = new Thickness(7),
                CornerRadius = new CornerRadius(15), Margin = new Thickness(8),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = accent, BlurRadius = 24, ShadowDepth = 0, Opacity = 0.85 }
            });
        if (banner)
        {
            var title = result switch
            {
                RunResult.Completed => "腳本執行完成",
                RunResult.Failed => "腳本執行失敗",
                RunResult.Interrupted => "腳本已中斷",
                _ => "腳本已停止"
            };
            var copy = new StackPanel();
            copy.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(accent) });
            copy.Children.Add(new TextBlock { Text = result == RunResult.Completed
                    ? $"{scriptName} · 共執行 {iterations} 輪" : scriptName,
                Margin = new Thickness(0, 5, 0, 0), FontSize = 14, Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 410 });
            if (result != RunResult.Completed)
                copy.Children.Add(new TextBlock { Text = explanation, Margin = new Thickness(0, 4, 0, 0),
                    FontSize = 12, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 410, MaxHeight = 44 });
            grid.Children.Add(new Border
            {
                Child = copy, Background = new SolidColorBrush(Color.FromRgb(28, 35, 48)),
                BorderBrush = new SolidColorBrush(accent), BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(12), Padding = new Thickness(20, 15, 20, 15),
                MinWidth = 280, MaxWidth = 470, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(24)
            });
        }
        Content = grid;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(handle, -20);
            SetWindowLongPtr(handle, -20, style | 0x20 | 0x80 | 0x08000000); // transparent, tool window, no activate
            var anchor = target != 0 && Native.IsWindow(target) ? target : Native.GetForegroundWindow();
            var monitor = MonitorFromWindow(anchor != 0 ? anchor : handle, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
                SetWindowPos(handle, -1, info.Monitor.Left, info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, 0x10 | 0x40);
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(durationSeconds) };
        _timer.Tick += (_, _) => Close();
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
