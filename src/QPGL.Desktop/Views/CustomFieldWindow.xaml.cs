using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.Services;
using QPGL.Infrastructure.Query;

namespace QPGL.Desktop.Views;

/// <summary>自定义字段完成产物：新增列 + 行回填动作。</summary>
public sealed class CustomFieldOutcome
{
    public List<QueryColumnSpec> NewColumns { get; init; } = [];
    public Action<IReadOnlyList<QueryRow>> Apply { get; init; } = _ => { };
}

/// <summary>
/// fr_mdefi 模式选择页（FrmMain）：①本地（表达式加列，纯客户端）②系统表（GROUP BY 聚合关联回填，发 SQL）③外部文件（CSV，XLS/DBF 备案）。
/// </summary>
public partial class CustomFieldWindow : Window
{
    private readonly IReadOnlyList<QueryRow> _rows;
    private readonly IReadOnlyList<QueryColumnSpec> _columns;
    private readonly Func<Task<IReadOnlyList<SysTableInfo>>> _getSysTables;
    private readonly Func<string, Task<IReadOnlyList<SysFieldInfo>>> _getSysFields;
    private readonly Func<string, IReadOnlyList<AggregateItem>, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>> _aggregateSys;
    private readonly Func<Task<string>> _pickCsvFile;
    private readonly Func<string, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>> _loadCsv;

    public CustomFieldOutcome? Outcome { get; private set; }

    public CustomFieldWindow(
        IReadOnlyList<QueryRow> rows,
        IReadOnlyList<QueryColumnSpec> columns,
        Func<Task<IReadOnlyList<SysTableInfo>>> getSysTables,
        Func<string, Task<IReadOnlyList<SysFieldInfo>>> getSysFields,
        Func<string, IReadOnlyList<AggregateItem>, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>> aggregateSys,
        Func<Task<string>> pickCsvFile,
        Func<string, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>> loadCsv)
    {
        InitializeComponent();
        _rows = rows;
        _columns = columns;
        _getSysTables = getSysTables;
        _getSysFields = getSysFields;
        _aggregateSys = aggregateSys;
        _pickCsvFile = pickCsvFile;
        _loadCsv = loadCsv;
    }

    private void Opt_Checked(object sender, RoutedEventArgs e)
    {
        if (TxtNote is null) return;
        TxtNote.Text = OptLocal.IsChecked == true ? "    在本地数据库的字段或增加新字段的计算"
            : OptSys.IsChecked == true ? "    引用在本系统的经常用的表字段或增加新字段的计算"
            : "    引用外部数据的字段或增加新字段的计算";
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private async void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            MessageBox.Show("请先查询数据", "提示");
            return;
        }
        if (OptLocal.IsChecked == true)
        {
            var local = new CustomFieldLocalWindow(_rows, _columns) { Owner = this };
            if (local.ShowDialog() == true) { Outcome = local.Outcome; DialogResult = true; }
            return;
        }
        if (OptSys.IsChecked == true)
        {
            var sys = new CustomFieldSysWindow(_rows, _getSysTables, _getSysFields, _aggregateSys) { Owner = this };
            if (sys.ShowDialog() == true) { Outcome = sys.Outcome; DialogResult = true; }
            return;
        }
        // 模式③外部 CSV
        var file = await _pickCsvFile();
        if (string.IsNullOrEmpty(file)) return;
        var ext = new CustomFieldExtWindow(_rows, file, _loadCsv) { Owner = this };
        if (ext.ShowDialog() == true) { Outcome = ext.Outcome; DialogResult = true; }
    }
}
