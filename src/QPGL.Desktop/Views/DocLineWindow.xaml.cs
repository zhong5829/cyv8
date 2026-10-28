using System.Collections.ObjectModel;
using System.Windows;
using QPGL.Desktop.Services;
using QPGL.Domain.Query;

namespace QPGL.Desktop.Views;

/// <summary>出库单明细窗（旧 fr_sesada 明细展开语义：WHERE paper+type+dpm）。信息头直接赋值控件。</summary>
public partial class DocLineWindow : Window
{
    public ObservableCollection<OutDocLine> Lines { get; } = [];

    private readonly string _dpm;

    public DocLineWindow(string paper, string type, string dpm)
    {
        _dpm = dpm;
        InitializeComponent();
        TxtPaper.Text = paper;
        TxtType.Text = type;
        LinesGrid.ItemsSource = Lines;
        Loaded += async (_, _) => await LoadAsync(paper, type);
    }

    private async System.Threading.Tasks.Task LoadAsync(string paper, string type)
    {
        try
        {
            var summary = await Db.DocQuery.GetOutDocSummaryAsync(paper, type, _dpm);
            TxtDate.Text = summary.Outdate.ToString("yyyy-MM-dd");
            TxtGname.Text = summary.Gname;
            TxtMark.Text = summary.Mark.Trim() == "Y" ? $"已收款（{summary.Guno}）" : "未收款";
            Lines.Clear();
            var lines = await Db.DocQuery.GetOutDocLinesAsync(paper, type, _dpm);
            foreach (var l in lines) Lines.Add(l);
            Title = $"单据明细 - {type} {paper}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex is QPGL.Domain.Common.AppException ae ? ae.Message : "加载明细失败：" + ex.Message,
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
