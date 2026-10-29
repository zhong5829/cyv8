using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace QPGL.Infrastructure.Vin;

/// <summary>组合VIN查询服务 — 并行查询多个数据源，合并结果</summary>
public class CompositeVinQueryService : IVinQueryService
{
    private readonly List<IVinDataSource> _sources;

    /// <summary>数据源状态变更事件（转发子数据源的LoginStatusChanged）</summary>
    public event EventHandler? SourceStatusChanged;

    public CompositeVinQueryService(IEnumerable<IVinDataSource> sources)
    {
        _sources = sources?.ToList() ?? [];

        // 订阅每个子数据源的登录状态变更事件，转发为统一的SourceStatusChanged
        foreach (var source in _sources)
        {
            source.LoginStatusChanged += (s, e) => SourceStatusChanged?.Invoke(s, e);
        }
    }

    /// <summary>任一数据源已登录即视为已登录</summary>
    public bool IsLoggedIn => _sources.Any(s => s.IsLoggedIn);

    /// <summary>获取已登录的数据源列表</summary>
    public List<IVinDataSource> GetLoggedInSources()
    {
        return _sources.Where(s => s.IsLoggedIn).ToList();
    }

    /// <summary>获取所有数据源（含未登录）</summary>
    public List<IVinDataSource> GetAllSources()
    {
        return _sources.ToList();
    }

    /// <summary>启动时主动续期所有已登录数据源</summary>
    public async Task StartupRefreshAsync()
    {
        var loggedIn = _sources.Where(s => s.IsLoggedIn).ToList();
        if (loggedIn.Count == 0) return;

        await Task.WhenAll(loggedIn.Select(s => s.StartupRefreshAsync()));
    }

    /// <summary>向所有未登录数据源发送短信验证码，任一成功即返回true</summary>
    public async Task<bool> SendSmsAsync(string phone, CancellationToken ct = default)
    {
        var targets = _sources.Where(s => !s.IsLoggedIn).ToList();
        if (targets.Count == 0) return true;

        var tasks = targets.Select(s => SafeExecuteAsync(
            () => s.SendSmsAsync(phone, ct), s.SourceName, "SendSms"));

        var results = await Task.WhenAll(tasks);
        return results.Any(r => r);
    }

    /// <summary>指定数据源发送短信验证码</summary>
    public async Task<bool> SendSourceSmsAsync(string sourceName, string phone, CancellationToken ct = default)
    {
        var source = _sources.FirstOrDefault(s =>
            string.Equals(s.SourceName, sourceName, StringComparison.OrdinalIgnoreCase));

        if (source == null)
            throw new ArgumentException($"未找到数据源: {sourceName}，可用数据源: {string.Join(", ", _sources.Select(s => s.SourceName))}", nameof(sourceName));

        return await source.SendSmsAsync(phone, ct);
    }

    /// <summary>使用相同手机号/验证码登录所有未登录数据源，任一成功即返回true</summary>
    public async Task<bool> LoginAsync(string phone, string smsCode, CancellationToken ct = default)
    {
        var targets = _sources.Where(s => !s.IsLoggedIn).ToList();
        if (targets.Count == 0) return true;

        var tasks = targets.Select(s => SafeExecuteAsync(
            () => s.LoginAsync(phone, smsCode, ct), s.SourceName, "Login"));

        var results = await Task.WhenAll(tasks);
        return results.Any(r => r);
    }

    /// <summary>指定数据源登录</summary>
    public async Task<bool> LoginSourceAsync(string sourceName, string phone, string smsCode, CancellationToken ct = default)
    {
        var source = _sources.FirstOrDefault(s =>
            string.Equals(s.SourceName, sourceName, StringComparison.OrdinalIgnoreCase));

        if (source == null)
            throw new ArgumentException($"未找到数据源: {sourceName}，可用数据源: {string.Join(", ", _sources.Select(s => s.SourceName))}", nameof(sourceName));

        return await source.LoginAsync(phone, smsCode, ct);
    }

    /// <summary>并行VIN解码 — 取第一个成功结果，合并VehicleIds</summary>
    public async Task<VinDecodeResult?> DecodeVinAsync(string vin, CancellationToken ct = default)
    {
        LastQueryErrors.Clear();
        var loggedInSources = GetLoggedInSources();
        if (loggedInSources.Count == 0) return null;

        var tasks = loggedInSources.Select(s => SafeExecuteAsync(
            () => s.DecodeVinAsync(vin, ct), s.SourceName, "DecodeVin"));

        var results = await Task.WhenAll(tasks);

        // 取第一个非null结果作为基础
        var baseResult = results.FirstOrDefault(r => r != null);
        if (baseResult == null) return null;

        // 合并VehicleIds：收集其他数据源返回的额外VehicleIds
        var allVehicleIds = new HashSet<string>(baseResult.VehicleIds);
        foreach (var result in results.Where(r => r != null && r != baseResult))
        {
            foreach (var vid in result!.VehicleIds)
            {
                allVehicleIds.Add(vid);
            }
        }
        baseResult.VehicleIds = allVehicleIds.ToList();

        return baseResult;
    }

    /// <summary>并行获取配件列表 — 按分类合并，同编码配件去重并记录多来源</summary>
    public async Task<VinPartPageResult?> GetPartCardsAsync(string vin, VinDecodeResult vehicleInfo, int page = 1, CancellationToken ct = default)
    {
        var loggedInSources = GetLoggedInSources();
        if (loggedInSources.Count == 0) return null;

        var tasks = loggedInSources.Select(s => SafeExecuteAsync(
            () => s.GetPartCardsAsync(vin, vehicleInfo, page, ct), s.SourceName, "GetPartCards"));

        var results = await Task.WhenAll(tasks);

        // 过滤掉null结果
        var validResults = results.Where(r => r != null).ToList();
        if (validResults.Count == 0) return null;

        // 单数据源直接返回
        if (validResults.Count == 1)
            return validResults[0];

        // 多数据源合并
        return MergePartResults(validResults!);
    }

    /// <summary>并行按关键词搜索配件（不依赖VIN）— 多数据源合并</summary>
    public async Task<VinPartPageResult?> SearchByKeywordAsync(string keyword, int page = 1, CancellationToken ct = default)
    {
        var loggedInSources = GetLoggedInSources();
        if (loggedInSources.Count == 0) return null;

        var tasks = loggedInSources.Select(s => SafeExecuteAsync(
            () => s.SearchByKeywordAsync(keyword, page, ct), s.SourceName, "SearchByKeyword"));

        var results = await Task.WhenAll(tasks);
        var validResults = results.Where(r => r != null).ToList();
        if (validResults.Count == 0) return null;

        if (validResults.Count == 1)
            return validResults[0];

        return MergePartResults(validResults!);
    }

    /// <summary>查询配件适配车型 — 取首个返回非空结果的数据源（仅318car支持）</summary>
    public async Task<List<VinAdaptVehicleBrand>> GetAdaptVehiclesAsync(long productId, CancellationToken ct = default)
    {
        foreach (var source in GetLoggedInSources())
        {
            var result = await SafeExecuteAsync(
                () => source.GetAdaptVehiclesAsync(productId, ct), source.SourceName, "GetAdaptVehicles");
            if (result is { Count: > 0 }) return result;
        }
        return [];
    }

    /// <summary>刷新所有已登录数据源的Token，任一成功即返回true</summary>
    public async Task<bool> RefreshTokenAsync(CancellationToken ct = default)
    {
        var loggedInSources = GetLoggedInSources();
        if (loggedInSources.Count == 0) return false;

        var tasks = loggedInSources.Select(s => SafeExecuteAsync(
            () => s.RefreshTokenAsync(ct), s.SourceName, "RefreshToken"));

        var results = await Task.WhenAll(tasks);
        return results.Any(r => r);
    }

    #region 合并逻辑

    /// <summary>合并多个数据源的配件查询结果</summary>
    private static VinPartPageResult MergePartResults(List<VinPartPageResult> results)
    {
        var merged = new VinPartPageResult
        {
            Current = results[0].Current,
            AdaptQueryRecordId = results[0].AdaptQueryRecordId
        };

        // 全局按编码去重（跨分类、跨数据源）
        var globalSeenModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 按CategoryName分组，合并同名分类
        var categoryDict = new Dictionary<string, VinPartCategoryGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in results)
        {
            // 累加分页信息（取最大值）
            merged.Total = Math.Max(merged.Total, result.Total);
            merged.Pages = Math.Max(merged.Pages, result.Pages);

            foreach (var category in result.Categories)
            {
                var key = category.CategoryName ?? $"_unnamed_{category.TenantCategoryId}";
                if (!categoryDict.TryGetValue(key, out var existingCategory))
                {
                    existingCategory = new VinPartCategoryGroup
                    {
                        TenantCategoryId = category.TenantCategoryId,
                        CategoryName = category.CategoryName
                    };
                    categoryDict[key] = existingCategory;
                }

                MergeProductsIntoCategory(existingCategory, category.Products, globalSeenModels);
            }
        }

        merged.Categories = categoryDict.Values.ToList();
        return merged;
    }

    /// <summary>将一组配件合并到目标分类中，同编码去重并记录多来源</summary>
    private static void MergeProductsIntoCategory(VinPartCategoryGroup target, List<VinPartCard> newProducts, HashSet<string> globalSeenModels)
    {
        // 以Model（编码）为键建立已有配件索引（用于同分类内合并来源信息）
        var existingByModel = new Dictionary<string, VinPartCard>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in target.Products)
        {
            if (!string.IsNullOrWhiteSpace(p.Model))
                existingByModel[p.Model] = p;
        }

        foreach (var newCard in newProducts)
        {
            // 全局按编码去重：如果该编码已在任何分类中出现过
            if (!string.IsNullOrWhiteSpace(newCard.Model) && !globalSeenModels.Add(newCard.Model))
            {
                // 编码已存在，尝试在同分类内合并来源信息
                if (existingByModel.TryGetValue(newCard.Model, out var primary))
                {
                    if (!primary.SourceName?.Contains(newCard.SourceName ?? "") == true)
                    {
                        primary.SourceName = string.IsNullOrEmpty(primary.SourceName)
                            ? newCard.SourceName
                            : $"{primary.SourceName},{newCard.SourceName}";
                    }
                    if (primary != newCard)
                    {
                        primary.AlternateSources.Add(newCard);
                    }
                }
                // 否则该编码已在其他分类中，直接跳过不再添加
                continue;
            }

            // 新编码，添加到分类
            target.Products.Add(newCard);
            if (!string.IsNullOrWhiteSpace(newCard.Model))
                existingByModel[newCard.Model] = newCard;
        }
    }

    #endregion

    #region 安全执行辅助

    /// <summary>最近一次查询中各数据源的错误信息（sourceName → errorMessage）</summary>
    public Dictionary<string, string> LastQueryErrors { get; } = new();

    /// <summary>包装异步操作，异常时记录错误并返回默认值，不阻塞其他并行任务</summary>
    private async Task<T?> SafeExecuteAsync<T>(Func<Task<T>> action, string sourceName, string operation) where T : class?
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            LastQueryErrors[sourceName] = $"{operation}失败: {ex.Message}";
            return null;
        }
    }

    /// <summary>包装异步操作（bool返回值），异常时记录错误并返回false</summary>
    private async Task<bool> SafeExecuteAsync(Func<Task<bool>> action, string sourceName, string operation)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            LastQueryErrors[sourceName] = $"{operation}失败: {ex.Message}";
            return false;
        }
    }

    #endregion
}
