using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Infrastructure.Orders;

namespace QPGL.Desktop.ViewModels;

/// <summary>供应商下拉项（tbgugys）。</summary>
public sealed record SupplierOption(string Gyno, string Gyname)
{
    public override string ToString() => $"{Gyno}  {Gyname}";
}

/// <summary>
/// 供应商价格（F.5，fr_gysjg；表 tbgys）：按编号前缀/供应商查询，
/// 支持新增/修改（弹窗：编号失焦带出名称、供应商/备注类型下拉）、删除单条、按供应商全删。
/// </summary>
public partial class SupplierPriceViewModel : ObservableObject
{
    public ObservableCollection<SupplierPriceRow> Rows { get; } = [];
    public ObservableCollection<SupplierOption> SupplierOptions { get; } = [];
    public ObservableCollection<string> BzOptions { get; } = [];

    [ObservableProperty] private string _nnoPrefix = "";
    [ObservableProperty] private string _gyname = "";
    [ObservableProperty] private SupplierPriceRow? _selectedRow;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    public async Task LoadedAsync(CancellationToken ct)
    {
        try
        {
            IsBusy = true;
            Message = "";
            var suppliers = await Db.SupplierPrices.GetSupplierOptionsAsync(ct);
            SupplierOptions.Clear();
            foreach (var s in suppliers) SupplierOptions.Add(new SupplierOption(s.Gyno, s.Gyname));
            var bz = await Db.SupplierPrices.GetBzOptionsAsync(ct);
            BzOptions.Clear();
            foreach (var b in bz) BzOptions.Add(b);
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
        var rows = await Db.SupplierPrices.SearchAsync(NnoPrefix, Gyname, ct);
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        SelectedRow = null;
        if (rows.Count == 0) Message = "未找到符合条件的供应商价格。";
    }

    [RelayCommand]
    private async Task AddAsync(CancellationToken ct)
    {
        var (ok, input) = ShowPriceDialog("新增供应商价格", new SupplierPriceInput { Cp = AppSession.Brand }, "", null, null);   // 旧 m.CP=P_CP（F组小表单契约）
        if (!ok || input is null) return;
        try
        {
            IsBusy = true;
            Message = "";
            await Db.SupplierPrices.InsertAsync(input, ct);
            await SearchCoreAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "供应商价格", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task EditAsync(CancellationToken ct)
    {
        if (SelectedRow is null)
        {
            MessageBox.Show("请先选择要修改的价格行。", "供应商价格", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var r = SelectedRow;
        var initial = new SupplierPriceInput
        {
            Cp = r.Cp, Nno = r.Nno, Oenno = r.Oenno, Zq = r.Zq, Iprj = r.Iprj,
            Gyno = r.Gyno, Gyname = r.Gyname, Bzlx = r.Bzlx,
        };
        var (ok, input) = ShowPriceDialog("修改供应商价格", initial, r.Na1,
            SupplierOptions.FirstOrDefault(o => o.Gyname == r.Gyname),
            BzOptions.Contains(r.Bzlx) ? r.Bzlx : null);
        if (!ok || input is null) return;
        try
        {
            IsBusy = true;
            Message = "";
            await Db.SupplierPrices.UpdateAsync(r.Id, input, ct);
            await SearchCoreAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "供应商价格", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        if (MessageBox.Show($"确认删除配件 {r.Nno}（供应商 {r.Gyname}）的价格记录吗？",
                "供应商价格", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            IsBusy = true;
            Message = "";
            await Db.SupplierPrices.DeleteAsync(r.Id, ct);
            await SearchCoreAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "供应商价格", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteByGynameAsync(CancellationToken ct)
    {
        var gyname = ShowInputBox("按供应商删除价格", "供应商名称", "");
        if (gyname is null) return;
        if (MessageBox.Show($"确认删除供应商「{gyname}」的全部价格记录吗？该操作不可恢复！",
                "供应商价格", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            IsBusy = true;
            Message = "";
            await Db.SupplierPrices.DeleteByGynameAsync(gyname, ct);
            MessageBox.Show("删除完成。", "供应商价格", MessageBoxButton.OK, MessageBoxImage.Information);
            await SearchCoreAsync(ct);
        }
        catch (AppException ex)
        {
            Message = ex.Message;
            MessageBox.Show(ex.Message, "供应商价格", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>价格输入小窗（厂牌默认中驭；编号失焦带出名称；供应商/备注类型下拉）。</summary>
    private (bool Ok, SupplierPriceInput? Input) ShowPriceDialog(string title, SupplierPriceInput initial,
        string initialNa1, SupplierOption? selectedSupplier, string? selectedBz)
    {
        var cpBox = new TextBox { Text = initial.Cp, Width = 200, HorizontalAlignment = HorizontalAlignment.Left, MaxLength = 10 };
        var nnoBox = new TextBox { Text = initial.Nno, Width = 200, HorizontalAlignment = HorizontalAlignment.Left, MaxLength = 18 };
        var na1Text = new TextBlock { Text = initialNa1, VerticalAlignment = VerticalAlignment.Center };
        var oennoBox = new TextBox { Text = initial.Oenno, Width = 200, HorizontalAlignment = HorizontalAlignment.Left, MaxLength = 18 };
        var zqBox = new TextBox { Text = initial.Zq.ToString(), Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        var iprjBox = new TextBox { Text = initial.Iprj.ToString(), Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        var supplierBox = new ComboBox { ItemsSource = SupplierOptions, SelectedItem = selectedSupplier, Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        var bzBox = new ComboBox { ItemsSource = BzOptions, SelectedItem = selectedBz, Width = 200, HorizontalAlignment = HorizontalAlignment.Left };

        nnoBox.LostFocus += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nnoBox.Text)) { na1Text.Text = ""; return; }
            try
            {
                na1Text.Text = await Db.SupplierPrices.GetPartNameAsync(nnoBox.Text.Trim()) ?? "";
            }
            catch (AppException ex)
            {
                MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };

        var win = new Window
        {
            Title = title,
            Width = 470,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        var okBtn = new Button { Content = "确定", IsDefault = true, Padding = new Thickness(22, 4, 22, 4), Margin = new Thickness(0, 0, 10, 0) };
        var cancelBtn = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(22, 4, 22, 4) };
        if (Application.Current.TryFindResource("PrimaryButtonStyle") is Style primary) okBtn.Style = primary;

        decimal zq = initial.Zq, iprj = initial.Iprj;
        SupplierOption supplier = null!;
        okBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nnoBox.Text)) { Warn(title, "配件编号不能为空。"); return; }
            if (!decimal.TryParse(zqBox.Text.Trim(), out zq)) { Warn(title, "币别（Zq）必须为数字。"); return; }
            if (!decimal.TryParse(iprjBox.Text.Trim(), out iprj)) { Warn(title, "外币价（Iprj）必须为数字。"); return; }
            if (supplierBox.SelectedItem is not SupplierOption s) { Warn(title, "请选择供应商。"); return; }
            supplier = s;
            win.DialogResult = true;
        };

        var body = new StackPanel { Margin = new Thickness(16) };
        body.Children.Add(DialogRow("厂牌 Cp", cpBox));
        body.Children.Add(DialogRow("配件编号", nnoBox));
        body.Children.Add(DialogRow("名称", na1Text));
        body.Children.Add(DialogRow("原厂编号", oennoBox));
        body.Children.Add(DialogRow("币别 Zq", zqBox));
        body.Children.Add(DialogRow("外币价 Iprj", iprjBox));
        body.Children.Add(DialogRow("供应商", supplierBox));
        body.Children.Add(DialogRow("备注类型 Bzlx", bzBox));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(okBtn);
        buttons.Children.Add(cancelBtn);
        body.Children.Add(buttons);
        win.Content = body;

        win.Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (win.ShowDialog() != true) return (false, null);
        return (true, new SupplierPriceInput
        {
            Cp = cpBox.Text.Trim(),
            Nno = nnoBox.Text.Trim(),
            Oenno = oennoBox.Text.Trim(),
            Zq = zq,
            Iprj = iprj,
            Gyno = supplier.Gyno,
            Gyname = supplier.Gyname,
            Bzlx = bzBox.SelectedItem as string ?? "",
        });
    }

    /// <summary>单行文本输入小窗；取消/关闭返回 null。</summary>
    private static string? ShowInputBox(string title, string label, string value)
    {
        var box = new TextBox { Text = value, Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        var win = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        var okBtn = new Button { Content = "确定", IsDefault = true, Padding = new Thickness(22, 4, 22, 4), Margin = new Thickness(0, 0, 10, 0) };
        var cancelBtn = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(22, 4, 22, 4) };
        if (Application.Current.TryFindResource("PrimaryButtonStyle") is Style primary) okBtn.Style = primary;

        string? result = null;
        okBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text)) { Warn(title, $"{label}不能为空。"); return; }
            result = box.Text.Trim();
            win.DialogResult = true;
        };

        var body = new StackPanel { Margin = new Thickness(16) };
        body.Children.Add(new TextBlock { Text = label });
        body.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(okBtn);
        buttons.Children.Add(cancelBtn);
        body.Children.Add(buttons);
        win.Content = body;

        win.Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        return win.ShowDialog() == true ? result : null;
    }

    private static StackPanel DialogRow(string label, FrameworkElement editor)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(new TextBlock { Text = label, Width = 95, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(editor);
        return panel;
    }

    private static void Warn(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
}
