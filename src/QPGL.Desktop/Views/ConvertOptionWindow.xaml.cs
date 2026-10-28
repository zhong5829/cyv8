using System.Windows;

namespace QPGL.Desktop.Views;

/// <summary>报价转销售三选项弹窗（frmbjtoxs.Optgp1 复刻；P_IFCK='2' 才显示急件项，本部署 '1' 隐藏）。</summary>
public partial class ConvertOptionWindow : Window
{
    private ConvertOptionWindow()
    {
        InitializeComponent();
        // P_IFCK='1'（缺省）：OPTION3 急件隐藏（部署实测 tbsysset 无"出库"xs 行）
        Rb3.Visibility = Visibility.Collapsed;
        Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
    }

    /// <summary>返回用户选择的 1/2/3；取消返回 null。</summary>
    public static int? Ask(Window? owner)
    {
        var win = new ConvertOptionWindow();
        if (owner is not null) win.Owner = owner;
        return win.ShowDialog() == true ? (win.Rb2.IsChecked == true ? 2 : win.Rb3.IsChecked == true ? 3 : 1) : null;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
