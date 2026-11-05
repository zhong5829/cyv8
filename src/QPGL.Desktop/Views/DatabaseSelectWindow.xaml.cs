using System.Data;
using System.Windows;
using Microsoft.Data.SqlClient;

namespace QPGL.Desktop.Views;

/// <summary>
/// 数据库选择窗：appsettings.json 配置了多条连接串（ConnectionStrings 下多个键）时，
/// 启动先弹本窗选择本次使用的数据库，确定后才进入登录流程；取消/退出系统 = 关闭软件。
/// </summary>
public partial class DatabaseSelectWindow : Window
{
    /// <summary>用户确认后选中的连接（名称 + 连接串）。</summary>
    public (string Name, string ConnectionString) Selected { get; private set; }

    public DatabaseSelectWindow(IReadOnlyList<(string Name, string ConnectionString)> connections)
    {
        InitializeComponent();
        var items = connections.Select(c =>
            new DbItem(c.Name, c.ConnectionString, BuildDescription(c.ConnectionString))).ToList();
        DbList.ItemsSource = items;
        if (items.Count > 0) DbList.SelectedIndex = 0;
    }

    /// <summary>从连接串解析"服务器 / 数据库"辅助说明（解析失败留空，不影响选择）。</summary>
    private static string BuildDescription(string connectionString)
    {
        try
        {
            var b = new SqlConnectionStringBuilder(connectionString);
            return $"服务器：{b.DataSource}    数据库：{b.InitialCatalog}";
        }
        catch
        {
            return string.Empty;
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (DbList.SelectedItem is not DbItem item)
        {
            MessageBox.Show("请选择数据库。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Selected = (item.Name, item.ConnectionString);
        DialogResult = true;
    }

    private void OnQuit(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private sealed record DbItem(string Name, string ConnectionString, string Description)
    {
        public string DisplayName => string.IsNullOrEmpty(Description) ? Name : $"{Name}（{Description}）";
    }
}
