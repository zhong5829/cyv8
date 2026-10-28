using System.Windows;
using System.Windows.Controls;

namespace QPGL.Desktop.Views;

/// <summary>通用单值输入对话框（替代 VB InputBox，net8 WPF 无内置实现）。</summary>
public partial class PromptWindow : Window
{
    public string Prompt { get; }
    public string Input { get; set; } = "";
    public bool Confirmed { get; private set; }

    public PromptWindow(string prompt, string defaultValue = "")
    {
        Prompt = prompt;
        Input = defaultValue;
        InitializeComponent();
        DataContext = this;
        Owner = Application.Current.MainWindow;
        Loaded += (_, _) =>
        {
            var tb = (TextBox)((Panel)Content).Children[1];
            tb.SelectAll();
            tb.Focus();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    /// <summary>便捷调用：返回输入值（取消返回 null）。</summary>
    public static string? Ask(string prompt, string defaultValue = "")
    {
        var w = new PromptWindow(prompt, defaultValue);
        w.ShowDialog();
        return w.Confirmed ? w.Input : null;
    }
}
