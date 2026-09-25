using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

internal static class KeyboardTarget
{
    internal static void Run()
    {
        var thread = new Thread(() =>
        {
            var application = new Application();
            var input = new TextBox { FontSize = 20 };
            var window = new Window
            {
                Title = "FlowKey cross-process keyboard target",
                Width = 600, Height = 300, Content = input
            };
            window.Loaded += (_, _) =>
            {
                input.Focus();
                Console.WriteLine(new WindowInteropHelper(window).Handle);
                Console.Out.Flush();
            };
            input.TextChanged += (_, _) => { Console.WriteLine("TEXT:" + input.Text); Console.Out.Flush(); };
            application.Run(window);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }
}
