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
/// 订货公式（F.3，fr_orderexp）：左侧订货参数（Nvalue 可编辑，失焦保存），
/// 右侧公式（Expna 可编辑；新增/修改弹小窗；basexp 系统公式禁止删除）。
/// </summary>
public partial class OrderExpViewModel : ObservableObject
{
    public ObservableCollection<OrderExpParaRow> Params { get; } = [];
    public ObservableCollection<OrderExpRow> Formulas { get; } = [];

    [ObservableProperty] private OrderExpRow? _selectedFormula;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    public async Task LoadedAsync(CancellationToken ct)
    {
        try
        {
            IsBusy = true;
            Message = "";
            var paras = await Db.OrderExp.GetParamsAsync(ct);
            Params.Clear();
            foreach (var p in paras) Params.Add(p);
            await ReloadFormulasAsync(ct);
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

    private async Task ReloadFormulasAsync(CancellationToken ct)
    {
        var rows = await Db.OrderExp.GetFormulasAsync(ct);
        Formulas.Clear();
        foreach (var f in rows) Formulas.Add(f);
        SelectedFormula = null;
    }

    /// <summary>参数值单元格提交（DataGrid 失焦提交）。</summary>
    public async Task SaveParamAsync(string paraId, decimal nValue)
    {
        try
        {
            Message = "";
            await Db.OrderExp.UpdateParamValueAsync(paraId, nValue);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "订货公式", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>公式名称单元格提交（表达式保持不变）。</summary>
    public async Task SaveFormulaNameAsync(int expId, string expNa, string cExpress)
    {
        if (string.IsNullOrWhiteSpace(expNa))
        {
            MessageBox.Show("公式名称不能为空。", "订货公式", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            Message = "";
            await Db.OrderExp.UpdateFormulaAsync(expId, cExpress, expNa);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "订货公式", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    public async Task AddFormulaAsync(CancellationToken ct)
    {
        var (ok, expna, cexpress) = ShowFormulaDialog("新增公式", "", "");
        if (!ok) return;
        try
        {
            IsBusy = true;
            Message = "";
            await Db.OrderExp.AddFormulaAsync(cexpress, expna, ct);
            await ReloadFormulasAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "订货公式", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task EditFormulaAsync(CancellationToken ct)
    {
        if (SelectedFormula is null)
        {
            MessageBox.Show("请先选择要修改的公式。", "订货公式", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var f = SelectedFormula;
        var (ok, expna, cexpress) = ShowFormulaDialog("修改公式", f.Expna, f.Cexpress);
        if (!ok) return;
        try
        {
            IsBusy = true;
            Message = "";
            await Db.OrderExp.UpdateFormulaAsync(f.Expid, cexpress, expna, ct);
            await ReloadFormulasAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "订货公式", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task DeleteFormulaAsync(CancellationToken ct)
    {
        if (SelectedFormula is null) return;
        var f = SelectedFormula;
        if (f.Basexp)
        {
            MessageBox.Show("系统公式不允许删除。", "订货公式", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show($"确认删除公式「{f.Expna}」吗？", "订货公式", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            IsBusy = true;
            Message = "";
            await Db.OrderExp.DeleteFormulaAsync(f.Expid, ct);
            await ReloadFormulasAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "订货公式", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>公式输入小窗（代码构建：expna + cexpress）。</summary>
    private static (bool Ok, string Expna, string Cexpress) ShowFormulaDialog(string title, string expna, string cexpress)
    {
        var expnaBox = new TextBox { Text = expna, Margin = new Thickness(0, 4, 0, 10), MaxLength = 20 };
        var cexpBox = new TextBox
        {
            Text = cexpress,
            Margin = new Thickness(0, 4, 0, 14),
            Height = 64,
            MaxLength = 200,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var win = new Window
        {
            Title = title,
            Width = 430,
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
            if (string.IsNullOrWhiteSpace(expnaBox.Text))
            {
                MessageBox.Show("公式名称不能为空。", title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            win.DialogResult = true;
        };

        var body = new StackPanel { Margin = new Thickness(16) };
        body.Children.Add(new TextBlock { Text = "公式名称" });
        body.Children.Add(expnaBox);
        body.Children.Add(new TextBlock { Text = "公式表达式" });
        body.Children.Add(cexpBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(okBtn);
        buttons.Children.Add(cancelBtn);
        body.Children.Add(buttons);
        win.Content = body;

        win.Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        var ok = win.ShowDialog() == true;
        return (ok, expnaBox.Text.Trim(), cexpBox.Text);
    }
}
