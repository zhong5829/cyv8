using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;
using QPGL.Infrastructure.Finance;

namespace QPGL.Desktop.Views;

/// <summary>
/// 业务凭证页（I.2，fr_finopvouch 契约）：生成条件窗（fr_finoperation）+ 凭证预览/审核/反审核网格。
/// 生成=游标预览（ADDSUB 建科目除外）；审核 INSERT finvouch(audit=1) 并回写 tbistoed/tbsada.audit。
/// </summary>
public partial class FinOpVouchControl : UserControl
{
    private readonly FinOpVouchViewModel _vm = new();

    public FinOpVouchControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e) => await _vm.InitializeAsync();

    private void Opty_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag }) _vm.Opty = tag;
    }

    /// <summary>fcolor 隔行底色（旧 FINOPVOUCH.FCOLOR 契约）。</summary>
    private void Grid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is FinOpRow { Fcolor: true })
            e.Row.Background = System.Windows.Media.Brushes.AliceBlue;
    }
}
