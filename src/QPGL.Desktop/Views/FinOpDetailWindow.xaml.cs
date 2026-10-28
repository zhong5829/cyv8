using System.Collections.ObjectModel;
using System.Windows;
using QPGL.Infrastructure.Finance;

namespace QPGL.Desktop.Views;

/// <summary>业务明细窗（fr_finopdet 复刻，只读；合计=Σ数量/Σ折前/Σ折后）。</summary>
public partial class FinOpDetailWindow : Window
{
    public FinOpDetailWindow(string opty, string opercode, DateTime date, IReadOnlyList<FinOpDetailRow> rows)
    {
        InitializeComponent();
        Title = opty == "BBI" ? "入库明细清单" : "销售明细清单";
        DataContext = new
        {
            Title,
            Header = $"{opty} {opercode}（{date:yyyy-MM-dd}） 共 {rows.Count} 行",
            Rows = new ObservableCollection<FinOpDetailRow>(rows),
            Summary = $"合计：数量 {rows.Sum(r => r.Amount):N2}　折前 {rows.Sum(r => r.Amount * r.Oprc):N2}　折后 {rows.Sum(r => r.Amount * r.Oprct * (100 - r.Yhl) / 100):N2}",
        };
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
