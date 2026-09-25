using System.Windows;
using System.Windows.Interop;

namespace FlowKey.Desktop;

public partial class FlowDialog : Window
{
    public FlowDialog(Window owner, string message, bool confirmation)
    {
        InitializeComponent();
        Owner = owner;
        Message.Text = message;
        Heading.Text = confirmation ? "請確認" : "請留意一下";
        CancelAction.Visibility = confirmation ? Visibility.Visible : Visibility.Collapsed;
        AcceptAction.Content = confirmation ? "確定" : "知道了";
        if (confirmation && message.StartsWith("刪除「"))
        { Heading.Text = "刪除這個腳本？"; AcceptAction.Content = "刪除腳本"; }
        else if (confirmation && message.StartsWith("放棄這次"))
        { Heading.Text = "不儲存這次錄製？"; AcceptAction.Content = "放棄錄製"; CancelAction.Content = "繼續命名"; }
        else if (confirmation && message.StartsWith("剛才的修改"))
        { Heading.Text = "修改還沒儲存"; AcceptAction.Content = "放棄並切換"; CancelAction.Content = "留在這裡"; }
        AcceptAction.IsDefault = !confirmation;
        Loaded += (_, _) =>
        {
            WindowTheme.Apply(new WindowInteropHelper(this).Handle);
            if (confirmation) CancelAction.Focus(); else AcceptAction.Focus();
        };
    }

    private void AcceptClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    internal static bool Ask(Window owner, string message, bool confirmation = false) =>
        new FlowDialog(owner, message, confirmation).ShowDialog() == true;
}
