using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Desktop.Views;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Sales;

namespace QPGL.Desktop.ViewModels;

/// <summary>
/// 其他公司报价（C5，fr_orsa；表 tbqtgs）：条件查询（nno/gname）、新增/编辑（弹窗：编码失焦带出名称/车型）、
/// 删除单条、按供应商整删（确认后）、明细面板（本公司库存 + 同件全部报价）。
/// 查询条件由仓储侧白名单拼 where（fr_orsa setsource PASSTH 语义，输入直接是 SQL 片段）。
/// </summary>
public partial class OrsaViewModel : ObservableObject
{
    public ObservableCollection<OrsaRow> Rows { get; } = [];
    public ObservableCollection<OrsaDetailRow> DetailRows { get; } = [];

    [ObservableProperty] private string _nnoFilter = "";
    [ObservableProperty] private string _gnameFilter = "";
    [ObservableProperty] private OrsaRow? _selectedRow;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct)
    {
        Message = "";
        IsBusy = true;
        try
        {
            var where = BuildWhere();
            var rows = await Db.Orsa.SearchAsync(where, ct);
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            if (Rows.Count == 0) Message = "没有符合条件的记录！";
        }
        catch (Exception ex) { Message = ex is AppException ae ? ae.Message : "查询失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>查询条件（fr_orsa setsource：用户输入拼 nno/gname LIKE；空=全部）。</summary>
    private string BuildWhere()
    {
        var parts = new List<string>();
        var nno = NnoFilter.Trim();
        var gname = GnameFilter.Trim();
        if (nno.Length > 0) parts.Add($"nno LIKE '%{nno.Replace("'", "''")}%'");
        if (gname.Length > 0) parts.Add($"gname LIKE '%{gname.Replace("'", "''")}%'");
        return string.Join(" AND ", parts);
    }

    [RelayCommand]
    private async Task AddAsync(CancellationToken ct)
    {
        var (ok, cmd) = OrsaEditDialog.Edit(null, Cp);
        if (!ok || cmd is null) return;
        try
        {
            var row = await Db.Orsa.CreateAsync(cmd, Cp, ct);
            Message = $"报价已新增（id={row.Id}）";
            await SearchAsync(ct);
        }
        catch (Exception ex) { Message = ex is AppException ae ? ae.Message : "新增失败：" + ex.Message; }
    }

    [RelayCommand]
    private async Task EditAsync(CancellationToken ct)
    {
        if (SelectedRow is null) { Message = "请先选择记录"; return; }
        var (ok, cmd) = OrsaEditDialog.Edit(SelectedRow, Cp);
        if (!ok || cmd is null) return;
        try
        {
            await Db.Orsa.UpdateAsync(SelectedRow.Id, cmd.Qtamount, cmd.Oprc, ct);
            Message = "报价已修改";
            await SearchAsync(ct);
        }
        catch (Exception ex) { Message = ex is AppException ae ? ae.Message : "修改失败：" + ex.Message; }
    }

    [RelayCommand]
    private async Task DeleteAsync(CancellationToken ct)
    {
        if (SelectedRow is null) { Message = "请先选择记录"; return; }
        var row = SelectedRow;
        if (MessageBox.Show($"删除 {row.Gname} 的零件 {row.Nno} 报价？", "删除确认",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await Db.Orsa.DeleteAsync(row.Id, ct);
            Message = "已删除";
            await SearchAsync(ct);
        }
        catch (Exception ex) { Message = "删除失败：" + ex.Message; }
    }

    /// <summary>按供应商整删（row51：DELETE tbqtgs WHERE gname；先确认）。</summary>
    [RelayCommand]
    private async Task DeleteByGnameAsync(CancellationToken ct)
    {
        var gname = PromptGname("按供应商删除报价", "供应商名称：");
        if (string.IsNullOrEmpty(gname)) return;
        if (MessageBox.Show($"删除「{gname}」的全部报价记录？不可恢复。", "整删确认",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var n = await Db.Orsa.DeleteByGnameAsync(gname, ct);
            Message = $"已删除 {n} 条「{gname}」的报价";
            await SearchAsync(ct);
        }
        catch (Exception ex) { Message = "删除失败：" + ex.Message; }
    }

    /// <summary>明细面板（row29 AfterRowColChange：选中行自动带出本公司库存+同件全部报价）。</summary>
    public async Task LoadDetailAsync(OrsaRow? row, CancellationToken ct = default)
    {
        DetailRows.Clear();
        if (row is null) return;
        try
        {
            var rows = await Db.Orsa.DetailAsync(Cp, row.Nno, ct);
            foreach (var d in rows) DetailRows.Add(d);
        }
        catch { /* 明细加载失败不阻断列表 */ }
    }

    /// <summary>单行输入小窗（按供应商整删用）。</summary>
    private string? PromptGname(string title, string label)
    {
        var box = new System.Windows.Controls.TextBox { Width = 220, Margin = new Thickness(0, 4, 0, 0) };
        var win = new Window
        {
            Title = title, Width = 380, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };
        string? result = null;
        var ok = new System.Windows.Controls.Button { Content = "确定", IsDefault = true, Padding = new Thickness(22, 4, 22, 4) };
        if (Application.Current.TryFindResource("PrimaryButtonStyle") is Style s) ok.Style = s;
        ok.Click += (_, _) => { if (string.IsNullOrWhiteSpace(box.Text)) return; result = box.Text.Trim(); win.DialogResult = true; };
        var cancel = new System.Windows.Controls.Button { Content = "取消", IsCancel = true, Padding = new Thickness(22, 4, 22, 4), Margin = new Thickness(10, 0, 0, 0) };
        var body = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        body.Children.Add(new System.Windows.Controls.TextBlock { Text = label });
        body.Children.Add(box);
        var btns = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        body.Children.Add(btns);
        win.Content = body;
        win.Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        return win.ShowDialog() == true ? result : null;
    }

    /// <summary>当前厂牌（tbqtgs.cp，fr_orsa p_cp 语义 = 厂牌会话 P_CP）。</summary>
    private static string Cp => AppSession.Brand;
}
