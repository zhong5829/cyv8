using System.Windows;
using System.Windows.Controls;
using QPGL.Domain.Common;
using QPGL.Domain.FinanceConfirm;

namespace QPGL.Desktop.Views;

/// <summary>
/// 财务确认弹窗族（G.1，旧 fr_sk 复刻）：
/// ConfirmInDialog（row410 FRM_PNO：码单/付款方式/供应商）、
/// ConfirmTransferDialog（妴_.row478 FRM调拨确认：付款方式）、
/// ConfirmConsignDialog（___.row454 FRMPJ：付款日期/经手人）、
/// ShowTotal（Z_.row54 → fr_total 合计窗）。
/// 付款方式清单 = tbsysset type='guno'（旧 c_guno / CBOFKFS rowsource）。
/// </summary>
public static class FinanceConfirmDialogs
{
    /// <summary>付款方式清单（旧 Load PASSTH c_guno：select name,valuen from tbsysset where type='guno'）。</summary>
    internal static async Task<IReadOnlyList<string>> GetPayTermsAsync()
    {
        try
        {
            var list = await Services.Db.Receipts.GetPaymentMethodsAsync();
            return list.Select(m => m.Name).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>合计窗（旧 DO FORM fr_total WITH, TOTAL_FIELD）。</summary>
    public static void ShowTotal(IReadOnlyList<FinConfirmTotalRow> totals)
    {
        var grid = new Grid { Margin = new Thickness(14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var row = 0;
        foreach (var t in totals)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(new TextBlock
            {
                Text = t.Name,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetRow(grid.Children[^1], row);
            Grid.SetColumn(grid.Children[^1], 0);
            var value = t.Count > 0 ? t.Count.ToString("N0") : t.Value.ToString("N2");
            grid.Children.Add(new TextBlock
            {
                Text = value,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetRow(grid.Children[^1], row);
            Grid.SetColumn(grid.Children[^1], 1);
            row++;
        }
        var win = new Window
        {
            Title = "分类汇总",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = grid,
        };
        win.ShowDialog();
    }

    private static void BuildForm(Grid grid, (string Label, FrameworkElement Field)[] items)
    {
        for (var i = 0; i < items.Length; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < items.Length; i++)
        {
            grid.Children.Add(new TextBlock
            {
                Text = items[i].Label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 8, 4),
            });
            Grid.SetRow(grid.Children[^1], i);
            Grid.SetColumn(grid.Children[^1], 0);
            items[i].Field.Margin = new Thickness(0, 4, 0, 4);
            Grid.SetRow(items[i].Field, i);
            Grid.SetColumn(items[i].Field, 1);
            grid.Children.Add(items[i].Field);
        }
    }

    private static UIElement Footer(Window win, Action accept)
    {
        var ok = new Button { Content = "确定", Width = 72, IsDefault = true, Padding = new Thickness(0, 3, 0, 3) };
        var cancel = new Button { Content = "取消", Width = 72, IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(0, 3, 0, 3) };
        ok.Click += (_, _) => accept();
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12),
            Children = { ok, cancel },
        };
    }

    /// <summary>入库确认窗（旧 FRM_PNO row410：TXT_PNO 码单 / CBOFKFS 付款方式 / TXTGYNAME 供应商）。</summary>
    public sealed class ConfirmInDialog : Window
    {
        private readonly TextBox _pno = new() { Width = 180 };
        // 旧 CBOFKFS.VALUE 可直接赋值不在下拉清单中的值（VFP 组合框语义）→ WPF IsEditable + Text
        private readonly ComboBox _fkfs = new() { Width = 180, IsEditable = true };
        private readonly TextBox _gyname = new() { Width = 180 };
        private readonly FinConfirmInRow _row;

        public FinConfirmInCommand? Result { get; private set; }

        public ConfirmInDialog(FinConfirmInRow row, bool ysyfLocked)
        {
            _row = row;
            Title = "入库确认 - " + row.Cno.Trim();
            Width = 360;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _pno.Text = row.Pno.Trim();
            _gyname.Text = row.Gyname.Trim();
            // 旧 row33 CASE 2：tbysyf 已有该单记录 → 锁定付款方式/供应商只允许改码单
            _fkfs.IsEnabled = !ysyfLocked;
            _gyname.IsEnabled = !ysyfLocked;
            var grid = new Grid { Margin = new Thickness(12) };
            BuildForm(grid,
            [
                ("进库单号", new TextBlock { Text = row.Cno.Trim(), VerticalAlignment = VerticalAlignment.Center }),
                ("码单", _pno),
                ("付款方式", _fkfs),
                ("供应商", _gyname),
            ]);
            var dock = new DockPanel();
            DockPanel.SetDock(grid, Dock.Top);
            dock.Children.Add(grid);
            dock.Children.Add(Footer(this, Accept));
            Content = dock;
            Loaded += async (_, _) =>
            {
                foreach (var m in await GetPayTermsAsync()) _fkfs.Items.Add(m);
                _fkfs.Text = row.Fkfs.Trim();   // 旧 row33 CASE 2：CBOFKFS.VALUE = C_RK.FKFS
            };
        }

        private void Accept()
        {
            // 旧 row410 L6-9：挂帐时供应商必填
            if (_fkfs.Text.Trim() == "挂帐" && string.IsNullOrWhiteSpace(_gyname.Text))
            {
                MessageBox.Show("挂帐的入库单，供应商不能空!", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Result = new FinConfirmInCommand
            {
                Seano = _row.Cno,
                Isord = _row.Isord,
                Pno = _pno.Text.Trim(),
                Fkfs = _fkfs.Text.Trim(),
                Gyname = _gyname.Text.Trim(),
            };
            DialogResult = true;
        }
    }

    /// <summary>调拨确认窗（旧 FRM调拨确认 妴_.row478：CBOFKFS 付款方式下拉）。</summary>
    public sealed class ConfirmTransferDialog : Window
    {
        private readonly ComboBox _fkfs = new() { Width = 180 };

        public FinConfirmTransferCommand? Result { get; private set; }

        public ConfirmTransferDialog(FinConfirmTransferRow row)
        {
            Title = "调拨确认 - " + row.Paper.Trim();
            Width = 340;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var info = new TextBlock
            {
                Text = $"单号：{row.Paper.Trim()}   日期：{row.Outdate:yyyy-MM-dd}\n客户：{row.Gname.Trim()}   实成本：{row.Oprct:N2}",
                Margin = new Thickness(12, 12, 12, 0),
            };
            var grid = new Grid { Margin = new Thickness(12, 8, 12, 0) };
            BuildForm(grid, [("付款方式", _fkfs)]);
            var dock = new DockPanel();
            DockPanel.SetDock(info, Dock.Top);
            dock.Children.Add(info);
            DockPanel.SetDock(grid, Dock.Top);
            dock.Children.Add(grid);
            dock.Children.Add(Footer(this, Accept));
            Content = dock;
            Loaded += async (_, _) =>
            {
                foreach (var m in await GetPayTermsAsync()) _fkfs.Items.Add(m);
                _fkfs.SelectedItem = row.Guno.Trim() is { Length: > 0 } g ? g : _fkfs.Items.Count > 0 ? _fkfs.Items[0] : null;
            };
        }

        private void Accept()
        {
            if (_fkfs.SelectedItem is not string fkfs || fkfs.Length == 0)
            {
                MessageBox.Show("付款方式不能为空！", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Result = new FinConfirmTransferCommand { Fkfs = fkfs };
            DialogResult = true;
        }
    }

    /// <summary>代销付款确认窗（旧 FRMPJ ___.row454：TXTSKDATE 付款日期 / TXTSKPER 付款经手人）。</summary>
    public sealed class ConfirmConsignDialog : Window
    {
        private readonly IReadOnlyList<FinConfirmConsignRow> _rows;
        private readonly DatePicker _skdate = new() { Width = 140, SelectedDate = DateTime.Today };
        private readonly TextBox _skper = new() { Width = 140, Text = Services.AppSession.Operator };

        /// <summary>确认命令集（勾选行，付款日期/经手人统一填写）。</summary>
        public IReadOnlyList<FinConfirmConsignCommand>? Result { get; private set; }

        public ConfirmConsignDialog(IReadOnlyList<FinConfirmConsignRow> rows)
        {
            _rows = rows;
            Title = "代销付款确认";
            Width = 340;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var info = new TextBlock
            {
                Text = $"已选 {rows.Count} 行（首行：{rows[0].Paper.Trim()} / {rows[0].Nno.Trim()}）",
                Margin = new Thickness(12, 12, 12, 0),
            };
            var grid = new Grid { Margin = new Thickness(12, 8, 12, 0) };
            BuildForm(grid,
            [
                ("付款日期", _skdate),
                ("付款经手人", _skper),
            ]);
            var dock = new DockPanel();
            DockPanel.SetDock(info, Dock.Top);
            dock.Children.Add(info);
            DockPanel.SetDock(grid, Dock.Top);
            dock.Children.Add(grid);
            dock.Children.Add(Footer(this, Accept));
            Content = dock;
        }

        private void Accept()
        {
            // 旧 ___.row454 L23-30：付款日期/经手人不能为空
            if (_skdate.SelectedDate is null)
            {
                MessageBox.Show("付款日期不能为空！", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(_skper.Text))
            {
                MessageBox.Show("付款经手人不能为空!", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Result = _rows.Select(r => new FinConfirmConsignCommand
            {
                Paper = r.Paper,
                Nno = r.Nno,
                Cp = r.Cp,
                Dpm = r.Dpm,
                Skper = _skper.Text.Trim(),
                Skdate = _skdate.SelectedDate.Value,
            }).ToList();
            DialogResult = true;
        }
    }
}
