using System.Collections.Concurrent;
using QPGL.Domain.Common;
using QPGL.Domain.Parts;
using QPGL.Infrastructure.Orders;
using QPGL.Infrastructure.Parts;

namespace QPGL.Desktop.Services;

/// <summary>
/// 配件选择缓存服务（2026-11-04 远程弱网需求，用户确认口径）：
/// 销售开单页搜索区 / 订单生成选料窗 / 散件入库 PartPickWindow 三入口共用。
/// ① 主数据内存快照：tbprnoty（全厂牌）+ tbisto（本部门）+ tbname0/tbty0 拼音映射，
///    属性侧 30 分钟 TTL、库存侧 5 分钟 TTL 后台静默刷新，三条件搜索在快照上内存过滤（零网络）；
/// ② 库存列始终实时：快照路径下对本页行做轻量批量实时刷新（1.5s 超时降级为缓存值并提示，
///    用户 2026-11-04 确认改 1.5 秒）；订单生成页库存/在途/警戒/区间出库量走批量聚合（1 次往返）；
/// ③ 查询结果缓存：快照不可用（首次加载中/条件不可缓存）时直查 DB 的结果缓存 60 秒；
/// ④ 历史区缓存：按（厂牌，编码）会话内缓存，秒显 + 调用方后台重查（多机数据 1~2 秒内跟上）。
/// 纯进程内存，不落盘；属性保存/入库生效等本地动作调 Invalidate() 立即失效。
/// 内存过滤语义与 PartRepository.SearchAsync / Order4Repository.SearchPartsAsync 的 SQL 条件逐条对齐。
/// </summary>
public static class PartCacheService
{
    /// <summary>实时刷新超时（用户 2026-11-04 确认 1.5 秒；超时降级为缓存值并提示）。</summary>
    public static readonly TimeSpan LiveTimeout = TimeSpan.FromSeconds(1.5);

    private static readonly TimeSpan AttrTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StockTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ResultTtl = TimeSpan.FromSeconds(60);

    /// <summary>最近一次缓存路径搜索的降级提示（"库存为缓存值…"），调用方读取后追加到状态文本；直查 DB 路径为 null。</summary>
    public static string? LastHint { get; private set; }

    private sealed class State
    {
        public string Dpm = "";
        public PartCacheSnapshot Snapshot = null!;
        public DateTime AttrLoadedAt;
        public DateTime StockLoadedAt;
        /// <summary>tbisto 增量水位（快照内最大 id）：实时刷新时 id 大于水位的行 = 其他电脑新入库批次行。</summary>
        public int LastMaxStockId;
        /// <summary>tbprnoty 增量水位（快照内最大 id）：id 大于水位 = 其他电脑新增配件属性。</summary>
        public int LastMaxAttrId;
    }

    private static State? _state;
    private static int _loading;
    private static DateTime _lastFailAt;

    private static readonly ConcurrentDictionary<string, (DateTime At, object Result)> _results = new();
    private static readonly ConcurrentDictionary<string, (List<PartInRecordRow> In, List<PartSaleRecordRow> Sale)> _history = new();
    private static readonly ConcurrentDictionary<string, PickAggregate> _aggs = new();

    /// <summary>清空主数据快照与查询结果缓存（属性保存/入库生效/调价等本地动作后调用；历史区缓存保留）。</summary>
    public static void Invalidate()
    {
        _state = null;
        _results.Clear();
    }

    // ---------- 快照装载 ----------

    private static State? GetState(string dpm)
    {
        var d = dpm.Trim();
        var s = _state;
        if (s is null || !string.Equals(s.Dpm, d, StringComparison.OrdinalIgnoreCase))
        {
            _ = EnsureLoadedAsync(d);
            return null;   // 本次走 DB，快照就绪后下次搜索生效
        }
        if (DateTime.Now - s.AttrLoadedAt > AttrTtl) _ = EnsureLoadedAsync(d);       // 全量后台刷新
        if (DateTime.Now - s.StockLoadedAt > StockTtl) _ = RefreshStockAsync(d);     // 仅 tbisto 后台刷新
        return s;
    }

    private static async Task EnsureLoadedAsync(string dpm)
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        try
        {
            if (DateTime.Now - _lastFailAt < TimeSpan.FromSeconds(60)) return;   // 失败退避：1 分钟内不重试
            var snap = await Db.Parts.GetCacheSnapshotAsync(dpm);
            _state = new State
            {
                Dpm = dpm,
                Snapshot = snap,
                AttrLoadedAt = DateTime.Now,
                StockLoadedAt = DateTime.Now,
                LastMaxStockId = snap.StockRows.Count > 0 ? snap.StockRows.Max(r => r.id) : 0,
                LastMaxAttrId = snap.AttrRows.Count > 0 ? snap.AttrRows.Max(r => r.Id) : 0,
            };
        }
        catch (Exception ex)
        {
            _lastFailAt = DateTime.Now;
            QPGL.Infrastructure.DebugLog.Log($"[PartCache] 快照加载失败：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _loading, 0);
        }
    }

    private static async Task RefreshStockAsync(string dpm)
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        try
        {
            var stock = await Db.Parts.GetStockCacheAsync(dpm);
            var s = _state;
            if (s is not null)
            {
                s.Snapshot.StockRows = stock;   // 原子引用替换，读侧继续用旧列表安全
                s.StockLoadedAt = DateTime.Now;
                s.LastMaxStockId = stock.Count > 0 ? stock.Max(r => r.id) : 0;
            }
        }
        catch (Exception ex)
        {
            QPGL.Infrastructure.DebugLog.Log($"[PartCache] 库存快照刷新失败：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _loading, 0);
        }
    }

    // ---------- 销售开单 / 散件入库搜索（PartRepository.SearchAsync 同签名替身） ----------

    public static async Task<PagedResult<PartItem>> SearchAsync(string dpm, string? cp, string? na1, string? ty,
        PagedRequest request, string? note = null, bool fillLastIprj = true, string? brand = null,
        bool includeAttrOnly = false, CancellationToken ct = default)
    {
        LastHint = null;
        var state = GetState(dpm);
        if (state is not null && string.IsNullOrWhiteSpace(request.Sort))
        {
            var (items, total) = FilterSale(state, dpm, cp, na1, ty, note, brand, includeAttrOnly, request.Page, request.PageSize);
            // 库存列始终实时 + 新批次行实时并入（用户 2026-11-04 确认：其他电脑新入库行也要实时可见）：
            // 一次往返带回 本页最新库存 + tbisto 增量水位 + 水位后新批次行，1.5s 超时降级为快照值
            var live = await FetchLiveAsync(state, items, dpm, ct);
            if (live is not null)
            {
                ApplyStock(items, live.StockByIds);
                var stockChanged = live.MaxId > state.LastMaxStockId && live.NewRows.Count > 0;
                var attrChanged = live.AttrMaxId > state.LastMaxAttrId && live.NewAttrRows.Count > 0;
                if (stockChanged || attrChanged)
                {
                    // 新批次行/新增属性并入快照（超 500 行转全量刷新），内存重过滤让新行即时进入本次结果
                    var stockOverflow = stockChanged && live.NewRows.Count >= 500;
                    var attrOverflow = attrChanged && live.NewAttrRows.Count >= 500;
                    if (stockOverflow) _ = RefreshStockAsync(dpm);
                    else if (stockChanged)
                    {
                        state.Snapshot.StockRows.AddRange(live.NewRows);
                        state.LastMaxStockId = live.MaxId;
                    }
                    if (attrOverflow) _ = EnsureLoadedAsync(dpm);
                    else if (attrChanged)
                    {
                        state.Snapshot.AttrRows.AddRange(live.NewAttrRows);
                        state.LastMaxAttrId = live.AttrMaxId;
                    }
                    (items, total) = FilterSale(state, dpm, cp, na1, ty, note, brand, includeAttrOnly, request.Page, request.PageSize);
                    ApplyStock(items, live.StockByIds);
                    if (!stockOverflow)
                        ApplyStock(items, live.NewRows.ToDictionary(r => r.id, r => r.kcamount));   // 新行自带新鲜库存
                }
            }
            if (fillLastIprj && items.Count > 0)
            {
                try { await Db.Parts.FillLastIprjAsync(items, dpm, ct); }
                catch { /* 最近进价列回填失败不影响搜索 */ }
            }
            return new PagedResult<PartItem> { Items = items, Total = total, Page = request.Page, PageSize = request.PageSize };
        }

        // 快照不可用/排序自定义 → 结果缓存 + DB 直查
        var key = ResultKey("sale", dpm, cp, na1, ty, note, request.Page, request.PageSize, brand, includeAttrOnly, fillLastIprj, request.Sort);
        if (TryGetResult<PagedResult<PartItem>>(key, out var cached))
        {
            var items2 = cached.Items.ToList();
            await FillLiveAsync(items2, dpm, fillLastIprj: false, ct);   // 进价回填已含在缓存结果中
            return new PagedResult<PartItem> { Items = items2, Total = cached.Total, Page = cached.Page, PageSize = cached.PageSize };
        }
        var db = await Db.Parts.SearchAsync(dpm, cp, na1, ty, request, note, fillLastIprj, brand, includeAttrOnly, ct);
        _results[key] = (DateTime.Now, db);
        return db;
    }

    /// <summary>跨厂牌命中统计（快照版，语义与 PartRepository.CountOtherBrandsAsync 一致含属性库补集）。</summary>
    public static async Task<IReadOnlyList<(string Cp, int Count)>> CountOtherBrandsAsync(string dpm, string brand,
        string? cp, string? na1, string? ty, CancellationToken ct = default, string? note = null)
    {
        LastHint = null;
        var state = GetState(dpm);
        if (state is null)
            return await Db.Parts.CountOtherBrandsAsync(dpm, brand, cp, na1, ty, ct, note);

        var cpKw = (cp ?? "").Trim();
        var na1Kw = (na1 ?? "").Trim();
        var tyKw = (ty ?? "").Trim();
        var noteKw = (note ?? "").Trim();
        var brandKw = (brand ?? "").Trim();
        if (brandKw.Length == 0) return [];
        var cpContains = cpKw.StartsWith("/");
        var cpWord = cpContains ? cpKw[1..].Trim() : cpKw;
        var nameSet = BuildNameSet(state.Snapshot.Name0, na1Kw);
        var tySet = BuildTySet(state.Snapshot.Ty0, tyKw);
        var stockKeys = BuildStockKeys(state.Snapshot.StockRows);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Add(string cpValue)
        {
            var c = cpValue.Trim();
            if (c.Length == 0) return;
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }
        foreach (var r in state.Snapshot.StockRows)
        {
            if (r.cp.Trim().Equals(brandKw, StringComparison.OrdinalIgnoreCase)) continue;
            if (!MatchStock(r, cpContains, cpWord, na1Kw, tyKw, noteKw, nameSet, tySet)) continue;
            Add(r.cp);
        }
        foreach (var p in state.Snapshot.AttrRows)
        {
            if (p.Cp.Equals(brandKw, StringComparison.OrdinalIgnoreCase)) continue;
            if (stockKeys.Contains((p.Cp, p.Nno))) continue;   // 属性库补集：本部门无任何库存行
            if (!MatchAttr(p, cpContains, cpWord, na1Kw, tyKw, noteKw, nameSet, tySet)) continue;
            Add(p.Cp);
        }
        return counts.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>快照路径实时获取（1.5s 超时降级返回 null 并提示）：本页批次最新库存 + tbisto 增量水位 + 新批次行。</summary>
    private static async Task<StockLiveResult?> FetchLiveAsync(State state, List<PartItem> items, string dpm, CancellationToken ct)
    {
        var ids = items.Select(p => p.Id).Where(id => id > 0).Distinct().ToList();
        try
        {
            return await Db.Parts.GetStockLiveAsync(dpm, ids, state.LastMaxStockId, state.LastMaxAttrId, ct).WaitAsync(LiveTimeout, ct);
        }
        catch (TimeoutException) { LastHint = "（库存为缓存值：网络超时）"; }
        catch (OperationCanceledException) { }
        catch { LastHint = "（库存为缓存值：网络异常）"; }
        return null;
    }

    /// <summary>按批次 id 把最新库存写回行集（PartItem 不可变，按索引替换刷新单元格）。</summary>
    private static void ApplyStock(List<PartItem> items, IReadOnlyDictionary<int, decimal> map)
    {
        for (var i = 0; i < items.Count; i++)
            if (items[i].Id > 0 && map.TryGetValue(items[i].Id, out var kc) && kc != items[i].Kcamount)
                items[i] = items[i].WithStock(kc);
    }

    /// <summary>结果缓存路径实时补齐：仅本页库存刷新（1.5s 超时降级为缓存值并提示）；
    /// fillLastIprj=最近进价列可见时照旧回填（默认隐藏列，隐藏时零开销）。</summary>
    private static async Task FillLiveAsync(List<PartItem> items, string dpm, bool fillLastIprj, CancellationToken ct)
    {
        var ids = items.Select(p => p.Id).Where(id => id > 0).Distinct().ToList();
        if (ids.Count > 0)
        {
            try
            {
                var map = await Db.Parts.GetStockByIdsAsync(ids, dpm, ct).WaitAsync(LiveTimeout, ct);
                ApplyStock(items, map);
            }
            catch (TimeoutException) { LastHint = "（库存为缓存值：网络超时）"; }
            catch (OperationCanceledException) { }
            catch { LastHint = "（库存为缓存值：网络异常）"; }
        }
        if (fillLastIprj && items.Count > 0)
        {
            try { await Db.Parts.FillLastIprjAsync(items, dpm, ct); }
            catch { /* 最近进价列回填失败不影响搜索 */ }
        }
    }

    // ---------- 订单生成选料（Order4Repository.SearchPartsAsync 同签名替身） ----------

    public static async Task<IReadOnlyList<PartPickRow>> PickSearchAsync(PartPickQuery query, string dpm, string cp,
        string dpmusem, string? suggestExp, CancellationToken ct = default)
    {
        LastHint = null;
        // 可缓存条件：无建议公式（ydqty 动态子查询）且无库存/出货量区间过滤（过滤列依赖实时聚合）
        var cacheable = string.IsNullOrWhiteSpace(suggestExp)
            && query.Num1 == 0 && query.Num2 == 9999 && query.Kc1 == 0 && query.Kc2 == 9999
            && query.PageSize > 0;
        var state = cacheable ? GetState(dpm) : null;
        if (state is not null)
        {
            var rows = FilterPick(state, cp, query);
            // 实时聚合补齐 + 新增属性实时并入（1.5s 超时降级）；返回非 null = 属性增量并入后内存重过滤的新页
            var fresh = await FetchPickLiveAsync(state, query, rows, dpm, cp, dpmusem, ct);
            return fresh ?? rows;
        }

        var key = ResultKey("pick", dpm, cp, dpmusem, query.Nno, query.Na1, query.Ty,
            query.Date1.Ticks, query.Date2.Ticks, query.Num1, query.Num2, query.Kc1, query.Kc2,
            query.Page, query.PageSize, suggestExp ?? "");
        if (TryGetResult<IReadOnlyList<PartPickRow>>(key, out var cachedRows))
            return cachedRows;
        var db = await Db.Order4.SearchPartsAsync(query, dpm, cp, dpmusem, suggestExp, ct);
        _results[key] = (DateTime.Now, db);
        return db;
    }

    /// <summary>
    /// 选料页实时补齐：本页 nno 集合批量聚合（库存/最新批次价/在途/警戒值/区间出库量，1 次往返）+
    /// tbprnoty 增量水位/新增属性行（其他电脑新增配件实时并入快照并内存重过滤），均 1.5s 超时降级。
    /// 返回 null = 未发生属性增量（rows 已就地补齐）；返回新列表 = 并入后重过滤的新页。
    /// </summary>
    private static async Task<List<PartPickRow>?> FetchPickLiveAsync(State state, PartPickQuery query, List<PartPickRow> rows,
        string dpm, string cp, string dpmusem, CancellationToken ct)
    {
        try
        {
            var live = await Db.Order4.GetPickAggregatesAsync(cp, dpm, dpmusem,
                rows.Select(r => r.Nno).ToList(), query.Date1, query.Date2, state.LastMaxAttrId, ct).WaitAsync(LiveTimeout, ct);
            foreach (var r in rows)
                if (live.Aggs.TryGetValue(r.Nno, out var a))
                {
                    _aggs[$"{cp.Trim()}|{r.Nno}"] = a;
                    ApplyAgg(r, a);
                }
            if (live.AttrMaxId > state.LastMaxAttrId && live.NewAttrRows.Count > 0)
            {
                var overflow = live.NewAttrRows.Count >= 500;
                if (overflow) _ = EnsureLoadedAsync(dpm);
                else
                {
                    state.Snapshot.AttrRows.AddRange(live.NewAttrRows);
                    state.LastMaxAttrId = live.AttrMaxId;
                }
                if (!overflow)
                {
                    // 内存重过滤：新增配件即时进入本次结果（新行不在聚合集=无库存/在途，零值即真实值）
                    var fresh = FilterPick(state, cp, query);
                    foreach (var r in fresh)
                        if (live.Aggs.TryGetValue(r.Nno, out var a))
                            ApplyAgg(r, a);
                    return fresh;
                }
            }
            return null;
        }
        catch (TimeoutException)
        {
            LastHint = "（库存/在途为缓存值：网络超时）";
            FillFromAggCache(rows, cp);
        }
        catch (OperationCanceledException) { }
        catch
        {
            LastHint = "（库存/在途为缓存值：网络异常）";
            FillFromAggCache(rows, cp);
        }
        return null;
    }

    private static void ApplyAgg(PartPickRow r, PickAggregate a)
    {
        r.Kcamount = a.Kcamount;
        r.Iprc = a.Iprc;
        r.Oprc = a.Oprc;
        r.Oprb = a.Oprb;
        r.Ztqty = a.Ztqty;
        r.Ckamount = a.Ckamount;
        // Amjj/Bmjj 走属性赋值（INPC + RefreshLevel），在 Kcamount 之后赋值保证行色按最新库存计算
        r.Amjj = a.Amjj;
        r.Bmjj = a.Bmjj;
    }

    private static void FillFromAggCache(List<PartPickRow> rows, string cp)
    {
        foreach (var r in rows)
            if (_aggs.TryGetValue($"{cp.Trim()}|{r.Nno}", out var a))
                ApplyAgg(r, a);
    }

    // ---------- 历史区缓存（进货/销售历史）：秒显 + 调用方后台重查 ----------

    public static bool TryGetHistory(string cp, string nno, out List<PartInRecordRow> ins, out List<PartSaleRecordRow> sales)
    {
        if (_history.TryGetValue(HistoryKey(cp, nno), out var hit))
        {
            ins = hit.In;
            sales = hit.Sale;
            return true;
        }
        ins = [];
        sales = [];
        return false;
    }

    public static void SetHistory(string cp, string nno, List<PartInRecordRow> ins, List<PartSaleRecordRow> sales)
        => _history[HistoryKey(cp, nno)] = (ins, sales);

    private static string HistoryKey(string cp, string nno) => $"{(cp ?? "").Trim()}|{(nno ?? "").Trim()}";

    // ---------- 内存过滤（与 SQL 条件逐条对齐） ----------

    private static (List<PartItem> Items, int Total) FilterSale(State state, string dpm, string? cp, string? na1,
        string? ty, string? note, string? brand, bool includeAttrOnly, int page, int pageSize)
    {
        var cpKw = (cp ?? "").Trim();
        var na1Kw = (na1 ?? "").Trim();
        var tyKw = (ty ?? "").Trim();
        var noteKw = (note ?? "").Trim();
        var brandKw = (brand ?? "").Trim();
        var cpContains = cpKw.StartsWith("/");
        var cpWord = cpContains ? cpKw[1..].Trim() : cpKw;
        var nameSet = BuildNameSet(state.Snapshot.Name0, na1Kw);
        var tySet = BuildTySet(state.Snapshot.Ty0, tyKw);
        var stockKeys = BuildStockKeys(state.Snapshot.StockRows);

        // 组合行集 = tbisto 本部门行 ∪ 属性库补集行（includeAttrOnly），排序 cp,id 与 SQL 骨架一致
        var rows = new List<(string Cp, int Id, PartItem Item)>();
        foreach (var r in state.Snapshot.StockRows)
        {
            if (brandKw.Length > 0 && !r.cp.Trim().Equals(brandKw, StringComparison.OrdinalIgnoreCase)) continue;
            if (!MatchStock(r, cpContains, cpWord, na1Kw, tyKw, noteKw, nameSet, tySet)) continue;
            rows.Add((r.cp.Trim(), r.id, new PartItem
            {
                Id = r.id, Dpm = r.dpm, Cp = r.cp, Nno = r.nno, Na1 = r.na1, Ty = r.ty, Fa = r.fa,
                Dc = r.dc, Unit = r.unit, Kcamount = r.kcamount, Iprj = r.iprj, Iprc = r.iprc,
                Oprc = r.oprc, Oprb = r.oprb, Posi = r.posi, Depotno = r.depotno, Note = r.note,
            }));
        }
        if (includeAttrOnly)
        {
            foreach (var p in state.Snapshot.AttrRows)
            {
                if (brandKw.Length > 0 && !p.Cp.Equals(brandKw, StringComparison.OrdinalIgnoreCase)) continue;
                if (stockKeys.Contains((p.Cp, p.Nno))) continue;   // NOT EXISTS：本部门已有库存行不补
                if (!MatchAttr(p, cpContains, cpWord, na1Kw, tyKw, noteKw, nameSet, tySet)) continue;
                rows.Add((p.Cp, 0, new PartItem
                {
                    Id = 0, Dpm = dpm, Cp = p.Cp, Nno = p.Nno, Na1 = p.Na1, Ty = p.Ty, Fa = p.Fa,
                    Dc = p.Dc, Unit = p.Unit, Kcamount = 0, Iprj = p.Iprj, Iprc = 0, Oprc = p.OprcCp,
                    Oprb = 0, Posi = "", Depotno = "", Note = p.Note,
                }));
            }
        }
        rows.Sort((a, b) =>
        {
            var c = string.Compare(a.Cp, b.Cp, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        });

        // 最低售价回填（快照自带，等价 FillOprcMinCoreAsync 的 tbprnoty 匹配）
        var attrMin = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in state.Snapshot.AttrRows)
            attrMin[$"{p.Cp}|{p.Nno}"] = p.OprcMin;
        foreach (var (_, _, item) in rows)
            item.OprcMin = attrMin.GetValueOrDefault($"{item.Cp.Trim()}|{item.Nno.Trim()}");

        var total = rows.Count;
        var skip = (Math.Max(page, 1) - 1) * pageSize;
        var items = rows.Skip(skip).Take(pageSize).Select(r => r.Item).ToList();
        return (items, total);
    }

    private static bool MatchStock(PartCacheStockRow r, bool cpContains, string cpWord, string na1Kw, string tyKw,
        string noteKw, HashSet<string>? nameSet, HashSet<string>? tySet)
    {
        if (cpWord.Length > 0 && !(cpContains
                ? r.nno.Contains(cpWord, StringComparison.OrdinalIgnoreCase)
                : r.nno.StartsWith(cpWord, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (na1Kw.Length > 0 && !(r.na1.Contains(na1Kw, StringComparison.OrdinalIgnoreCase)
                || r.nno.Contains(na1Kw, StringComparison.OrdinalIgnoreCase)
                || r.ty.Contains(na1Kw, StringComparison.OrdinalIgnoreCase)
                || (nameSet?.Contains(r.na1.Trim()) ?? false)))
            return false;
        if (tyKw.Length > 0 && !(r.ty.Contains(tyKw, StringComparison.OrdinalIgnoreCase)
                || r.na1.Contains(tyKw, StringComparison.OrdinalIgnoreCase)
                || r.note.Contains(tyKw, StringComparison.OrdinalIgnoreCase)
                || (tySet?.Contains(r.ty.Trim()) ?? false)))
            return false;
        if (noteKw.Length > 0 && !r.note.Contains(noteKw, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static bool MatchAttr(PartCacheAttrRow p, bool cpContains, string cpWord, string na1Kw, string tyKw,
        string noteKw, HashSet<string>? nameSet, HashSet<string>? tySet)
    {
        // 属性库补集 SQL 侧均为 rtrim(p.*) 口径（快照已 RTRIM）；Order4 侧 raw nno 前缀语义与 RTRIM 等价（尾随空格）
        if (cpWord.Length > 0 && !(cpContains
                ? p.Nno.Contains(cpWord, StringComparison.OrdinalIgnoreCase)
                : p.Nno.StartsWith(cpWord, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (na1Kw.Length > 0 && !(p.Na1.Contains(na1Kw, StringComparison.OrdinalIgnoreCase)
                || p.Nno.Contains(na1Kw, StringComparison.OrdinalIgnoreCase)
                || p.Ty.Contains(na1Kw, StringComparison.OrdinalIgnoreCase)
                || (nameSet?.Contains(p.Na1) ?? false)))
            return false;
        if (tyKw.Length > 0 && !(p.Ty.Contains(tyKw, StringComparison.OrdinalIgnoreCase)
                || p.Na1.Contains(tyKw, StringComparison.OrdinalIgnoreCase)
                || p.Note.Contains(tyKw, StringComparison.OrdinalIgnoreCase)
                || (tySet?.Contains(p.Ty) ?? false)))
            return false;
        if (noteKw.Length > 0 && !p.Note.Contains(noteKw, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static List<PartPickRow> FilterPick(State state, string cp, PartPickQuery query)
    {
        var cpTrim = cp.Trim();
        var nno = query.Nno?.Trim() ?? "";
        var na1 = query.Na1?.Trim() ?? "";
        var ty = query.Ty?.Trim() ?? "";
        var nameSet = BuildNameSet(state.Snapshot.Name0, na1);
        var tySet = BuildTySet(state.Snapshot.Ty0, ty);

        IEnumerable<PartCacheAttrRow> q = state.Snapshot.AttrRows
            .Where(p => p.Cp.Equals(cpTrim, StringComparison.OrdinalIgnoreCase));
        if (nno.Length > 0)
        {
            if (nno.StartsWith("*"))
            {
                var m = nno[1..];
                q = q.Where(p => p.Cxnno.StartsWith(m, StringComparison.OrdinalIgnoreCase));   // cxnno BETWEEN m AND m+'组组组组'
            }
            else if (nno.StartsWith("/"))
            {
                var m = nno[1..];
                q = q.Where(p => p.Nno.Contains(m, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                // nno BETWEEN nno AND nno+'组组组组' ≈ 前缀；nnp/nnq 精确仅在 ≥6 位且无 '+' 时参与
                var link = nno.Length >= 6 && !nno.Contains('+');
                q = q.Where(p => p.Nno.StartsWith(nno, StringComparison.OrdinalIgnoreCase)
                    || (link && (p.Nnp.Equals(nno, StringComparison.OrdinalIgnoreCase)
                              || p.Nnq.Equals(nno, StringComparison.OrdinalIgnoreCase))));
            }
        }
        if (na1.Length > 0)
            q = q.Where(p => p.Na1.Contains(na1, StringComparison.OrdinalIgnoreCase)
                || p.Nno.Contains(na1, StringComparison.OrdinalIgnoreCase)
                || p.Ty.Contains(na1, StringComparison.OrdinalIgnoreCase)
                || (nameSet?.Contains(p.Na1) ?? false));
        if (ty.Length > 0)
            q = q.Where(p => p.Ty.Contains(ty, StringComparison.OrdinalIgnoreCase)
                || p.Na1.Contains(ty, StringComparison.OrdinalIgnoreCase)
                || (tySet?.Contains(p.Ty) ?? false));

        var list = q.OrderBy(p => p.Nno, StringComparer.OrdinalIgnoreCase).ToList();   // ORDER BY p.nno
        var skip = (Math.Max(query.Page, 1) - 1) * query.PageSize;
        return list.Skip(skip).Take(query.PageSize).Select(p => new PartPickRow
        {
            Nno = p.Nno, Na1 = p.Na1, Ty = p.Ty, Fa = p.Fa, Unit = p.Unit,
            Iprj = p.Iprj, Nnp = p.Nnp, Nnq = p.Nnq, Note = p.Note, Tno = p.Tno,
            Xno = p.Xno, Dc = p.Dc, Uqty = p.Uqty, Cp = p.Cp,
        }).ToList();
    }

    private static HashSet<string>? BuildNameSet(List<PartCacheNameRow> rows, string kw)
    {
        if (kw.Length == 0) return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            if (r.Code.Contains(kw, StringComparison.OrdinalIgnoreCase))
                set.Add(r.Name);
        return set;
    }

    private static HashSet<string>? BuildTySet(List<PartCacheTyRow> rows, string kw)
    {
        if (kw.Length == 0) return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            if (r.Code.Contains(kw, StringComparison.OrdinalIgnoreCase))
                set.Add(r.Ty);
        return set;
    }

    private static HashSet<(string Cp, string Nno)> BuildStockKeys(List<PartCacheStockRow> rows)
    {
        var set = new HashSet<(string, string)>();
        foreach (var r in rows)
            set.Add((r.cp.Trim(), r.nno.Trim()));
        return set;
    }

    // ---------- 结果缓存 ----------

    private static string ResultKey(params object?[] parts)
        => string.Join("|", parts.Select(p => p?.ToString() ?? ""));

    private static bool TryGetResult<T>(string key, out T result)
    {
        if (_results.TryGetValue(key, out var hit) && DateTime.Now - hit.At < ResultTtl && hit.Result is T typed)
        {
            result = typed;
            return true;
        }
        result = default!;
        return false;
    }
}
