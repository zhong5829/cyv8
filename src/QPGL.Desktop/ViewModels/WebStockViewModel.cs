using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Orders;

namespace QPGL.Desktop.ViewModels;

/// <summary>
/// 库存上网（F.6，fr_kc；表 tbkc）：从 tbisto 聚合生成/刷新，mark 上网标记可切换、可全置，
/// 行编辑仅回写 Nno/Ty/Dc/Nnp 四列，支持删除单条。
/// </summary>
public partial class WebStockViewModel : ObservableObject
{
    public ObservableCollection<WebStockRow> Rows { get; } = [];

    [ObservableProperty] private string _nnoPrefix = "";
    [ObservableProperty] private string _na1Contains = "";
    [ObservableProperty] private WebStockRow? _selectedRow;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    public async Task LoadedAsync(CancellationToken ct)
    {
        try
        {
            IsBusy = true;
            await SearchCoreAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct)
    {
        try
        {
            IsBusy = true;
            await SearchCoreAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SearchCoreAsync(CancellationToken ct)
    {
        Message = "";
        var rows = await Db.WebStock.SearchAsync(NnoPrefix, Na1Contains, ct);
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        SelectedRow = null;
        if (rows.Count == 0) Message = "未找到符合条件的上网库存。";
    }

    /// <summary>从库存（tbisto 聚合）生成/刷新上网库存。</summary>
    [RelayCommand]
    private async Task GenerateAsync(CancellationToken ct)
    {
        if (MessageBox.Show("确认从库存（入库流水）重新生成/刷新上网库存吗？\n已存在的编号将更新售价与数量，新编号将插入。",
                "库存上网", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            IsBusy = true;
            Message = "";
            var (inserted, updated) = await Db.WebStock.GenerateAsync(ct);
            MessageBox.Show($"生成完成：插入 {inserted} 行，更新 {updated} 行。",
                "库存上网", MessageBoxButton.OK, MessageBoxImage.Information);
            await SearchCoreAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "库存上网", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task MarkAllOnAsync(CancellationToken ct)
    {
        if (MessageBox.Show("确认将全部库存记录的上网标记置为 √ 吗？",
                "库存上网", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            IsBusy = true;
            Message = "";
            await Db.WebStock.SetMarkAllAsync(true, ct);
            Message = "已全部置√。";
            await SearchCoreAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "库存上网", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task MarkAllOffAsync(CancellationToken ct)
    {
        if (MessageBox.Show("确认将全部库存记录的上网标记置灰（取消上网）吗？",
                "库存上网", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            IsBusy = true;
            Message = "";
            await Db.WebStock.SetMarkAllAsync(false, ct);
            Message = "已全部置灰。";
            await SearchCoreAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "库存上网", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>编辑行（仅 Nno/Ty/Dc/Nnp 四项回写）。</summary>
    [RelayCommand]
    private async Task EditRowAsync(CancellationToken ct)
    {
        if (SelectedRow is null)
        {
            MessageBox.Show("请先选择要编辑的行。", "库存上网", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var r = SelectedRow;
        var (ok, nno, ty, dc, nnp) = ShowRowDialog("编辑上网库存", r);
        if (!ok) return;
        try
        {
            IsBusy = true;
            Message = "";
            await Db.WebStock.UpdateRowAsync(r.Id, nno, ty, dc, nnp, ct);
            await SearchCoreAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "库存上网", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(CancellationToken ct)
    {
        if (SelectedRow is null) return;
        var r = SelectedRow;
        if (MessageBox.Show($"确认删除配件 {r.Nno}（{r.Na1}）的上网记录吗？",
                "库存上网", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            IsBusy = true;
            Message = "";
            await Db.WebStock.DeleteAsync(r.Id, ct);
            await SearchCoreAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "库存上网", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>勾选/取消上网标记（复选框点击回调）。</summary>
    public async Task ToggleMarkAsync(WebStockRow row)
    {
        try
        {
            Message = "";
            await Db.WebStock.ToggleMarkAsync(row.Id, row.Mark);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "库存上网", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>行编辑小窗（仅 Nno/Ty/Dc/Nnp 四项）。</summary>
    private static (bool Ok, string Nno, string Ty, string Dc, string Nnp) ShowRowDialog(string title, WebStockRow row)
    {
        var nnoBox = new TextBox { Text = row.Nno, Width = 200, HorizontalAlignment = HorizontalAlignment.Left, MaxLength = 18, Margin = new Thickness(0, 4, 0, 0) };
        var tyBox = new TextBox { Text = row.Ty, Width = 200, HorizontalAlignment = HorizontalAlignment.Left, MaxLength = 14, Margin = new Thickness(0, 4, 0, 0) };
        var dcBox = new TextBox { Text = row.Dc, Width = 200, HorizontalAlignment = HorizontalAlignment.Left, MaxLength = 8, Margin = new Thickness(0, 4, 0, 0) };
        var nnpBox = new TextBox { Text = row.Nnp, Width = 200, HorizontalAlignment = HorizontalAlignment.Left, MaxLength = 16, Margin = new Thickness(0, 4, 0, 0) };

        var win = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        var okBtn = new Button { Content = "确定", IsDefault = true, Padding = new Thickness(22, 4, 22, 4), Margin = new Thickness(0, 0, 10, 0) };
        var cancelBtn = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(22, 4, 22, 4) };
        if (Application.Current.TryFindResource("PrimaryButtonStyle") is Style primary) okBtn.Style = primary;

        okBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nnoBox.Text))
            {
                MessageBox.Show("配件编号不能为空。", title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            win.DialogResult = true;
        };

        var body = new StackPanel { Margin = new Thickness(16) };
        body.Children.Add(new TextBlock { Text = "配件编号" });
        body.Children.Add(nnoBox);
        body.Children.Add(new TextBlock { Text = "车型 Ty" });
        body.Children.Add(tyBox);
        body.Children.Add(new TextBlock { Text = "正厂 Dc" });
        body.Children.Add(dcBox);
        body.Children.Add(new TextBlock { Text = "上网编码 Nnp" });
        body.Children.Add(nnpBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(okBtn);
        buttons.Children.Add(cancelBtn);
        body.Children.Add(buttons);
        win.Content = body;

        win.Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        var ok = win.ShowDialog() == true;
        return (ok, nnoBox.Text.Trim(), tyBox.Text.Trim(), dcBox.Text.Trim(), nnpBox.Text.Trim());
    }
}
