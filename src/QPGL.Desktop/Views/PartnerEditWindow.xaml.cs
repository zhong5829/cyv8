using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Partners;

namespace QPGL.Desktop.Views;

/// <summary>客户资料编辑窗（fr_gu 复刻：新增/修改，校验走 IPartnerManageRepository）。</summary>
public partial class PartnerEditWindow : Window
{
    public PartnerEditViewModel ViewModel { get; }

    /// <param name="existing">null=新增；否则修改（PartnerListViewModel 的 Partner 行需含 id）。</param>
    public PartnerEditWindow(int? id, string gno, string gname, string name1, string linkman, string tel, string mobile, string adr, string note, decimal yhl, string ywy)
    {
        ViewModel = new PartnerEditViewModel(id, gno, gname, name1, linkman, tel, mobile, adr, note, yhl, ywy);
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.OnSaved += () =>
        {
            Saved = true;
            Close();
        };
    }

    public bool Saved { get; private set; }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}

/// <summary>客户编辑窗 VM。</summary>
public sealed class PartnerEditViewModel : INotifyPropertyChanged
{
    private readonly int? _id;

    public PartnerEditViewModel(int? id, string gno, string gname, string name1, string linkman, string tel, string mobile, string adr, string note, decimal yhl, string ywy)
    {
        _id = id;
        Gno = gno;
        Gname = gname;
        Name1 = name1;
        Linkman = linkman;
        Tel = tel;
        Mobile = mobile;
        Adr = adr;
        Note = note;
        Yhl = yhl;
        Ywy = ywy;
        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    public string Title => _id is null ? "客户资料 - 新增" : "客户资料 - 修改";

    private string _gno, _gname, _name1, _linkman, _tel, _mobile, _adr, _note, _ywy, _message = "";
    private decimal _yhl;

    public string Gno { get => _gno; set { _gno = value; On(); } }
    public string Gname { get => _gname; set { _gname = value; On(); } }
    public string Name1 { get => _name1; set { _name1 = value; On(); } }
    public string Linkman { get => _linkman; set { _linkman = value; On(); } }
    public string Tel { get => _tel; set { _tel = value; On(); } }
    public string Mobile { get => _mobile; set { _mobile = value; On(); } }
    public string Adr { get => _adr; set { _adr = value; On(); } }
    public string Note { get => _note; set { _note = value; On(); } }
    public decimal Yhl { get => _yhl; set { _yhl = value; On(); } }
    public string Ywy { get => _ywy; set { _ywy = value; On(); } }
    public string Message { get => _message; private set { _message = value; On(); } }

    public IAsyncRelayCommand SaveCommand { get; }

    public event Action? OnSaved;
    public event PropertyChangedEventHandler? PropertyChanged;

    private async System.Threading.Tasks.Task SaveAsync()
    {
        Message = "";
        try
        {
            var result = await Db.PartnerManage.SaveAsync(new SavePartnerCommand
            {
                Id = _id,
                Gno = Gno,
                Gname = Gname,
                Name1 = Name1,
                Linkman = Linkman,
                Tel = Tel,
                Mobile = Mobile,
                Adr = Adr,
                Note = Note,
                Yhl = Yhl,
                Ywy = Ywy,
            }, AppSession.DeptNo);
            MessageBox.Show(result.IsCreate ? "客户资料已添加" : "客户资料已修改", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
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
