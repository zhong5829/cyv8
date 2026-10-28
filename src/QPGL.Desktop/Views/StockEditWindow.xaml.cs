using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;

namespace QPGL.Desktop.Views;

/// <summary>库存修改窗（fr_istoch FRM2 复刻：字段启用按权限点，保存走 IStockEditRepository）。</summary>
public partial class StockEditWindow : Window
{
    public StockEditViewModel ViewModel { get; }

    public StockEditWindow(StockRow row)
    {
        ViewModel = new StockEditViewModel(row);
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.OnSaved += () =>
        {
            Changed = true;
            Close();
        };
    }

    public bool Changed { get; private set; }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}

/// <summary>库存修改窗 VM（INPC，窗内封闭）。</summary>
public sealed class StockEditViewModel : INotifyPropertyChanged
{
    private readonly StockRow _orig;

    public StockEditViewModel(StockRow row)
    {
        _orig = row;
        Id = row.Id;
        Cp = row.Cp.Trim();
        Nno = row.Nno.Trim();
        Na1 = row.Na1.Trim();
        Ty = row.Ty.Trim();
        Unit = row.Unit.Trim();
        Kcamount = row.Kcamount;
        Iprc = row.Iprc;
        Iprj = row.Iprj;
        Oprc = row.Oprc;
        Oprb = row.Oprb;
        Posi = row.Posi.Trim();
        Depotno = row.Depotno.Trim();
        Note = row.Note.Trim();

        var perm = AppSession.Permission;
        CanEditQuantity = perm.HasMenu(StockEditPermissions.Quantity);
        CanEditPurchase = perm.HasMenu(StockEditPermissions.PurchasePrice);
        CanEditSale = perm.HasMenu(StockEditPermissions.SalePrice);
        CanEditProperty = perm.HasMenu(StockEditPermissions.Property);

        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    public int Id { get; }
    public string Cp { get; }
    public string Nno { get; }

    private string _na1, _ty, _unit, _posi, _depotno, _note, _message = "";
    private decimal _kcamount, _iprc, _iprj, _oprc, _oprb;

    public string Na1 { get => _na1; set { _na1 = value; On(); } }
    public string Ty { get => _ty; set { _ty = value; On(); } }
    public string Unit { get => _unit; set { _unit = value; On(); } }
    public decimal Kcamount { get => _kcamount; set { _kcamount = value; On(); } }
    public decimal Iprc { get => _iprc; set { _iprc = value; On(); } }
    public decimal Iprj { get => _iprj; set { _iprj = value; On(); } }
    public decimal Oprc { get => _oprc; set { _oprc = value; On(); } }
    public decimal Oprb { get => _oprb; set { _oprb = value; On(); } }
    public string Posi { get => _posi; set { _posi = value; On(); } }
    public string Depotno { get => _depotno; set { _depotno = value; On(); } }
    public string Note { get => _note; set { _note = value; On(); } }
    public string Message { get => _message; private set { _message = value; On(); } }

    public bool CanEditQuantity { get; }
    public bool CanEditPurchase { get; }
    public bool CanEditSale { get; }
    public bool CanEditProperty { get; }

    public IAsyncRelayCommand SaveCommand { get; }

    public event Action? OnSaved;
    public event PropertyChangedEventHandler? PropertyChanged;

    private async System.Threading.Tasks.Task SaveAsync()
    {
        Message = "";
        try
        {
            var command = new UpdateStockCommand
            {
                Id = Id,
                OrigKcamount = _orig.Kcamount,
                Per = AppSession.Operator,
                Na1 = Changed(Na1, _orig.Na1) ? Na1 : null,
                Ty = Changed(Ty, _orig.Ty) ? Ty : null,
                Unit = Changed(Unit, _orig.Unit) ? Unit : null,
                Kcamount = Kcamount != _orig.Kcamount ? Kcamount : null,
                Iprc = Iprc != _orig.Iprc ? Iprc : null,
                Iprj = Iprj != _orig.Iprj ? Iprj : null,
                Oprc = Oprc != _orig.Oprc ? Oprc : null,
                Oprb = Oprb != _orig.Oprb ? Oprb : null,
                Posi = Changed(Posi, _orig.Posi) ? Posi : null,
                Depotno = Changed(Depotno, _orig.Depotno) ? Depotno : null,
                Note = Changed(Note, _orig.Note) ? Note : null,
            };
            StockEditPermissions.Validate(AppSession.Permission, command);
            var result = await Db.StockEdit.UpdateAsync(command, AppSession.DeptNo);
            MessageBox.Show(
                $"修改成功{(result.HasChangelog ? $"（{result.ChangeNote}，已记修改流水）" : "")}",
                "库存修改", MessageBoxButton.OK, MessageBoxImage.Information);
            OnSaved?.Invoke();
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "保存失败：" + ex.Message;
        }
    }

    private static bool Changed(string? now, string orig) =>
        now is not null && !string.Equals(now.Trim(), orig.Trim(), StringComparison.Ordinal);

    private void On([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
