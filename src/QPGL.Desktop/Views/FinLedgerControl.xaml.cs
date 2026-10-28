using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

/// <summary>五账查询（I.4：总账/明细账/数量金额账/核算项目账/多栏账）。</summary>
public partial class FinLedgerControl : UserControl
{
    private readonly FinLedgerViewModel _vm = new();

    public FinLedgerControl()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private void UserControl_Loaded(object sender, RoutedEventArgs e) { }
}
