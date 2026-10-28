using System.Windows;
using System.Windows.Controls;

namespace QPGL.Desktop.Views;

/// <summary>
/// 收款导入输入窗（旧 fr_skdr 复刻，普通模式 M_GR=.F.）：
/// 旧为 Excel Append（4_.row5 XLS 列序 cno/ch/drmoney）；单店部署简化为手工多行录入，
/// 分析与回填走 XlskRepository.AnalyzeSkdrAsync（__.row34）→ XlskViewModel 回填网格（__.row6 L110-124）。
/// </summary>
public partial class SkdrImportWindow : Window
{
    private const int MaxRows = 10;

    public List<(string Cno, string Ch, decimal DrMoney)> InputRows { get; } = [];

    private readonly List<TextBox> _cnoBoxes = [];
    private readonly List<TextBox> _chBoxes = [];
    private readonly List<TextBox> _amtBoxes = [];

    public SkdrImportWindow()
    {
        InitializeComponent();
        AddRow();
    }

    private void AddRow()
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cno = new TextBox { Margin = new Thickness(0, 0, 4, 0), MaxLength = 15 };
        var ch = new TextBox { Margin = new Thickness(0, 0, 4, 0), MaxLength = 12 };
        var amt = new TextBox { Margin = new Thickness(0), MaxLength = 14 };
        Grid.SetColumn(cno, 0);
        Grid.SetColumn(ch, 1);
        Grid.SetColumn(amt, 2);
        grid.Children.Add(cno);
        grid.Children.Add(ch);
        grid.Children.Add(amt);
        _cnoBoxes.Add(cno);
        _chBoxes.Add(ch);
        _amtBoxes.Add(amt);
        RowsPanel.Children.Add(grid);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        InputRows.Clear();
        for (var i = 0; i < _cnoBoxes.Count; i++)
        {
            var cno = _cnoBoxes[i].Text.Trim();
            if (cno.Length == 0) continue;
            if (!decimal.TryParse(_amtBoxes[i].Text.Trim(), out var amt) || amt <= 0)
            {
                MessageBox.Show($"第 {i + 1} 行金额输入不正确", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            InputRows.Add((cno, _chBoxes[i].Text.Trim(), amt));
        }
        DialogResult = InputRows.Count > 0;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
