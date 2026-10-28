using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dapper;
using Microsoft.Extensions.Configuration;
using QPGL.Desktop.Services;
using QPGL.Domain.Common;
using QPGL.Domain.Stock;
using QPGL.Infrastructure.Db;

namespace QPGL.Desktop.ViewModels;

/// <summary>调拨入库页行（待确认调拨单）。</summary>
public partial class DbrkPaperRow : ObservableObject
{
    public string Paper { get; init; } = "";

    [ObservableProperty]
    private bool _isSelected = true;
}

/// <summary>
/// 调拨入库（fr_dbrk，菜单 C9）：列出 mark='Y' 待入库调拨单（tbsada type='调拨' AND gname=本部门名），
/// 勾选后确认——负库存预演 / exec dbrk / 挂帐写 tbysyf 均在 DbrkRepository 内（契约备案见该类头）。
/// 客户端标识 Client（旧 P_CLIENT，进 dbrk SP @client）取 appsettings ClientName，缺省空串（标准分支）。
/// </summary>
public partial class DbrkViewModel : ObservableObject
{
    public ObservableCollection<DbrkPaperRow> Papers { get; } = [];

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>旧 P_CLIENT（dbrk SP @client，非「长沙贻惠」走含售价比率分支）；部署变体备案。</summary>
    private static string Client => new ConfigurationBuilder()
        .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .Build()["ClientName"] ?? "";

    [RelayCommand]
    public async System.Threading.Tasks.Task SearchAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        Message = "";
        try
        {
            var dpmname = await GetDeptNameAsync(ct);
            var list = await Db.Dbrk.GetOpenPapersAsync(dpmname, ct);
            Papers.Clear();
            foreach (var p in list) Papers.Add(new DbrkPaperRow { Paper = p });
            Message = list.Count == 0 ? "没有待入库的调拨单" : $"共 {list.Count} 张待入库调拨单";
        }
        catch (Exception ex) { Message = "查询失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>单条确认（fr_dbrk row7）：确认选中列表中的单张调拨单。</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task ConfirmOneAsync(DbrkPaperRow? row, CancellationToken ct)
    {
        if (row is null)
        {
            Message = "没有数据，不能调拨确认！";
            return;
        }
        if (MessageBox.Show($"将 {row.Paper} 号调拨单确认入库吗？（生成新进库单号）",
                "调拨确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        IsBusy = true;
        Message = "";
        try
        {
            var (dpm, dpmname) = await GetDeptAsync(ct);
            var result = await Db.Dbrk.ConfirmAsync(
                new DbrkConfirmCommand { Papers = [row.Paper], Client = Client },
                dpm, dpmname, AppSession.Operator, ct);
            Message = $"确认入库：[{result.Paper}] 生成进库单号为：[{result.NewPaper}]"
                      + (result.CreditWritten ? "（挂帐已写应收）" : "");
            await SearchAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
        catch (Exception ex) { Message = "确认失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>批量确认（__.row68）：逐单独立事务，单笔失败记录后继续。</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task ConfirmBatchAsync(CancellationToken ct)
    {
        var selected = Papers.Where(p => p.IsSelected).Select(p => p.Paper).ToList();
        if (selected.Count == 0)
        {
            Message = "没有选择调拨单，不能批量确认！";
            return;
        }
        if (MessageBox.Show($"将选中的 {selected.Count} 张调拨单批量确认入库吗？",
                "批量调拨确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        IsBusy = true;
        Message = "";
        try
        {
            var (dpm, dpmname) = await GetDeptAsync(ct);
            var result = await Db.Dbrk.ConfirmBatchAsync(
                new DbrkConfirmCommand { Papers = selected, Client = Client },
                dpm, dpmname, AppSession.Operator, ct);
            var okText = string.Join("、", result.Confirmed.Select(c => $"{c.Paper}→{c.NewPaper}"));
            var failText = string.Join("；", result.Failed.Select(kv => $"{kv.Key}：{kv.Value}"));
            Message = $"成功 {result.Confirmed.Count} 张：{okText}"
                      + (result.Failed.Count > 0 ? $"　失败 {result.Failed.Count} 张：{failText}" : "");
            await SearchAsync(ct);
        }
        catch (AppException ex) { Message = ex.Message; }
        catch (Exception ex) { Message = "批量确认失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    private static async Task<(string Dpm, string Dpmname)> GetDeptAsync(CancellationToken ct)
    {
        var dpm = AppSession.DeptNo;
        return (dpm, await GetDeptNameAsync(ct));
    }

    /// <summary>部门名（旧 P_DPMNAME）：tbdpm dpmbh→dpm。</summary>
    private static async Task<string> GetDeptNameAsync(CancellationToken ct)
    {
        await using var conn = await Db.Factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT RTRIM(dpm) FROM tbdpm WITH (NOLOCK) WHERE dpmbh = @dpm",
            new { dpm = new DbString { Value = AppSession.DeptNo, IsAnsi = true, Length = 10 } },
            cancellationToken: ct)) ?? "";
    }
}
