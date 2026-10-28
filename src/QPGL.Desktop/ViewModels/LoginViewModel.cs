using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QPGL.Desktop.Services;
using QPGL.Domain.Auth;

namespace QPGL.Desktop.ViewModels;

public partial class LoginViewModel : ObservableObject, IDisposable
{
    public System.Collections.Generic.IReadOnlyList<string> Operators { get; private set; } = [];

    [ObservableProperty]
    private string _selectedOperator = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _isBusy;

    public event Action? LoginSucceeded;
    public event Action? CancelRequested;

    public LoginViewModel()
    {
        _ = LoadOperatorsAsync();
    }

    /// <summary>操作员名单（直连 tbkey 实时查询，§6.7 禁一次性 TOP N——tbkey 行数极少可直接返回）。</summary>
    private async System.Threading.Tasks.Task LoadOperatorsAsync()
    {
        try
        {
            Operators = await Db.Users.GetOperatorsAsync();
            OnPropertyChanged(nameof(Operators));
            if (Operators.Count > 0 && string.IsNullOrEmpty(SelectedOperator))
                SelectedOperator = Operators[0];
        }
        catch
        {
            // 名单获取失败不阻断登录（仍可手输），登录时再报具体错误
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task LoginAsync(CancellationToken ct)
    {
        Message = "";
        if (string.IsNullOrWhiteSpace(SelectedOperator))
        {
            Message = "请选择操作员";
            return;
        }

        IsBusy = true;
        try
        {
            var per = SelectedOperator.Trim();
            var row = await Db.Users.GetByOperatorAsync(per, ct);
            if (row is null ||
                !string.Equals(row.password.Trim(), Password.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                // 旧系统 fr_pwd 语义：LOWER(ALLTRIM(col)) == LOWER(ALLTRIM(input))
                Message = "操作员或密码错误";
                return;
            }

            // M3 权限完整：登录后按 dpmbh+role 加载角色权限（旧 fr_login row12 语义，权限一律取 tbrole）
            var role = row.role.Trim();
            var deptNo = row.dpmbh.Trim();
            PermissionSet permission = PermissionSet.None;
            string dpmusem = row.dpmusem.Trim();
            string[]? brandWhitelist = null;
            if (role.Length > 0)
            {
                var roleRow = await Db.Permissions.GetRoleAsync(deptNo, role, ct);
                if (roleRow is not null)
                {
                    permission = PermissionSet.Parse(roleRow.usem, roleRow.usem1);
                    dpmusem = roleRow.dpmusem.Trim();
                    // 旧 fr_login row12 L99-113：角色 usemcp（厂牌白名单），空=不限制
                    var usemcp = roleRow.usemcp.Trim();
                    if (usemcp.Length > 0)
                        brandWhitelist = usemcp.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Append("急件").ToArray();
                }
            }

            AppSession.SignIn(
                row.per.Trim(),
                role,
                deptNo,
                permission.Raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                permission,
                dpmusem,
                brandWhitelist);

            Message = "";
            LoginSucceeded?.Invoke();
        }
        catch (Exception ex)
        {
            Message = "登录失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke();

    public void Dispose() { }
}
