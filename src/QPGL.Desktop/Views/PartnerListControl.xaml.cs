using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class PartnerListControl : UserControl
{
    private readonly PartnerListViewModel _vm = new();

    public PartnerListControl()
    {
        InitializeComponent();
        DataContext = _vm;

        KeywordBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) _ = _vm.SearchAsync(System.Threading.CancellationToken.None);
        };
    }

    private async void UserControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
        => await _vm.LoadAsync();

    private async void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        await _vm.SortByAsync(e.Column.SortMemberPath);
    }

    private async void PageSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not PartnerListViewModel vm) return;
        if (e.AddedItems.Count == 0) return;
        var size = ((ComboBoxItem)e.AddedItems[0]!).Content as string;
        if (size is null) return;
        await vm.ChangePageSizeAsync(size, System.Threading.CancellationToken.None);
    }

    // ---- M5 客户资料增删改（fr_gu 契约） ----

    private async void OnAddPartner(object sender, System.Windows.RoutedEventArgs e)
    {
        var w = new PartnerEditWindow(null, "", "", "", "", "", "", "", "", 0, "");
        w.ShowDialog();
        if (w.Saved) await _vm.LoadAsync();
    }

    private async void OnEditPartner(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not Domain.Partners.Partner p)
        { System.Windows.MessageBox.Show("请先选择客户行"); return; }
        var w = new PartnerEditWindow(p.Id, p.PartNo, p.PartName, p.PartName, p.LinkMan, p.Tel, p.Mobile, p.Address, p.Note, p.DiscountRate, p.SalesMan);
        w.ShowDialog();
        if (w.Saved) await _vm.LoadAsync();
    }

    private async void OnDeletePartner(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not Domain.Partners.Partner p)
        { System.Windows.MessageBox.Show("请先选择客户行"); return; }
        if (System.Windows.MessageBox.Show($"确认删除客户 {p.PartNo} {p.PartName}？", "删除确认",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;
        try
        {
            await QPGL.Desktop.Services.Db.PartnerManage.DeleteAsync(p.Id);
            System.Windows.MessageBox.Show("已删除");
            await _vm.LoadAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "删除失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }
}
