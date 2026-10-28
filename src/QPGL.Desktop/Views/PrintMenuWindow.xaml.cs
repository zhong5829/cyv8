using System.Windows;
using System.Windows.Controls;

namespace QPGL.Desktop.Views;

/// <summary>POPMENU 复刻（row59 等）：打印方式选择弹菜单，BAR()=选中项 1..N。</summary>
public partial class PrintMenuWindow : Window
{
    /// <summary>选中项（1 起），未选为 null。</summary>
    public int? SelectedBar { get; private set; }

    public PrintMenuWindow(IReadOnlyList<string> items)
    {
        InitializeComponent();
        var buttons = new[] { Btn1, Btn2, Btn3 };
        for (var i = 0; i < buttons.Length; i++)
        {
            if (i < items.Count)
            {
                buttons[i].Content = items[i];
                buttons[i].Visibility = Visibility.Visible;
            }
            else
            {
                buttons[i].Visibility = Visibility.Collapsed;
            }
        }
    }

    private void Btn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string t } && int.TryParse(t, out var bar))
            SelectedBar = bar;
        DialogResult = true;
    }
}
