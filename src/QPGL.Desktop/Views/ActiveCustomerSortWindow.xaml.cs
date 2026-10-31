using System.Windows;

namespace QPGL.Desktop.Views;

/// <summary>活跃客户自定义排序输入窗：多行粘贴客户名单（一行一个），确定后用名单覆盖面板。</summary>
public partial class ActiveCustomerSortWindow : Window
{
    /// <summary>用户输入的客户名单原文。</summary>
    public string NamesText { get; private set; } = "";

    public ActiveCustomerSortWindow()
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        Loaded += (_, _) =>
        {
            NamesBox.CaretIndex = 0;   // 光标置于首行开头
            NamesBox.Focus();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        NamesText = NamesBox.Text;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
