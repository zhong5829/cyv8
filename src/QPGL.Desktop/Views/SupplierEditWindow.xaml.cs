using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Partners;

namespace QPGL.Desktop.Views;

/// <summary>
/// 供应商资料编辑窗（2026-11-07 需求）：新增/修改共用一窗，按传入行是否为 null 切换标题与保存模式。
/// 取代供应商管理页原先「编号/名称/账期」逐字段弹框；落库复用 Db.Suppliers.SaveAsync（tbgugys），不新写 SQL。
/// 入库页新增供应商也走本窗口；保存成功后通过 Saved/SavedId/SavedGyno/SavedGyname 回传，供页面回填与定位。
/// </summary>
public partial class SupplierEditWindow : Window
{
    public SupplierEditViewModel ViewModel { get; }

    /// <param name="existing">null=新增；否则按该行预填并修改（须含 Id）。</param>
    /// <param name="prefillGyname">新增时预填的供应商名称（入库页"否/是"流程带入）。</param>
    public SupplierEditWindow(SupplierRow? existing = null, string? prefillGyname = null)
    {
        ViewModel = new SupplierEditViewModel(existing, prefillGyname);
        InitializeComponent();
        DataContext = ViewModel;
        Owner = Application.Current.MainWindow;
        ViewModel.OnSaved += () =>
        {
            Saved = true;
            SavedId = ViewModel.SavedId;
            SavedGyno = ViewModel.SavedGyno;
            SavedGyname = ViewModel.SavedGyname;
            Close();
        };
    }

    /// <summary>是否已保存成功（用于调用方刷新/回填）。</summary>
    public bool Saved { get; private set; }
    public int? SavedId { get; private set; }
    public string SavedGyno { get; private set; } = "";
    public string SavedGyname { get; private set; } = "";

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}

/// <summary>供应商编辑窗 VM：字段与供应商管理页展示列一致（编号/名称/联系人/电话/账期/税率/地址/备注）。</summary>
public sealed class SupplierEditViewModel : INotifyPropertyChanged
{
    private readonly int? _id;
    private readonly string _fax;
    private readonly string _email;
    private readonly string _bank;
    private readonly string _lb;

    public SupplierEditViewModel(SupplierRow? existing, string? prefillGyname)
    {
        _id = existing?.Id;
        _fax = existing?.Fax ?? "";
        _email = existing?.Email ?? "";
        _bank = existing?.Bank ?? "";
        _lb = existing?.Lb ?? "";
        Gyno = existing?.Gyno ?? "";
        Gyname = existing?.Gyname ?? prefillGyname?.Trim() ?? "";
        Linkman = existing?.Linkman ?? "";
        Tel = existing?.Tel ?? "";
        Zq = (existing?.Zq ?? 0).ToString("0.##");
        Sl = (existing?.Sl ?? 0).ToString("0.##");
        Adr = existing?.Adr ?? "";
        Note = existing?.Note ?? "";
        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    public string Title => _id is null ? "供应商资料 - 新增" : "供应商资料 - 修改";

    private string _gyno = "", _gyname = "", _linkman = "", _tel = "", _zq = "0", _sl = "0", _adr = "", _note = "", _message = "";

    public string Gyno { get => _gyno; set { _gyno = value; On(); } }
    public string Gyname { get => _gyname; set { _gyname = value; On(); } }
    public string Linkman { get => _linkman; set { _linkman = value; On(); } }
    public string Tel { get => _tel; set { _tel = value; On(); } }
    public string Zq { get => _zq; set { _zq = value; On(); } }
    public string Sl { get => _sl; set { _sl = value; On(); } }
    public string Adr { get => _adr; set { _adr = value; On(); } }
    public string Note { get => _note; set { _note = value; On(); } }
    public string Message { get => _message; private set { _message = value; On(); } }

    public IAsyncRelayCommand SaveCommand { get; }

    public int? SavedId { get; private set; }
    public string SavedGyno { get; private set; } = "";
    public string SavedGyname { get; private set; } = "";

    public event Action? OnSaved;
    public event PropertyChangedEventHandler? PropertyChanged;

    private async System.Threading.Tasks.Task SaveAsync()
    {
        Message = "";
        if (!decimal.TryParse(Zq.Trim(), out var zq) || zq < 0)
        {
            Message = "账期必须是不小于 0 的数字。";
            return;
        }
        if (!decimal.TryParse(Sl.Trim(), out var sl) || sl < 0)
        {
            Message = "税率必须是不小于 0 的数字。";
            return;
        }
        try
        {
            // 复用供应商管理现有保存逻辑（gyno 唯一 + gyname 必填校验在仓储内）
            var result = await Db.Suppliers.SaveAsync(new SaveSupplierCommand
            {
                Id = _id,
                Gyno = Gyno,
                Gyname = Gyname,
                Linkman = Linkman,
                Tel = Tel,
                Fax = _fax,
                Adr = Adr,
                Zq = zq,
                Email = _email,
                Note = Note,
                Bank = _bank,
                Sl = sl,
                Lb = _lb,
            });
            SavedId = result.Id;
            SavedGyno = Gyno.Trim();
            SavedGyname = Gyname.Trim();
            MessageBox.Show(result.IsCreate ? "供应商已添加" : "供应商已修改", "供应商资料",
                MessageBoxButton.OK, MessageBoxImage.Information);
            OnSaved?.Invoke();
        }
        catch (Exception ex)
        {
            Message = ex is AppException ae ? ae.Message : "保存失败：" + ex.Message;
        }
    }

    private void On([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
