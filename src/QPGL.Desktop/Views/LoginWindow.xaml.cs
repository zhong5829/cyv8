using System.Windows;
using System.Windows.Input;
using QPGL.Desktop.ViewModels;

namespace QPGL.Desktop.Views;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm = new();

    public LoginWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        OperatorBox.Focus();

        _vm.LoginSucceeded += () =>
        {
            // 旧 fr_login row12 L129/134：登录成功后必选厂牌（fr_cp），不选即退出
            var brand = new BrandWindow(mandatory: true);
            if (brand.ShowDialog() != true)
            {
                Application.Current.Shutdown();
                return;
            }
            var main = new MainWindow();
            Application.Current.MainWindow = main;
            main.Show();
            Close();
        };
        _vm.CancelRequested += Close;

        // 回车从操作员跳到密码框；密码框回车即登录（IsDefault 已生效）
        OperatorBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) PasswordBox.Focus();
        };
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _vm.Password = PasswordBox.Password;
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }
}
