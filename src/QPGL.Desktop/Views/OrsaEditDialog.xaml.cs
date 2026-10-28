using System.Windows;
using System.Windows.Controls;
using QPGL.Infrastructure.Sales;

namespace QPGL.Desktop.Views;

/// <summary>
/// 报价新增/编辑弹窗（fr_orsa row58 FRMEDIT 复刻）：
/// 新增（EMODE=0）：单位编号/名称 + 零件编码（失焦从 tbprnoty 带出名称，禁手工改）+ 数量/单价；
/// 编辑（EMODE=1）：仅报价数量/单价可改（row8 UPDATE tbqtgs SET qtamount,oprc）。
/// </summary>
public partial class OrsaEditDialog : Window
{
    /// <summary>确认后的保存命令（null=取消）。</summary>
    public OrsaSaveCommand? Result { get; private set; }

    private readonly OrsaRow? _editRow;

    private OrsaEditDialog(OrsaRow? editRow)
    {
        _editRow = editRow;
        InitializeComponent();
        Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (editRow is not null)
        {
            Title = "报价编辑";
            TxtGno.Text = editRow.Gno;
            TxtGname.Text = editRow.Gname;
            TxtNno.Text = editRow.Nno;
            TxtNa1.Text = editRow.Na1;
            TxtQty.Text = editRow.Qtamount.ToString("0.##");
            TxtOprc.Text = editRow.Oprc.ToString("N2");
            // EMODE=1：单位/零件不可改（旧只 UPDATE 数量/单价）
            TxtGno.IsEnabled = false;
            TxtGname.IsEnabled = false;
            TxtNno.IsEnabled = false;
        }
    }

    /// <summary>editRow=null → 新增；返回 (确认?, 命令)。</summary>
    public static (bool Ok, OrsaSaveCommand? Command) Edit(OrsaRow? editRow, string cp)
    {
        var win = new OrsaEditDialog(editRow) { Tag = cp };
        return win.ShowDialog() == true ? (true, win.Result) : (false, null);
    }

    private async void TxtNno_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_editRow is not null) return;   // 编辑态锁定
        var cp = Tag as string ?? "";
        try
        {
            var rows = await Services.Db.Orsa.SearchAsync(
                $"cp = '{cp.Replace("'", "''")}' AND nno = '{TxtNno.Text.Trim().Replace("'", "''")}'");
            if (rows.Count > 0)
            {
                TxtNa1.Text = rows[0].Na1;
                return;
            }
        }
        catch { /* 带出失败不阻断 */ }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var gno = TxtGno.Text.Trim();
        var gname = TxtGname.Text.Trim();
        var nno = TxtNno.Text.Trim();
        if (gname.Length == 0) { Warn("请输入单位名称！"); return; }
        if (nno.Length == 0) { Warn("请输入零件编码！"); return; }
        if (!decimal.TryParse(TxtQty.Text.Trim(), out var qty)) { Warn("报价数量格式不正确"); return; }
        if (!decimal.TryParse(TxtOprc.Text.Trim(), out var oprc)) { Warn("报价单价格式不正确"); return; }
        Result = new OrsaSaveCommand { Gno = gno, Gname = gname, Nno = nno, Na1 = TxtNa1.Text, Qtamount = qty, Oprc = oprc };
        DialogResult = true;
    }

    private void Warn(string msg) =>
        MessageBox.Show(msg, "报价编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
}
