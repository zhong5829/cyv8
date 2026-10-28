﻿using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Infrastructure.DocNo;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Finance;

// =====================================================================================
// 出纳管理 G 组（契约：开发文档/契约摘录/出纳管理G组契约.md）
//   G.2/G.3 收款/付款（fr_xlsk）  G.4 应收应付（fr_ysyf）  G.5 收支流水帐（fr_lsz）
// P_FINANCE 财务凭证分支（finvouch/finvid/finsub）随账务模块（I 组）启用，本批一律 FINVOUCH=.F.。
// 部署常量分支（沙河丰田/庆丰丰田/万德/宝狮/广保丰田/致友忠达/福州玖玖）不适用本部署，按主路径实现。
// =====================================================================================

#region G.2/G.3 收款/付款（fr_xlsk）

public sealed record XlskCheckTarget(string Type) // DocNoService 防重目标：tbysyf.cno + type 过滤
{
    public DocNoService.DocNoCheckTarget ToTarget() => new("tbysyf", "cno", Type);
}

public sealed class XlskOutstandingRow
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string Paper { get; set; } = "";     // tbysyf.cno
    public DateTime Datime { get; set; }
    public decimal Ys { get; set; }
    public decimal Yf { get; set; }
    public decimal Je { get; set; }             // 收款取 ys / 付款取 yf（CmdGU 映射）
    public decimal Skmoney { get; set; }
    public decimal Wsje { get; set; }           // 收/付款类 Je+skmoney，其余 Je-skmoney（未夹取）
    public string Isord { get; set; } = "";
    public string Ch { get; set; } = "";
    public string Dp { get; set; } = "";
    public string Remark { get; set; } = "";
    public string Dpm { get; set; } = "";
    /// <summary>被旧系统纳入过滤规则排除的行（TYPE='预收' 或已结清），UI 仍可用于累计余额。</summary>
    public bool Included { get; set; }
}

public sealed class XlskDocLine
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string Paper { get; set; } = "";
    public DateTime Date { get; set; }
    public decimal Je { get; set; }
    public decimal Ysje { get; set; }
    public decimal Wsje { get; set; }
    public decimal Bcje { get; set; }
    public bool Checked { get; set; }
    public string Isord { get; set; } = "";
    public string Ch { get; set; } = "";
    public string Dp { get; set; } = "";
    public string Remark { get; set; } = "";
    public string Dpm { get; set; } = "";
}

public sealed class XlskDoc
{
    public string Cno { get; set; } = "";
    public string Gname { get; set; } = "";
    public string Xm { get; set; } = "";
    public DateTime Datime { get; set; }
    public string Fkfs { get; set; } = "";
    public string Pno { get; set; } = "";
    public decimal PaySum { get; set; }   // 汇总行 ys/yf 取负
    public decimal Yue { get; set; }      // lj + (付款? yf : -ys)
    public DateTime LszDate { get; set; }
    public List<XlskDocLine> Lines { get; set; } = [];
}

public sealed class XlskSaveLine
{
    public int Id { get; set; }
    public string Paper { get; set; } = "";
    public string LineType { get; set; } = "";
    public decimal Bcje { get; set; }     // 本次勾选金额（含负数语义）
    public decimal Wsje { get; set; }     // 保存前未付金额（结清联动判定用）
}

public sealed class XlskSaveCommand
{
    public string Type { get; set; } = "收款";   // 收款 / 付款
    public DateTime Date { get; set; }
    public DateTime LszDate { get; set; }
    public string Fkfs { get; set; } = "";
    public string Pno { get; set; } = "";
    public decimal PaySum { get; set; }
    public string Gname { get; set; } = "";
    public string Xm { get; set; } = "";
    public string BankName { get; set; } = "";
    public decimal LjValue { get; set; }          // TXT2（余额列，落 tbysyf.lj）
    public List<XlskSaveLine> Lines { get; set; } = [];
    public bool FinVouchEnabled { get; set; }     // P_FINANCE && P_FININVOU（fininvou=2 且 l_use=1）
    public string SkqxSubname { get; set; } = ""; // COMBO1 收付去向科目名（finsub detacode=1 type C/B）
    /// <summary>
    /// 预收定金路径（旧 fr_xlsk IS_YSK=.T. 且 YSK_JE=勾选总额，skinfor 表单导入）。
    /// 仅此路径受「关闭预收款已结帐」配置控制；普通收付款路径恒写 skmoney（A_.row75 L162-163）。
    /// </summary>
    public bool IsPresaleDeposit { get; set; }
}

public sealed class XlskSaveResult
{
    public string DocNo { get; set; } = "";
    public decimal Yue { get; set; }
}

public sealed class XlskRestoreLine
{
    public int Id { get; set; }
    public string Paper { get; set; } = "";
    public string LineType { get; set; } = "";
    public decimal Bcje { get; set; }     // 载入时的本次金额（未勾选行=0）
    public decimal Wsje { get; set; }     // 载入时未付金额（=0 即已结清，触发 jzdate 回退）
    public bool Checked { get; set; }
}

public interface IXlskRepository
{
    Task<IReadOnlyList<string>> GetBankNamesAsync(CancellationToken ct = default);
    Task<bool> IsLszDateLockedAsync(string dpm, CancellationToken ct = default);
    Task<IReadOnlyList<XlskOutstandingRow>> GetOutstandingAsync(string gname, string type, bool includeRebate, CancellationToken ct = default);
    Task<XlskDoc?> LoadDocAsync(string cno, string type, CancellationToken ct = default);
    Task<bool> HasMonthlyCloseAsync(CancellationToken ct = default);
    Task<bool> IsMonthlyLockedAsync(DateTime docDate, CancellationToken ct = default);
    Task<XlskSaveResult> SaveAsync(XlskSaveCommand cmd, string dpm, string per, CancellationToken ct = default);
    Task<decimal> RestoreAsync(string cno, string type, string gname, IReadOnlyList<XlskRestoreLine> lines, string dpm, string per, bool finVouch = false, CancellationToken ct = default);
    Task<string?> GetNextDocNoAsync(string cno, string type, bool forward, CancellationToken ct = default);

    /// <summary>skinfor 业务类型字典（tbsysset type='业务类型'）。</summary>
    Task<IReadOnlyList<string>> GetBizTypesAsync(CancellationToken ct = default);

    /// <summary>提取未收款（fr_xlsk_tq Init row17/19）：单条 tbysyf 未结行 + tbxlrc 车牌。</summary>
    Task<IReadOnlyList<XlskTqRow>> GetUnsettledAsync(string type, string where, CancellationToken ct = default);

    /// <summary>收款导入分析（fr_skdr __.row34）：按单号+车牌校验剩余应收。</summary>
    Task<IReadOnlyList<SkdrRow>> AnalyzeSkdrAsync(IReadOnlyList<(string Cno, string Ch, decimal DrMoney)> rows, string gname, CancellationToken ct = default);

    /// <summary>客户余额（fr_xlsk_tq 返回后 TXTYUE：sum(ys-yf) 全行累计）。</summary>
    Task<decimal> GetCustomerYueAsync(string gname, CancellationToken ct = default);

    /// <summary>入库单随付打印（fr_xlsk 墌.row67）：勾选入库单明细 tbistoed（按 cno）。</summary>
    Task<IReadOnlyList<IstofkRow>> GetIstofkRowsAsync(IReadOnlyList<string> cnos, string client, CancellationToken ct = default);

    /// <summary>收/付款单明细（fr_skmx Init：tbysyf 表头 + tblsz 勾选行三段 UNION，回扣行优先）。</summary>
    Task<SkmxDoc?> GetSkMxAsync(string type, string skno, string dpm, CancellationToken ct = default);
}

/// <summary>收/付款单明细（fr_skmx：表头 + 勾选行，只读）。</summary>
public sealed class SkmxDoc
{
    public string Gname { get; set; } = "";
    public DateTime Datime { get; set; }
    public string Cno { get; set; } = "";
    public string Pno { get; set; } = "";
    public string Fkfs { get; set; } = "";
    public decimal Amount { get; set; }   // 收款=ABS(ys) / 付款=yf
    public IReadOnlyList<SkmxRow> Lines { get; set; } = [];
}

public sealed class SkmxRow
{
    public string Type { get; set; } = "";
    public string Paper { get; set; } = "";
    public decimal Je { get; set; }       // 收款=ys / 付款=yf
    public DateTime Datime { get; set; }
}

/// <summary>入库单随付打印行（re_istofk c_rkdy：明细 + 沙河丰田人员分派 zu/zuc）。</summary>
public sealed class IstofkRow
{
    public string Cno { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public decimal Jkamount { get; set; }
    public decimal Iprc { get; set; }
    public DateTime Indate { get; set; }
    public string Gu { get; set; } = "";
    public string Gyname { get; set; } = "";
    public int Zu { get; set; }
    public string Zuc { get; set; } = "";
}

/// <summary>提取未收款行（fr_xlsk_tq c_wsk 列集）。</summary>
public sealed class XlskTqRow
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string Cno { get; set; } = "";
    public DateTime Datime { get; set; }
    public string Gname { get; set; } = "";
    public string Pno { get; set; } = "";
    public decimal Amount { get; set; }   // 收款:ys-skmoney / 付款:yf-skmoney
    public decimal Skmoney { get; set; }
    public string Xm { get; set; } = "";
    public string Ch { get; set; } = "";
    public string Tgh { get; set; } = "";
    public string Dpm { get; set; } = "";
}

/// <summary>收款导入分析行（fr_skdr c_skdr）。</summary>
public sealed class SkdrRow
{
    public string Cno { get; set; } = "";
    public string Ch { get; set; } = "";
    public decimal DrMoney { get; set; }
    /// <summary>剩余应收 = isnull(sum(ys-yf-skmoney),0)（单号+客户）。</summary>
    public decimal Remain { get; set; }
    /// <summary>分析状态：'' 成功 / 金额大于应收金额 / 工单已经收款 / 此工号收款的客户应该是[xxx] / 找不到对应工号。</summary>
    public string Status { get; set; } = "";
    /// <summary>校验通过（金额≤剩余应收且≥0）。</summary>
    public bool Ok => Status.Length == 0;
}

/// <summary>流水提取候选行（fr_lsz c_tiqu 六列）。</summary>
public sealed class LszTiquRow
{
    public DateTime Outdate { get; set; }
    public string Paper { get; set; } = "";
    public decimal Jine { get; set; }
    public string Gname { get; set; } = "";
    public string Guno { get; set; } = "";
}

/// <summary>
/// 收款/付款管理仓储（fr_xlsk 契约）。
/// 保存（A_.row75）：tblsz 去向保障 → 取号（SK/FK，防重 tbysyf.cno）→ INSERT tbysyf 汇总行（ys/yf 取负、lj=TXT2、per）
///   → 逐勾选行：skmoney+=Δ（收/付款类行取 ABS，其余原值）→ INSERT tblsz 流水（ytu=收款/付款、remark=类型：单号、
///   paper 空→'id:'+id）→ 结清联动（销售→tbsada.jzdate；维修→tbysyf 无未结→tbxlrc.jzdate）→
///   UPDATE 汇总行 skmoney=勾选总额 → 维修行 colmoney/发票号拼 xm → tbfpgl（发票号非空）→ ysyf_refresh。
/// 还原（c_.row74）：月结保护 → DELETE tbysyf WHERE cno → 维修 colmoney 回退 → 逐勾选行 skmoney-=Δ、
///   结清行 jzdate 回 1900-01-01、按 paper 反查 tblsz 二次冲抵 → DELETE tblsz WHERE skno → ysyf_refresh。
/// 死代码备案：保存/还原内「按 paper 反查 tblsz 修改汇总 skmoney」的保存侧块只拼日志不执行（旧系统原样）。
/// </summary>
public sealed class XlskRepository(SqlConnectionFactory factory, IDocNoService docNo) : IXlskRepository
{
    public async Task<IReadOnlyList<string>> GetBankNamesAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT RTRIM(name) FROM tbsysset WITH (NOLOCK) WHERE type = 'bankname' ORDER BY id",
            cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>tbsysset type='sfkdate' name='可修改收付款日期' valuel=0 有行 → 流水日期只读（G22/@ 再放开由 UI 判）。</summary>
    public async Task<bool> IsLszDateLockedAsync(string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var n = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM tbsysset WITH (NOLOCK) WHERE type = 'sfkdate' AND name = N'可修改收付款日期' AND valuel = 0 AND dpm = @dpm",
            new { dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        return n > 0;
    }

    /// <summary>CmdGU（鏤.row41）：客户应收应付未结清单。行过滤与 WSJE 口径在服务端按旧游标规则计算。</summary>
    public async Task<IReadOnlyList<XlskOutstandingRow>> GetOutstandingAsync(string gname, string type, bool includeRebate, CancellationToken ct = default)
    {
        gname = gname.Trim();
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(int id, string type, string cno, DateTime datime, decimal ys, decimal yf,
            decimal skmoney, string isord, string ch, string dp, string remark, string dpm)>(new CommandDefinition(
            """
            SELECT id, RTRIM(type) type, RTRIM(cno) cno, datime, ys, yf, skmoney, RTRIM(ISNULL(isord,'')) isord,
                   ch = CASE WHEN RTRIM(type) = N'维修' THEN ISNULL((SELECT TOP (1) RTRIM(ch) FROM tbxlrc WITH (NOLOCK) WHERE gh = tbysyf.cno), '') ELSE '' END,
                   dp = CASE WHEN RTRIM(type) = N'维修' THEN ISNULL((SELECT TOP (1) RTRIM(dp) FROM tbxlrc WITH (NOLOCK) WHERE gh = tbysyf.cno), '') ELSE '' END,
                   remark = CASE WHEN RTRIM(type) = N'销售' THEN ISNULL((SELECT TOP (1) RTRIM(remark) FROM tbsada WITH (NOLOCK) WHERE paper = tbysyf.cno AND remark > ''), '') ELSE '' END,
                   RTRIM(ISNULL(dpm,'')) dpm
            FROM tbysyf WITH (NOLOCK)
            WHERE gname = @gname AND (@inc = 1 OR (pno <> N'回扣' AND pno <> 'KH'))
            ORDER BY datime
            """,
            new
            {
                gname = Ansi(gname, 60),
                inc = includeRebate ? 1 : 0,
            }, cancellationToken: ct));
        var list = new List<XlskOutstandingRow>();
        foreach (var r in rows)
        {
            var isXlsk = r.type is "收款" or "付款";
            var je = type == "收款" ? r.ys : r.yf;
            var row = new XlskOutstandingRow
            {
                Id = r.id,
                Type = r.type,
                Paper = r.cno,
                Datime = r.datime,
                Ys = r.ys,
                Yf = r.yf,
                Je = je,
                Skmoney = r.skmoney,
                Wsje = isXlsk ? je + r.skmoney : je - r.skmoney,
                Isord = r.isord,
                Ch = r.ch ?? "",
                Dp = r.dp ?? "",
                Remark = r.remark ?? "",
                Dpm = r.dpm ?? "",
            };
            // 行纳入过滤（row41 L57）：非预收 且 有发生额 且 未结清
            row.Included = r.type != "预收" && r.ys != 0 && r.ys - r.yf != 0 &&
                           Math.Abs(r.skmoney) != Math.Abs(r.ys - r.yf) &&
                           (type == "收款" ? r.ys != 0 : r.yf != 0);
            list.Add(row);
        }
        return list;
    }

    /// <summary>载入已有单（skno，row2）：表头 tbysyf WHERE cno + 流水行 tblsz WHERE skno（含回查匹配规则）。</summary>
    public async Task<XlskDoc?> LoadDocAsync(string cno, string type, CancellationToken ct = default)
    {
        cno = cno.Trim();
        await using var conn = await factory.OpenAsync(ct);
        var head = (await conn.QueryAsync<(string cno, string gname, string xm, DateTime datime, string fkfs,
            string pno, decimal ys, decimal yf, decimal lj)>(new CommandDefinition(
            "SELECT TOP (1) RTRIM(cno), RTRIM(gname), ISNULL(RTRIM(xm),''), datime, RTRIM(fkfs), ISNULL(RTRIM(pno),''), ys, yf, lj " +
            "FROM tbysyf WITH (NOLOCK) WHERE cno = @cno AND type = @type",
            new { cno = Ansi(cno, 30), type = Ansi(type, 8) }, cancellationToken: ct))).FirstOrDefault();
        if (head.cno is null)
            return null;
        var doc = new XlskDoc
        {
            Cno = head.cno,
            Gname = head.gname ?? "",
            Xm = head.xm ?? "",
            Datime = head.datime,
            Fkfs = head.fkfs ?? "",
            Pno = head.pno ?? "",
            PaySum = (type != "收款" ? head.yf : head.ys) * -1,
            Yue = head.lj + (type != "收款" ? head.yf : 0.00m - head.ys),
        };
        doc.LszDate = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT TOP (1) ls_date FROM tblsz WITH (NOLOCK) WHERE skno = @cno ORDER BY id",
            new { cno = Ansi(cno, 10) }, cancellationToken: ct)) ?? DateTime.Today;

        // 流水行重建（row2 L63-99）：每行 BZ='√'、BCJE=该行原始流水额（收款取 ys / 付款取 yf）；
        // paper='id:' 按 id 回查；pno='回扣'/'KH' 按 cno+pno；否则按 cno；
        // 多候选按 skmoney 依次定位：=金额 → >金额 → >0 → 首行
        var flows = (await conn.QueryAsync<(int id, string paper, decimal ys, decimal yf, string pno)>(new CommandDefinition(
            "SELECT id, RTRIM(ISNULL(paper,'')), ISNULL(ys,0), ISNULL(yf,0), RTRIM(ISNULL(pno,'')) FROM tblsz WITH (NOLOCK) WHERE skno = @cno ORDER BY id",
            new { cno = Ansi(cno, 10) }, cancellationToken: ct))).ToList();
        var lines = new List<XlskDocLine>();
        foreach (var f in flows)
        {
            XlskDocLine line;
            if (f.paper.StartsWith("id:", StringComparison.Ordinal) &&
                int.TryParse(f.paper[3..], out var refId))
            {
                var tgt = (await conn.QueryAsync<(string type, decimal ys, decimal yf, decimal skmoney, string isord)>(new CommandDefinition(
                    "SELECT TOP (1) RTRIM(type), ys, yf, skmoney, RTRIM(ISNULL(isord,'')) FROM tbysyf WITH (NOLOCK) WHERE id = @id",
                    new { id = refId }, cancellationToken: ct))).FirstOrDefault();
                line = BuildLine(refId, tgt.type, f, tgt.ys, tgt.yf, tgt.skmoney, tgt.isord, type, doc.Gname, head.datime);
            }
            else if (f.pno is "回扣" or "KH")
            {
                line = await MatchFlowAsync(conn, f.paper, f.pno, byPno: true, f, type, doc.Gname, head.datime, ct);
            }
            else
            {
                line = await MatchFlowAsync(conn, f.paper, f.pno, byPno: false, f, type, doc.Gname, head.datime, ct);
            }
            lines.Add(line);
        }
        doc.Lines = lines;
        return doc;
    }

    private static XlskDocLine BuildLine(int id, string lineType,
        (int id, string paper, decimal ys, decimal yf, string pno) flow,
        decimal ys, decimal yf, decimal skmoney, string isord, string docType, string gname, DateTime headDate)
    {
        var je = docType == "收款" ? ys : yf;
        return new XlskDocLine
        {
            Id = id,
            Type = lineType ?? "",
            Paper = flow.paper,
            Date = headDate,
            Je = je,
            Ysje = skmoney,
            Wsje = Math.Max(je - skmoney, 0),
            Bcje = docType == "收款" ? flow.ys : flow.yf,   // 载入行 BCJE=原始流水额（row2 L71）
            Checked = true,                                  // 载入行默认全勾（BZ='√'）
            Isord = isord ?? "",
        };
    }

    private async Task<XlskDocLine> MatchFlowAsync(SqlConnection conn, string paper, string pno, bool byPno,
        (int id, string paper, decimal ys, decimal yf, string pno) flow,
        string docType, string gname, DateTime headDate, CancellationToken ct)
    {
        var sql = byPno
            ? "SELECT id, RTRIM(type), ys, yf, skmoney, RTRIM(ISNULL(isord,'')) FROM tbysyf WITH (NOLOCK) WHERE cno = @paper AND pno = @pno"
            : "SELECT id, RTRIM(type), ys, yf, skmoney, RTRIM(ISNULL(isord,'')) FROM tbysyf WITH (NOLOCK) WHERE cno = @paper";
        var cands = (await conn.QueryAsync<(int id, string type, decimal ys, decimal yf, decimal skmoney, string isord)>(
            new CommandDefinition(sql, new { paper = Ansi(paper, 30), pno = Ansi(pno, 10) }, cancellationToken: ct))).AsList();
        if (cands.Count == 0)
        {
            return new XlskDocLine { Paper = flow.paper, Date = headDate, Type = "" };
        }
        var amount = flow.ys + flow.yf;
        var pick = cands.FirstOrDefault(c => c.skmoney == amount);
        if (pick.id == 0) pick = cands.FirstOrDefault(c => c.skmoney > amount);
        if (pick.id == 0) pick = cands.FirstOrDefault(c => c.skmoney > 0);
        if (pick.id == 0) pick = cands[0];
        return BuildLine(pick.id, pick.type, flow, pick.ys, pick.yf, pick.skmoney, pick.isord, docType, gname, headDate);
    }

    /// <summary>月结开关（tbsysset「启用财务月结」，与统计恢复同口径）。</summary>
    public async Task<bool> HasMonthlyCloseAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT ISNULL(CAST(valuel AS bit), 0) FROM tbsysset WITH (NOLOCK) WHERE RTRIM(name) = N'启用财务月结' AND type = 'system'",
            cancellationToken: ct)) != 0;
    }

    /// <summary>还原月结保护（c_.row74 L25-32）：tbyj.max(yjdate)+1 &gt; 单据日期 → 拒绝。</summary>
    public async Task<bool> IsMonthlyLockedAsync(DateTime docDate, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var yj = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MAX(yjdate) FROM tbyj WITH (NOLOCK)", cancellationToken: ct));
        return yj.HasValue && yj.Value.AddDays(1) > docDate;
    }

    public async Task<XlskSaveResult> SaveAsync(XlskSaveCommand cmd, string dpm, string per, CancellationToken ct = default)
    {
        var type = cmd.Type.Trim();
        if (type is not ("收款" or "付款")) throw new AppException("XLSK_TYPE_INVALID", "类型必须为收款或付款", 422);
        if (string.IsNullOrWhiteSpace(cmd.Gname)) throw new AppException("GU_REQUIRED", "请先选择客户", 422);
        if (string.IsNullOrWhiteSpace(cmd.Fkfs)) throw new AppException("FKFS_REQUIRED", "请选择收付款方式", 422);
        var gname = cmd.Gname.Trim();

        await using var conn = await factory.OpenAsync(ct);

        // 1) tblsz 去向保障（A_.row75 L36-43）
        var hasBank = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM tblsz WITH (NOLOCK) WHERE guno = @fkfs",
            new { fkfs = Ansi(cmd.Fkfs, 10) }, cancellationToken: ct));
        if (hasBank == 0)
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO tblsz (lj, guno, dpm) VALUES (0, @fkfs, @dpm)",
                new { fkfs = Ansi(cmd.Fkfs, 10), dpm = Ansi(dpm, 10) }, cancellationToken: ct));

        // 2) 取号：先保障 tbdjgs 前缀（row2 getnewdh L197-210，PASSTH 原样），再 getnewdh 防重 tbysyf.cno
        var djName = type + "单";
        var prefix = type == "收款" ? "SK" : "FK";
        await conn.ExecuteAsync(new CommandDefinition(
            """
            IF NOT EXISTS (SELECT 1 FROM tbdjgs WHERE dpm = @dpm AND djname = @dj)
                INSERT INTO tbdjgs (djname, djjx, dyear, dpm) VALUES (@dj, @prefix, @year, @dpm)
            """,
            new { dpm = Ansi(dpm, 10), dj = DbStr(djName, 10), prefix = Ansi(prefix, 4), year = DateTime.Today.Year.ToString() },
            cancellationToken: ct));
        var newDocNo = await docNo.GetNewDocNoAsync(djName, dpm, new XlskCheckTarget(type).ToTarget(), ct);

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 3) INSERT tbysyf 汇总行（A_.row75 L64-76；PASSTH 自动补 dpm）
            var summaryId = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                INSERT INTO tbysyf (type, cno, gname, xm, datime, fkfs, pno, ys, yf, lj, per, dpm)
                VALUES (@type, @cno, @gname, @xm, @datime, @fkfs, @pno, @ys, @yf, @lj, @per, @dpm);
                SELECT CAST(SCOPE_IDENTITY() AS int);
                """,
                new
                {
                    type = Ansi(type, 8),
                    cno = Ansi(newDocNo, 30),
                    gname = Ansi(gname, 60),
                    xm = new DbString { Value = cmd.Xm ?? "", IsAnsi = false, Length = 250 },
                    datime = cmd.Date,
                    fkfs = Ansi(cmd.Fkfs, 10),
                    pno = new DbString { Value = cmd.Pno ?? "", IsAnsi = true, Length = 50 },
                    ys = type == "收款" ? -cmd.PaySum : 0m,
                    yf = type == "付款" ? -cmd.PaySum : 0m,
                    lj = cmd.LjValue,
                    per = Ansi(per, 10),
                    dpm = Ansi(dpm, 10),
                }, transaction: tx, cancellationToken: ct));

            var totalBcje = cmd.Lines.Where(l => l.Bcje != 0).Sum(l => Math.Abs(l.Bcje));

            // 4) 逐勾选行
            foreach (var line in cmd.Lines.Where(l => l.Bcje != 0))
            {
                var delta = line.LineType is "收款" or "付款" ? Math.Abs(line.Bcje) : line.Bcje;
                // 4a. skmoney 累加（A_.row75 L83-89）
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbysyf SET skmoney = skmoney + @delta WHERE id = @id",
                    new { delta, line.Id }, transaction: tx, cancellationToken: ct));
                // 4b. INSERT tblsz 流水行（A_.row75 L92-112）
                var paper = string.IsNullOrWhiteSpace(line.Paper)
                    ? "id:" + (line.Id == 0 ? summaryId : line.Id)
                    : line.Paper.Trim();
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO tblsz (ls_date, guno, ytu, remark, ys, yf, skno, paper, gname, bankname, dpm, pno)
                    VALUES (@lsDate, @guno, @ytu, @remark, @ys, @yf, @skno, @paper, @gname, @bankname, @dpm, @pno)
                    """,
                    new
                    {
                        lsDate = cmd.LszDate,
                        guno = Ansi(cmd.Fkfs, 10),
                        ytu = Ansi(type, 20),
                        remark = Ansi($"{line.LineType}：{line.Paper}", 50),   // 旧码不裁剪冒号（paper 空则保留"类型："）
                        ys = type == "收款" ? line.Bcje : 0m,
                        yf = type == "付款" ? line.Bcje : 0m,
                        skno = Ansi(newDocNo, 10),
                        paper = Ansi(paper, 30),
                        gname = Ansi(gname, 60),
                        bankname = Ansi(cmd.BankName ?? "", 40),
                        dpm = Ansi(dpm, 10),
                        pno = Ansi("", 10),
                    }, transaction: tx, cancellationToken: ct));
                // 4c. 结清联动（A_.row75 L116-135）：先 REPLACE WSJE=max(WSJE-BCJE,0) 再判 =0 → 扣减后≤0 即结清
                if (line.Wsje - line.Bcje <= 0)
                {
                    if (line.LineType == "销售")
                    {
                        await conn.ExecuteAsync(new CommandDefinition(
                            "UPDATE tbsada SET jzdate = @date WHERE paper = @paper",
                            new { date = cmd.Date, paper = Ansi(line.Paper, 15) }, transaction: tx, cancellationToken: ct));
                    }
                    else if (line.LineType == "维修")
                    {
                        var unsettled = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                            "SELECT COUNT(1) FROM tbysyf WITH (NOLOCK) WHERE cno = @paper AND ys <> skmoney",
                            new { paper = Ansi(line.Paper, 30) }, transaction: tx, cancellationToken: ct));
                        if (unsettled == 0)
                            await conn.ExecuteAsync(new CommandDefinition(
                                "UPDATE tbxlrc SET jzdate = @date WHERE gh = @paper",
                                new { date = cmd.Date, paper = Ansi(line.Paper, 15) }, transaction: tx, cancellationToken: ct));
                    }
                }
                // 4d. 汇总行 skmoney=勾选总额（A_.row75 L163，逐行重复执行幂等）。
                // 预收定金路径受「关闭预收款已结帐」配置控制（A_.row75 L152-161）：
                // 配置行存在且 valuel=.T. → 空转（skmoney=skmoney+0）；否则照常写。
                // 普通收付款路径恒写（L162-163 ELSE 分支）。
                var skipSummarySkmoney = cmd.IsPresaleDeposit
                    && await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                        "SELECT ISNULL(valuel, 0) = 1 FROM tbsysset WITH (NOLOCK) " +
                        "WHERE name = N'关闭预收款已结帐' AND type = 'system' AND dpm = @dpm",
                        new { dpm = Ansi(dpm, 10) }, transaction: tx, cancellationToken: ct));
                if (!skipSummarySkmoney)
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE tbysyf SET skmoney = @total WHERE cno = @cno AND type = @type AND dpm = @dpm",
                        new { total = totalBcje, cno = Ansi(newDocNo, 30), type = Ansi(type, 8), dpm = Ansi(dpm, 10) },
                        transaction: tx, cancellationToken: ct));
            }

            // 5) 维修行追加（A_.row75 L166-188）：colmoney 累加（全部勾选维修行）+ 发票号拼 xm
            foreach (var line in cmd.Lines.Where(l => l.Bcje != 0 && l.LineType == "维修"))
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbxlrc SET colmoney = colmoney + @bcje WHERE gh = @gh",
                    new { bcje = line.Bcje, gh = Ansi(line.Paper, 15) }, transaction: tx, cancellationToken: ct));
            }
            if (!string.IsNullOrWhiteSpace(cmd.Pno) && cmd.Pno.Trim().Length > 1)
            {
                foreach (var line in cmd.Lines.Where(l => l.Bcje != 0 && l.LineType == "维修"))
                {
                    var xm = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                        "SELECT TOP (1) RTRIM(ISNULL(xm,'')) FROM tbxlrc WITH (NOLOCK) WHERE gh = @gh",
                        new { gh = Ansi(line.Paper, 15) }, transaction: tx, cancellationToken: ct));
                    var cur = xm ?? "";
                    if (!cur.Contains(cmd.Pno.Trim(), StringComparison.Ordinal))
                    {
                        var next = (string.IsNullOrEmpty(cur) ? "" : ",") + cmd.Pno.Trim();
                        await conn.ExecuteAsync(new CommandDefinition(
                            "UPDATE tbxlrc SET xm = @xm WHERE gh = @gh",
                            new { xm = new DbString { Value = cur + next, IsAnsi = false, Length = 250 }, gh = Ansi(line.Paper, 15) },
                            transaction: tx, cancellationToken: ct));
                    }
                }
            }

            // 5.5) finvouch 联动（A_.row75 L190-237，P_FINANCE && FINVOUCH）：两行分录
            if (cmd.FinVouchEnabled)
            {
                if (string.IsNullOrWhiteSpace(cmd.SkqxSubname))
                    throw new AppException("SKQX_REQUIRED", $"请确定{type}去向.", 422);
                var skqx = (await conn.QueryAsync<(string Subcode, string Subname, string Type, bool Fcash)>(new CommandDefinition(
                    """
                    SELECT TOP (1) rtrim(subcode), rtrim(subname), rtrim(ISNULL(type,'')), ISNULL(fcash,0)
                    FROM finsub WITH (NOLOCK)
                    WHERE rtrim(subname) = @n AND detacode = 1 AND (type = 'C' OR type = 'B')
                    ORDER BY id
                    """, new { n = Ansi(cmd.SkqxSubname.Trim(), 40) }, transaction: tx, cancellationToken: ct))).FirstOrDefault();
                if (skqx.Subcode is null)
                    throw new AppException("SKQX_NOT_FOUND", "收付去向科目不存在", 422);
                // finvid 全行推进（SPT "update finvid set vid=vid+1" 无 WHERE，旧码原样）；取 dpm 行新号
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE finvid SET vid = vid + 1", transaction: tx, cancellationToken: ct));
                var fvid = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT vid FROM finvid WITH (NOLOCK) WHERE dpm = @dpm", new { dpm = Ansi(dpm, 10) },
                    transaction: tx, cancellationToken: ct));
                var fperiod = cmd.Date.ToString("yyyyMM");
                var fvcode = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT ISNULL(MAX(vcode), 0) + 1 FROM finvouch WITH (NOLOCK) WHERE period = @p",
                    new { p = Ansi(fperiod, 6) }, transaction: tx, cancellationToken: ct));
                var bcje = cmd.Lines.Where(l => l.Bcje != 0).Sum(l => l.Bcje);   // SUM BCJE FOR BZ='√'（原值，非 ABS）
                var note = $"第-{newDocNo}-号{type}";
                var dcdir1 = type == "收款" ? "借" : "贷";
                var dcdir2 = type == "收款" ? "贷" : "借";
                // 行 1：收付去向科目行
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO finvouch (vouchid, vcode, date, period, recoid, note, subcode, subname, type,
                                          paytype, paydate, payno, fcash, debit, credit, dcdir, vouword, client,
                                          invcode, opertype, opercode, maker, dpm)
                    VALUES (@vouchid, @vcode, @date, @period, 1, @note, @subcode, @subname, @type,
                            @paytype, @paydate, @payno, @fcash, @debit, @credit, @dcdir, @vouword, @client,
                            @invcode, '', '', @maker, @dpm)
                    """,
                    new
                    {
                        vouchid = fvid, vcode = fvcode, date = cmd.Date, period = Ansi(fperiod, 6),
                        note = Ansi(note, 60), subcode = Ansi(skqx.Subcode, 18), subname = Ansi(skqx.Subname, 40),
                        type = Ansi(skqx.Type, 1), paytype = Ansi(cmd.Fkfs, 10), paydate = cmd.Date,
                        payno = Ansi(newDocNo, 30), fcash = skqx.Fcash,
                        debit = type == "收款" ? bcje : 0m, credit = type == "付款" ? bcje : 0m,
                        dcdir = Ansi(dcdir1, 2), vouword = Ansi(type, 8), client = Ansi(gname, 60),
                        invcode = Ansi(cmd.Pno.Trim(), 20), maker = Ansi(per, 10), dpm = Ansi(dpm, 10),
                    }, transaction: tx, cancellationToken: ct));
                // 行 2：往来科目行（ADDSUB 尾码进位）
                var cty = type == "收款" ? "A" : "P";
                var clientSub = await FinSubCodes.AddSubClientAsync(conn, tx, gname, cty, dpm, ct);
                var clientSubName = await FinSubCodes.GSubNameAsync(conn, tx, clientSub, ct);
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO finvouch (vouchid, vcode, date, period, recoid, note, subcode, subname, type,
                                          paytype, paydate, payno, fcash, debit, credit, dcdir, vouword, client,
                                          opertype, opercode, maker, dpm)
                    VALUES (@vouchid, @vcode, @date, @period, 2, @note, @subcode, @subname, @type,
                            @paytype, @paydate, @payno, 0, @debit, @credit, @dcdir, @vouword, @client,
                            '', '', @maker, @dpm)
                    """,
                    new
                    {
                        vouchid = fvid, vcode = fvcode, date = cmd.Date, period = Ansi(fperiod, 6),
                        note = Ansi(note, 60), subcode = Ansi(clientSub, 18), subname = Ansi(clientSubName, 40),
                        type = Ansi(cty, 1), paytype = Ansi(cmd.Fkfs, 10), paydate = cmd.Date,
                        payno = Ansi(newDocNo, 30),
                        debit = type == "付款" ? bcje : 0m, credit = type == "收款" ? bcje : 0m,
                        dcdir = Ansi(dcdir2, 2), vouword = Ansi(type, 8), client = Ansi(gname, 60),
                        maker = Ansi(per, 10), dpm = Ansi(dpm, 10),
                    }, transaction: tx, cancellationToken: ct));
            }

            // 6) 发票管理（A_.row75 L244-265）：发票号非空 → INSERT tbfpgl（ctype=首个勾选行类型）
            if (!string.IsNullOrWhiteSpace(cmd.Pno))
            {
                var ctype = cmd.Lines.FirstOrDefault(l => l.Bcje != 0)?.LineType ?? "销售";
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO tbfpgl (pno, gname, fpdate, fpje, jsr, djno, ctype, fptype, note, fkfs, dpm)
                    VALUES (@pno, @gname, @fpdate, @fpje, @jsr, @djno, @ctype, '', '', @fkfs, @dpm)
                    """,
                    new
                    {
                        pno = new DbString { Value = cmd.Pno.Trim(), IsAnsi = true, Length = 50 },
                        gname = Ansi(gname, 60),
                        fpdate = cmd.Date,
                        fpje = cmd.PaySum,
                        jsr = Ansi(per, 10),
                        djno = Ansi(newDocNo, 30),
                        ctype = Ansi(ctype, 10),
                        fkfs = Ansi(cmd.Fkfs, 10),
                        dpm = Ansi(dpm, 10),
                    }, transaction: tx, cancellationToken: ct));
            }

            // 7) 余额刷新（存储过程会逐行回写 tbysyf.lj，属契约写路径）
            var yue = await CallYsyfRefreshAsync(conn, tx, gname, ct);

            await tx.CommitAsync(ct);
            return new XlskSaveResult { DocNo = newDocNo, Yue = yue };
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<decimal> RestoreAsync(string cno, string type, string gname,
        IReadOnlyList<XlskRestoreLine> lines, string dpm, string per, bool finVouch = false, CancellationToken ct = default)
    {
        cno = cno.Trim();
        gname = gname.Trim();
        await using var conn = await factory.OpenAsync(ct);
        // 「没有单据可还原」守卫（c_.row74 L18-23）：tbysyf 中不存在该单号 → 拒绝
        var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM tbysyf WITH (NOLOCK) WHERE cno = @cno",
            new { cno = Ansi(cno, 30) }, cancellationToken: ct));
        if (exists == 0) throw new AppException("XLSK_DOC_NOT_FOUND", "没有单据可还原", 404);
        // finvouch 联动守卫（c_.row74 L36-44）：该单生成的凭证已审核 → 不能删除
        if (finVouch && await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM finvouch WITH (NOLOCK) WHERE payno = @cno AND audit = 1",
                new { cno = Ansi(cno, 30) }, cancellationToken: ct)) > 0)
            throw new AppException("FINVOUCH_AUDITED", $"该{type}生成的凭证已审核,不能删除!", 409);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // 4) 删除汇总行（c_.row74 L47）
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM tbysyf WHERE cno = @cno",
                new { cno = Ansi(cno, 30) }, transaction: tx, cancellationToken: ct));

            // 5) 维修行 colmoney 回退（L50-57，对全部载入维修行，勾选与否都算——旧码 SCAN 无过滤）
            foreach (var line in lines.Where(l => l.LineType == "维修"))
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbxlrc SET colmoney = colmoney - @bcje WHERE gh = @gh",
                    new { bcje = line.Bcje, gh = Ansi(line.Paper, 15) }, transaction: tx, cancellationToken: ct));
            }

            // 6) 逐勾选行回退（L60-95）
            foreach (var line in lines.Where(l => l.Checked))
            {
                var delta = line.LineType is "收款" or "付款" ? Math.Abs(line.Bcje) : line.Bcje;
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbysyf SET skmoney = skmoney - @delta WHERE id = @id",
                    new { delta, line.Id }, transaction: tx, cancellationToken: ct));
                if (line.Wsje == 0)
                {
                    if (line.LineType == "销售")
                        await conn.ExecuteAsync(new CommandDefinition(
                            "UPDATE tbsada SET jzdate = '1900-01-01' WHERE paper = @paper",
                            new { paper = Ansi(line.Paper, 15) }, transaction: tx, cancellationToken: ct));
                    else if (line.LineType == "维修")
                        await conn.ExecuteAsync(new CommandDefinition(
                            "UPDATE tbxlrc SET jzdate = '1900-01-01' WHERE gh = @paper",
                            new { paper = Ansi(line.Paper, 15) }, transaction: tx, cancellationToken: ct));
                    // 按 paper 反查 tblsz 二次冲抵（L84-94，仅结清行分支内执行）
                    var flows = (await conn.QueryAsync<(string skno, decimal ys, decimal yf)>(new CommandDefinition(
                        "SELECT RTRIM(skno), ISNULL(ys,0), ISNULL(yf,0) FROM tblsz WITH (NOLOCK) WHERE paper = @paper",
                        new { paper = Ansi(string.IsNullOrWhiteSpace(line.Paper) ? "id:" + line.Id : line.Paper, 30) },
                        transaction: tx, cancellationToken: ct))).AsList();
                    foreach (var f in flows.Where(f => f.skno == cno))
                    {
                        var je = type == "收款" ? f.ys : f.yf;
                        await conn.ExecuteAsync(new CommandDefinition(
                            "UPDATE tbysyf SET skmoney = skmoney - @je WHERE cno = @skno AND type = @type",
                            new { je, skno = Ansi(f.skno, 10), type = Ansi(type, 8) }, transaction: tx, cancellationToken: ct));
                    }
                }
            }

            // 7) 删除流水（L97）
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM tblsz WHERE skno = @cno",
                new { cno = Ansi(cno, 10) }, transaction: tx, cancellationToken: ct));

            // 7.5) finvouch 联动删除（c_.row74 L115-116）：DELETE finvouch WHERE payno=单号
            if (finVouch)
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM finvouch WHERE payno = @cno",
                    new { cno = Ansi(cno, 30) }, transaction: tx, cancellationToken: ct));

            var yue = await CallYsyfRefreshAsync(conn, tx, gname, ct);
            await tx.CommitAsync(ct);
            return yue;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>下一张/上一张（手.row77/掠.row76）：type 相同、cno 前进/后退第一张。</summary>
    public async Task<string?> GetNextDocNoAsync(string cno, string type, bool forward, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var sql = forward
            ? "SELECT TOP (1) RTRIM(cno) FROM tbysyf WITH (NOLOCK) WHERE type = @type AND cno > @cno ORDER BY cno"
            : "SELECT TOP (1) RTRIM(cno) FROM tbysyf WITH (NOLOCK) WHERE type = @type AND cno < @cno ORDER BY cno DESC";
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(sql,
            new { type = Ansi(type, 8), cno = Ansi(cno.Trim(), 30) }, cancellationToken: ct));
    }

    /// <summary>skinfor p_.row11：业务类型下拉（tbsysset type='业务类型'）。</summary>
    public async Task<IReadOnlyList<string>> GetBizTypesAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT RTRIM(name) FROM tbsysset WITH (NOLOCK) WHERE type = N'业务类型' ORDER BY id",
            cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// 提取未收款（fr_xlsk_tq Init row17/19）。未结条件：ys<>0 and ys-yf<>0 and abs(skmoney)&lt;abs(ys-yf)；
    /// 收款排除 type in(收款,付款)；付款再加 预收,预收款。where=表单 fr_find 追加条件（gname/datime/type/cno/xm）。
    /// </summary>
    public async Task<IReadOnlyList<XlskTqRow>> GetUnsettledAsync(string type, string where, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var isReceipt = type == "收款";
        var excluded = isReceipt ? "N'收款',N'付款'" : "N'收款',N'付款',N'预收',N'预收款'";
        var sign = isReceipt ? "ys" : "yf";
        var extra = (where ?? "").Trim();
        var extraSql = extra.Length > 0 ? " AND " + extra : "";
        var sql = $"""
            SELECT id, RTRIM(type) type, RTRIM(cno) cno, datime, RTRIM(ISNULL(gname,'')) gname, RTRIM(ISNULL(pno,'')) pno,
                   {sign} - skmoney AS amount, skmoney, ISNULL(RTRIM(xm),'') xm,
                   ch = CASE WHEN RTRIM(type) = N'维修' THEN ISNULL((SELECT TOP (1) RTRIM(ch) FROM tbxlrc WITH (NOLOCK) WHERE gh = a.cno), '') ELSE '' END,
                   tgh = CASE WHEN RTRIM(type) = N'维修' THEN ISNULL((SELECT TOP (1) RTRIM(ISNULL(tgh,'')) FROM tbxlrc WITH (NOLOCK) WHERE gh = a.cno), '') ELSE '' END,
                   RTRIM(ISNULL(dpm,'')) dpm
            FROM tbysyf a WITH (NOLOCK)
            WHERE {sign} <> 0 AND {sign} - yf <> 0 AND ABS(skmoney) < ABS({sign} - yf)
              AND type NOT IN ({excluded}){extraSql}
            ORDER BY datime DESC
            """;
        var rows = await conn.QueryAsync<(int id, string type, string cno, DateTime datime, string gname, string pno,
            decimal amount, decimal skmoney, string xm, string ch, string tgh, string dpm)>(
            new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(r => new XlskTqRow
        {
            Id = r.id, Type = r.type, Cno = r.cno, Datime = r.datime, Gname = r.gname, Pno = r.pno,
            Amount = r.amount, Skmoney = r.skmoney, Xm = r.xm, Ch = r.ch ?? "", Tgh = r.tgh ?? "", Dpm = r.dpm ?? "",
        }).ToList();
    }

    /// <summary>
    /// 收款导入分析（fr_skdr __.row34）。剩余应收 = isnull(sum(ys-yf-skmoney),0) WHERE cno+gname；
    /// 校验：drmoney≤剩余且≥0 → 成功；剩余>0 → 金额大于应收金额；否则 工单已经收款；
    /// 按单号+客户查不到 → 按单号查：1 客户 → 此工号收款的客户应该是[xxx]；多客户 → 本工号有N个客户欠款；无 → 找不到对应工号。
    /// </summary>
    public async Task<IReadOnlyList<SkdrRow>> AnalyzeSkdrAsync(IReadOnlyList<(string Cno, string Ch, decimal DrMoney)> rows,
        string gname, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var result = new List<SkdrRow>();
        foreach (var (cnoRaw, ch, drMoney) in rows)
        {
            var cno = cnoRaw.Trim();
            var row = new SkdrRow { Cno = cno, Ch = ch, DrMoney = drMoney };
            var remain = await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                "SELECT ISNULL(SUM(ys - yf - skmoney), 0) FROM tbysyf WITH (NOLOCK) WHERE cno = @cno AND gname = @gname",
                new { cno = Ansi(cno, 30), gname = Ansi(gname, 60) }, cancellationToken: ct)) ?? 0m;
            if (remain != 0m || drMoney == 0m)
            {
                row.Remain = remain;
                row.Status = drMoney <= remain && drMoney >= 0 ? "" : (remain > 0 ? "金额大于应收金额" : "工单已经收款");
            }
            else
            {
                // 单号+客户无行或刚好清零 → 按单号查客户分布（__.row34 L37-53）
                var owners = (await conn.QueryAsync<(string gname, decimal drys)>(new CommandDefinition(
                    "SELECT RTRIM(gname) gname, ISNULL(SUM(ys - yf - skmoney), 0) drys FROM tbysyf WITH (NOLOCK) " +
                    "WHERE cno = @cno GROUP BY gname",
                    new { cno = Ansi(cno, 30) }, cancellationToken: ct))).AsList();
                if (owners.Count == 0)
                    row.Status = "找不到对应工号";
                else if (owners.Count == 1)
                    row.Status = $"此工号收款的客户应该是[{owners[0].gname}],无法导入";
                else
                    row.Status = $"本工号有{owners.Count}个客户欠款是[{string.Join(",", owners.Select(o => o.gname))}]";
            }
            result.Add(row);
        }
        return result;
    }

    /// <summary>客户余额（5_.row71 L23-25：sum(ys-yf)）。</summary>
    public async Task<decimal> GetCustomerYueAsync(string gname, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
            "SELECT ISNULL(SUM(ys - yf), 0) FROM tbysyf WITH (NOLOCK) WHERE gname = @gname",
            new { gname = Ansi(gname, 60) }, cancellationToken: ct)) ?? 0m;
    }

    /// <summary>ysyf_refresh：按 datime 重算该客户全部 tbysyf.lj 并返回余额（契约写路径）。</summary>
    private static async Task<decimal> CallYsyfRefreshAsync(SqlConnection conn, SqlTransaction tx, string gname, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("@gname", gname, System.Data.DbType.AnsiString, size: 50);
        p.Add("@yue", dbType: System.Data.DbType.Decimal, direction: System.Data.ParameterDirection.Output,
            precision: 18, scale: 2);
        await conn.ExecuteAsync(new CommandDefinition("dbo.ysyf_refresh", p,
            commandType: System.Data.CommandType.StoredProcedure, transaction: tx, cancellationToken: ct));
        return p.Get<decimal>("@yue");
    }

    /// <summary>djname 为中文场景：按 tbdjgs.djname 实测（char 列 + GB 排序规则）用 Unicode 参数可匹配，取号服务同款。</summary>
    private static DbString DbStr(string value, int length) => new() { Value = value, IsAnsi = false, Length = length };

    /// <summary>
    /// 入库单随付打印数据（fr_xlsk 墌.row67）：tbistoed WHERE cno IN 勾选入库单 ORDER BY cno,nno；
    /// 沙河丰田部署（P_CLIENT）按 nno 分组写 zu/zuc 人员分派（应用层，非落库——旧码 REPLACE 后 TABLEUPDATE 本地游标）。
    /// </summary>
    public async Task<IReadOnlyList<IstofkRow>> GetIstofkRowsAsync(IReadOnlyList<string> cnos, string client, CancellationToken ct = default)
    {
        var list = cnos.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct().ToList();
        if (list.Count == 0) return [];
        await using var conn = await factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(string Cno, string Nno, string Na1, string Ty, decimal Jkamount,
            decimal Iprc, DateTime Indate, string Gu, string Gyname)>(new CommandDefinition(
            """
            SELECT RTRIM(cno), RTRIM(nno), RTRIM(na1), RTRIM(ty), ISNULL(jkamount,0), ISNULL(iprc,0),
                   indate, RTRIM(ISNULL(gu,'')), RTRIM(ISNULL(gyname,''))
            FROM tbistoed WITH (NOLOCK) WHERE cno IN @cnos ORDER BY cno, nno
            """, new { cnos = list }, cancellationToken: ct))).AsList();
        var result = rows.Select(r => new IstofkRow
        {
            Cno = r.Cno, Nno = r.Nno, Na1 = r.Na1, Ty = r.Ty, Jkamount = r.Jkamount,
            Iprc = r.Iprc, Indate = r.Indate, Gu = r.Gu, Gyname = r.Gyname,
        }).ToList();
        if (client.Trim() == "沙河丰田")
        {
            foreach (var r in result)
            {
                r.Zu = r.Nno switch
                {
                    "3" or "4" or "C" or "F" or "D" or "M" or "N" => 2,
                    "5" or "6" or "G" or "PZ" => 3,
                    "7" or "8" or "A" or "Q" => 4,
                    "PQ" => 6,
                    "1" or "2" or "J" => 1,
                    _ => 5,
                };
                r.Zuc = r.Zu switch
                {
                    1 => "章军忱", 2 => "吴三英", 3 => "符鹏雪",
                    4 => "刘博谨", 5 => "李少波", 6 => "汪展华",
                    _ => "",
                };
            }
        }
        return result;
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? string.Empty, IsAnsi = true, Length = length };

    /// <summary>
    /// 收/付款单明细（fr_skmx Init row2）。表头=tbysyf type+cno 首行（金额=收款 ABS(ys)/付款 yf）；
    /// 勾选行=tblsz.skno 三段 UNION：①回扣行（pno in 回扣/KH，两例试图都非回扣才配对）
    /// ②非回扣行 ③id: 前缀行（tbysyf.id 回查）——单据列口径与还原（Load row2）一致。
    /// </summary>
    public async Task<SkmxDoc?> GetSkMxAsync(string type, string skno, string dpm, CancellationToken ct = default)
    {
        var t = (type ?? "").Trim();
        var no = (skno ?? "").Trim();
        await using var conn = await factory.OpenAsync(ct);
        var head = (await conn.QueryAsync<(string gname, DateTime datime, string cno, string pno, string fkfs, decimal ys, decimal yf)>(
            new CommandDefinition(
                """
                SELECT TOP (1) RTRIM(ISNULL(gname,'')), datime, RTRIM(ISNULL(cno,'')), RTRIM(ISNULL(pno,'')),
                       RTRIM(ISNULL(fkfs,'')), ISNULL(ys,0), ISNULL(yf,0)
                FROM tbysyf WITH (NOLOCK) WHERE rtrim(type) = @type AND rtrim(cno) = @no
                """, new { type = Ansi(t, 4), no = Ansi(no, 30) }, cancellationToken: ct))).FirstOrDefault();
        if (head.gname is null) return null;
        var lines = (await conn.QueryAsync<(string type, string paper, decimal ys, decimal yf, DateTime datime)>(
            new CommandDefinition(
                """
                SELECT RTRIM(ISNULL(y.type,'')), y.cno AS paper, ISNULL(y.ys,0), ISNULL(y.yf,0), y.datime
                FROM tbysyf y WITH (NOLOCK) LEFT JOIN tblsz l WITH (NOLOCK) ON y.cno = l.paper AND y.pno = l.pno AND y.gname = l.gname
                WHERE rtrim(l.skno) = @no AND l.dpm LIKE '%' + @dpm + '%' AND y.pno IN (N'回扣','KH') AND l.pno IN (N'回扣','KH')
                UNION ALL
                SELECT RTRIM(ISNULL(y.type,'')), y.cno, ISNULL(y.ys,0), ISNULL(y.yf,0), y.datime
                FROM tbysyf y WITH (NOLOCK) LEFT JOIN tblsz l WITH (NOLOCK) ON y.cno = l.paper AND y.gname = l.gname
                WHERE rtrim(l.skno) = @no AND l.dpm LIKE '%' + @dpm + '%' AND y.pno NOT IN (N'回扣','KH') AND l.pno NOT IN (N'回扣','KH')
                UNION ALL
                SELECT RTRIM(ISNULL(y.type,'')), 'id:' + CAST(y.id AS varchar(15)), ISNULL(y.ys,0), ISNULL(y.yf,0), y.datime
                FROM tbysyf y WITH (NOLOCK) LEFT JOIN (
                    SELECT skno, STUFF(paper, 1, 3, '') AS id FROM tblsz WITH (NOLOCK)
                    WHERE paper LIKE 'id%' AND dpm LIKE '%' + @dpm + '%') z ON y.id = z.id
                WHERE rtrim(z.skno) = @no
                """, new { no = Ansi(no, 30), dpm = Ansi(dpm, 20) }, cancellationToken: ct))).AsList();
        return new SkmxDoc
        {
            Gname = head.gname, Datime = head.datime, Cno = head.cno, Pno = head.pno, Fkfs = head.fkfs,
            Amount = t == "收款" ? Math.Abs(head.ys) : head.yf,
            Lines = lines.Select(r => new SkmxRow
            {
                Type = r.type, Paper = r.paper, Datime = r.datime, Je = t == "收款" ? r.ys : r.yf,
            }).ToList(),
        };
    }
}

#endregion

#region G.4 应收应付（fr_ysyf）

public sealed class YsyfSummaryRow
{
    public int Id { get; set; }
    public string Gname { get; set; } = "";
    public decimal SYs { get; set; }
    public decimal SYf { get; set; }
    public decimal Lj { get; set; }
    public decimal Cb { get; set; }
    public decimal Tick { get; set; }
    public decimal Yjb { get; set; }
    public string Ywy { get; set; } = "";
    public string Cwfzr { get; set; } = "";
    public decimal Gzye { get; set; }
    public DateTime Mindate { get; set; }
}

public sealed class YsyfDetailRow
{
    public int Id { get; set; }
    public string Gname { get; set; } = "";
    public DateTime Datime { get; set; }
    public string Cno { get; set; } = "";
    public string Xm { get; set; } = "";
    public string Pno { get; set; } = "";
    public decimal Ys { get; set; }
    public decimal Yf { get; set; }
    public decimal Lj { get; set; }
    public string Type { get; set; } = "";
    public string Fkfs { get; set; } = "";
    public decimal Skmoney { get; set; }
    public string Per { get; set; } = "";
    public string Note { get; set; } = "";
    public string Isord { get; set; } = "";
}

public sealed record YsyfCustomerCommand(string Gname, decimal Cb, decimal Tick, decimal Yjb, string? Htno,
    string? Ywy = null, string? Cwfzr = null);
public sealed record YsyfDetailCommand(string Gname, DateTime Datime, string Cno, string Xm, string Pno,
    decimal Ys, decimal Yf, string Type, string Fkfs, string Note);

public interface IYsyfRepository
{
    Task SyncCbTickAsync(CancellationToken ct = default);
    Task<IReadOnlyList<YsyfSummaryRow>> GetSummariesAsync(string gnamePrefix, string option,
        DateTime? dateFrom, DateTime? dateTo, string fkfs, string dpm, CancellationToken ct = default);
    Task<IReadOnlyList<YsyfDetailRow>> GetDetailsAsync(string gname, CancellationToken ct = default);
    Task<int> AddCustomerAsync(YsyfCustomerCommand cmd, bool withHtno, string per, string dpm, CancellationToken ct = default);
    Task UpdateCustomerAsync(string oldName, YsyfCustomerCommand cmd, bool withHtno, string per, CancellationToken ct = default);
    Task DeleteCustomerAsync(string gname, string per, CancellationToken ct = default);
    Task<int> AddDetailAsync(YsyfDetailCommand cmd, string per, string dpm, CancellationToken ct = default);
    Task UpdateDetailAsync(int id, YsyfDetailCommand cmd, CancellationToken ct = default);
    Task DeleteDetailAsync(int id, string gname, bool deleteInvoice, string per, CancellationToken ct = default);
    Task MultiDeleteBeforeAsync(int anchorId, string gname, DateTime today, string per, CancellationToken ct = default);
    Task<IReadOnlyList<YsyfTwelveMonthRow>> GetTwelveMonthAsync(string gname, DateTime periodDate, CancellationToken ct = default);
}

/// <summary>前十二月应收应付行（fr_ysyf_12y C_YSYF：期初 + 12 个月 + 累计）。</summary>
public sealed class YsyfTwelveMonthRow
{
    public string Cmonth { get; set; } = "";
    public decimal Ys { get; set; }
    public decimal Yf { get; set; }
    public decimal Ye { get; set; }
}

/// <summary>
/// 应收应付仓储（fr_ysyf 契约）。
/// Load 同步（row3 L98-108）：tbgu cb/tick 非零客户 → tbysyf 全行覆盖后两侧清零。
/// 汇总（o__.row170）：SUM(ys)/SUM(yf)/MAX(cb)/MAX(yjb)/getgufield(ywy,cwfzr)/gzye=MAX(cb)&gt;0?MAX(cb)-SUM(ys-yf):0。
/// 明细增改删（___.row119 / row4）；删除拦截：收款/付款类型、skmoney≠0 且 ys&gt;0/yf&gt;0；
/// 多条删除（row4 MDELE）：锚点前全删，锚点行改写为汇总行（type='其他'、datime=1900-01-01、xm='做过多记录删除'+日期）。
/// </summary>
public sealed class YsyfRepository(SqlConnectionFactory factory) : IYsyfRepository
{
    public async Task SyncCbTickAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var names = await conn.QueryAsync<string>(new CommandDefinition(
                "SELECT RTRIM(gname) FROM tbgu WITH (NOLOCK) WHERE cb <> 0 OR tick <> 0",
                transaction: tx, cancellationToken: ct));
            foreach (var gname in names)
            {
                // 旧码（row3 L99-108）：tbysyf ← tbgu 的 max(cb)/max(tick)；tbgu 清零；tbysyf 不清零
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE tbysyf SET cb = (SELECT MAX(cb) FROM tbgu WITH (NOLOCK) WHERE gname = @gname),
                                       tick = (SELECT MAX(tick) FROM tbgu WITH (NOLOCK) WHERE gname = @gname)
                    WHERE gname = @gname;
                    UPDATE tbgu SET cb = 0, tick = 0 WHERE gname = @gname;
                    """,
                    new { gname = Ansi(gname, 60) }, transaction: tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>页1 汇总查询（o__.row170 L2-40）：option ∈ 空/应收/应付/余额大于零/供货单位。</summary>
    public async Task<IReadOnlyList<YsyfSummaryRow>> GetSummariesAsync(string gnamePrefix, string option,
        DateTime? dateFrom, DateTime? dateTo, string fkfs, string dpm, CancellationToken ct = default)
    {
        var where = new List<string>();
        var p = new DynamicParameters();
        gnamePrefix = (gnamePrefix ?? "").Trim();
        if (gnamePrefix.Length > 0)
        {
            if (gnamePrefix.StartsWith("/", StringComparison.Ordinal))
            {
                where.Add("gname LIKE @glike");
                p.Add("@glike", $"%{gnamePrefix[1..]}%", System.Data.DbType.AnsiString);
            }
            else
            {
                where.Add("gname >= @gfrom AND gname <= @gto");
                p.Add("@gfrom", gnamePrefix, System.Data.DbType.AnsiString, size: 60);
                p.Add("@gto", gnamePrefix + "组组组组", System.Data.DbType.AnsiString, size: 64);
            }
        }
        if (option == "应收") where.Add("(ys <> 0 OR (ys = 0 AND yf = 0))");
        if (option == "应付") where.Add("(yf <> 0 OR (ys = 0 AND yf = 0))");
        if (option == "余额大于零")
            where.Add("gname IN (SELECT gname FROM tbysyf GROUP BY gname HAVING SUM(ys - yf) > 0) " +
                      "AND gname NOT IN (SELECT DISTINCT gname FROM tbysyf WHERE type = '入库')");
        if (option == "供货单位")
            where.Add("gname IN (SELECT DISTINCT gname FROM tbysyf WHERE type = '入库')");
        if (dateFrom.HasValue && dateTo.HasValue)
        {
            where.Add("gname IN (SELECT DISTINCT gname FROM tbysyf WHERE datime >= @d1 AND datime < @d2)");
            p.Add("@d1", dateFrom.Value, System.Data.DbType.DateTime);
            p.Add("@d2", dateTo.Value, System.Data.DbType.DateTime);
        }
        if (!string.IsNullOrWhiteSpace(fkfs))
        {
            where.Add("gname IN (SELECT DISTINCT gname FROM tbysyf WHERE fkfs = @fkfs)");
            p.Add("@fkfs", Ansi(fkfs.Trim(), 10));
        }
        var sql = $"""
            SELECT id = ROW_NUMBER() OVER (ORDER BY gname), RTRIM(gname) gname,
                   SUM(ys) SYs, SUM(yf) SYf, SUM(ys - yf) lj,
                   cb = ISNULL(MAX(cb), 0), tick = ISNULL(MAX(tick), 0),
                   yjb = ISNULL(MAX(yjb), 0),
                   ywy = ISNULL(dbo.getgufield(gname, @dpm, 'ywy'), ''),
                   gzye = CASE WHEN ISNULL(MAX(cb), 0) = 0 THEN 0 ELSE MAX(cb) - SUM(ys - yf) END,
                   mindate = ISNULL((SELECT MIN(datime) FROM tbysyf WITH (NOLOCK)
                                     WHERE datime > '1900-01-01' AND tick > 0 AND gname = b.gname
                                       AND CHARINDEX(@dpmc, dpm) > 0), GETDATE()),
                   cwfzr = ISNULL(dbo.getgufield(gname, @dpm, 'cwfzr'), '')
            FROM tbysyf b WITH (NOLOCK)
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            GROUP BY gname ORDER BY gname
            """;
        p.Add("@dpm", Ansi(dpm, 10));
        p.Add("@dpmc", Ansi(dpm, 10));
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<YsyfSummaryRow>(new CommandDefinition(sql, p, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<YsyfDetailRow>> GetDetailsAsync(string gname, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<YsyfDetailRow>(new CommandDefinition(
            """
            SELECT id, RTRIM(gname) gname, datime, RTRIM(cno) cno, ISNULL(RTRIM(xm),'') xm, ISNULL(RTRIM(pno),'') pno,
                   ys, yf, lj, RTRIM(type) type, RTRIM(fkfs) fkfs, skmoney, RTRIM(per) per,
                   ISNULL(RTRIM(note),'') note, RTRIM(ISNULL(isord,'')) isord
            FROM tbysyf WITH (NOLOCK) WHERE gname = @gname ORDER BY datime, id
            """,
            new { gname = Ansi(gname.Trim(), 60) }, cancellationToken: ct));
        var list = rows.AsList();
        // LJ 累计列（__.row2 change/mxrefresh：LJ = 前值 - YF + YS），应用层计算
        decimal run = 0;
        foreach (var r in list)
        {
            run += r.Ys - r.Yf;
            r.Lj = run;
        }
        return list;
    }

    /// <summary>
    /// 前十二月应收应付及余额（fr_ysyf_12y Init row2）：M_DATE1=P_RQ 前推 12 个月当月 1 日、
    /// M_DATE2=P_RQ 当月 1 日；期初=datime&lt;M_DATE1 全历史 Σys/Σyf/Σ(ys-yf)；
    /// 各月=月聚合 Σys/Σyf（YE=应用层游标累计 期初+逐月 ys-yf）；末行「累计」。
    /// </summary>
    public async Task<IReadOnlyList<YsyfTwelveMonthRow>> GetTwelveMonthAsync(string gname, DateTime periodDate, CancellationToken ct = default)
    {
        var date1 = new DateTime(periodDate.AddMonths(-12).Year, periodDate.AddMonths(-12).Month, 1);
        var date2 = new DateTime(periodDate.Year, periodDate.Month, 1);
        await using var conn = await factory.OpenAsync(ct);
        var monthly = (await conn.QueryAsync<(string Cmonth, decimal Ys, decimal Yf)>(new CommandDefinition(
            """
            SELECT CONVERT(char(7), datime, 120) cmonth, SUM(ys) ys, SUM(yf) yf
            FROM tbysyf WITH (NOLOCK)
            WHERE gname = @gname AND datime >= @d1 AND datime < @d2
            GROUP BY CONVERT(char(7), datime, 120)
            """,
            new { gname = Ansi(gname.Trim(), 60), d1 = date1, d2 = date2 }, cancellationToken: ct))).AsList();
        var open = await conn.QuerySingleAsync<(decimal Ys, decimal Yf)>(new CommandDefinition(
            "SELECT ISNULL(SUM(ys),0), ISNULL(SUM(yf),0) FROM tbysyf WITH (NOLOCK) WHERE gname = @gname AND datime < @d1",
            new { gname = Ansi(gname.Trim(), 60), d1 = date1 }, cancellationToken: ct));

        var list = new List<YsyfTwelveMonthRow>
        {
            new() { Cmonth = "期初", Ys = open.Ys, Yf = open.Yf, Ye = open.Ys - open.Yf },
        };
        decimal runYs = open.Ys, runYf = open.Yf, runYe = open.Ys - open.Yf;
        for (var i = 0; i < 12; i++)
        {
            // 旧码月份序 = GOMONTH(P_RQ, i-13)（自前推 12 月起递增）
            var m = date1.AddMonths(i);
            var key = m.ToString("yyyy-MM");
            var hit = monthly.FirstOrDefault(x => x.Cmonth == key);
            runYs += hit.Ys; runYf += hit.Yf; runYe += hit.Ys - hit.Yf;
            list.Add(new YsyfTwelveMonthRow { Cmonth = m.ToString("yyyy年MM月"), Ys = hit.Ys, Yf = hit.Yf, Ye = runYe });
        }
        list.Add(new YsyfTwelveMonthRow { Cmonth = "累计", Ys = runYs, Yf = runYf, Ye = runYe });
        return list;
    }

    /// <summary>添加客户空行（婅_.row140 add L52-58；PASSTH 自动补 dpm）。</summary>
    public async Task<int> AddCustomerAsync(YsyfCustomerCommand cmd, bool withHtno, string per, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var dup = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM tbysyf WITH (NOLOCK) WHERE gname = @gname",
                new { gname = Ansi(cmd.Gname, 60) }, transaction: tx, cancellationToken: ct));
            if (dup > 0) throw new AppException("GU_EXISTS", "此客户已存在", 409);
            var sql = withHtno
                ? "INSERT INTO tbysyf (gname, cb, tick, yjb, htno, dpm) VALUES (@gname, @cb, @tick, @yjb, @htno, @dpm); SELECT CAST(SCOPE_IDENTITY() AS int);"
                : "INSERT INTO tbysyf (gname, cb, tick, yjb, dpm) VALUES (@gname, @cb, @tick, @yjb, @dpm); SELECT CAST(SCOPE_IDENTITY() AS int);";
            var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql,
                new
                {
                    gname = Ansi(cmd.Gname, 60), cb = cmd.Cb, tick = (int)cmd.Tick, yjb = cmd.Yjb,
                    htno = new DbString { Value = cmd.Htno ?? "", IsAnsi = true, Length = 50 },
                    dpm = Ansi(dpm, 10),
                }, transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return id;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>修改客户（婅_.row140 edit L61-74）：按旧名全行改名 + tbgu ywy/cwfzr + tbxlje.zfr 改名。</summary>
    public async Task UpdateCustomerAsync(string oldName, YsyfCustomerCommand cmd, bool withHtno, string per, CancellationToken ct = default)
    {
        oldName = oldName.Trim();
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var sql = withHtno
                ? "UPDATE tbysyf SET gname = @gname, cb = @cb, tick = @tick, yjb = @yjb, htno = @htno WHERE gname = @old"
                : "UPDATE tbysyf SET gname = @gname, cb = @cb, tick = @tick, yjb = @yjb WHERE gname = @old";
            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                gname = Ansi(cmd.Gname, 60), cb = cmd.Cb, tick = (int)cmd.Tick, yjb = cmd.Yjb,
                htno = new DbString { Value = cmd.Htno ?? "", IsAnsi = true, Length = 50 },
                old = Ansi(oldName, 60),
            }, transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tbgu SET ywy = @ywy, cwfzr = @cwfzr WHERE gname = @gname",
                new
                {
                    ywy = new DbString { Value = cmd.Ywy ?? "", IsAnsi = false, Length = 20 },
                    cwfzr = new DbString { Value = cmd.Cwfzr ?? "", IsAnsi = false, Length = 20 },
                    gname = Ansi(cmd.Gname, 60),
                }, transaction: tx, cancellationToken: ct));
            if (!string.Equals(oldName, cmd.Gname, StringComparison.Ordinal))
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tbxlje SET zfr = @new WHERE zfr = @old AND ISNULL(zfr,'') <> ''",
                    new { @new = Ansi(cmd.Gname, 10), old = Ansi(oldName, 60) },
                    transaction: tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>删除客户（row4 DELEKH L30-40）：余额为 0 才可删，DELETE tbysyf WHERE gname。</summary>
    public async Task DeleteCustomerAsync(string gname, string per, CancellationToken ct = default)
    {
        gname = gname.Trim();
        await using var conn = await factory.OpenAsync(ct);
        var lj = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
            "SELECT ISNULL(SUM(ys - yf), 0) FROM tbysyf WITH (NOLOCK) WHERE gname = @gname",
            new { gname = Ansi(gname, 60) }, cancellationToken: ct));
        if (lj != 0) throw new AppException("YSYF_LJ_NOT_ZERO", "余额不为0,不能删除", 409);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tbysyf WHERE gname = @gname", new { gname = Ansi(gname, 60) }, cancellationToken: ct));
    }

    /// <summary>添加明细行（___.row119 add L36-38；保留字校验由 UI 层做（旧为表单校验）；PASSTH 自动补 dpm）。</summary>
    public async Task<int> AddDetailAsync(YsyfDetailCommand cmd, string per, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO tbysyf (datime, cno, gname, xm, pno, ys, yf, type, fkfs, per, note, dpm)
            VALUES (@datime, @cno, @gname, @xm, @pno, @ys, @yf, @type, @fkfs, @per, @note, @dpm);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new
            {
                datime = cmd.Datime,
                cno = Ansi(cmd.Cno, 30),
                gname = Ansi(cmd.Gname, 60),
                xm = new DbString { Value = cmd.Xm ?? "", IsAnsi = false, Length = 250 },
                pno = new DbString { Value = cmd.Pno ?? "", IsAnsi = true, Length = 50 },
                ys = cmd.Ys, yf = cmd.Yf,
                type = Ansi(cmd.Type, 8),
                fkfs = Ansi(cmd.Fkfs, 10),
                per = Ansi(per, 10),
                note = new DbString { Value = cmd.Note ?? "", IsAnsi = false, Length = 250 },
                dpm = Ansi(dpm, 10),
            }, cancellationToken: ct));
    }

    /// <summary>修改明细行（___.row119 edit L39）：per 保留原值。</summary>
    public async Task UpdateDetailAsync(int id, YsyfDetailCommand cmd, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tbysyf SET datime = @datime, cno = @cno, xm = @xm, pno = @pno, ys = @ys, yf = @yf, type = @type, fkfs = @fkfs, note = @note WHERE id = @id",
            new
            {
                id, datime = cmd.Datime, cno = Ansi(cmd.Cno, 30),
                xm = new DbString { Value = cmd.Xm ?? "", IsAnsi = false, Length = 250 },
                pno = new DbString { Value = cmd.Pno ?? "", IsAnsi = true, Length = 50 },
                ys = cmd.Ys, yf = cmd.Yf, type = Ansi(cmd.Type, 8), fkfs = Ansi(cmd.Fkfs, 10),
                note = new DbString { Value = cmd.Note ?? "", IsAnsi = false, Length = 250 },
            }, cancellationToken: ct));
    }

    /// <summary>删除明细行（row4 DELEMX L157-190）；tbfpgl 联动删除按契约条件。</summary>
    public async Task DeleteDetailAsync(int id, string gname, bool deleteInvoice, string per, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var row = (await conn.QueryAsync<(string type, decimal skmoney, decimal ys, decimal yf, string cno, string pno)>(
                new CommandDefinition(
                    "SELECT RTRIM(type), skmoney, ys, yf, RTRIM(cno), ISNULL(RTRIM(pno),'') FROM tbysyf WITH (NOLOCK) WHERE id = @id",
                    new { id }, transaction: tx, cancellationToken: ct))).FirstOrDefault();
            if (row.type is null) throw new AppException("YSYF_ROW_GONE", "明细行不存在", 404);
            if (row.type is "收款" or "付款")
                throw new AppException("YSYF_DEL_BLOCKED", $"请用{row.type}管理还原。", 409);
            if (row.skmoney != 0 && (row.ys > 0 || row.yf > 0))
                throw new AppException("YSYF_HAS_RECEIPT", "该项已有收款记录，请先还原收款单再删除。", 409);
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM tbysyf WHERE id = @id", new { id }, transaction: tx, cancellationToken: ct));
            if (deleteInvoice && row.pno.Length > 0)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM tbfpgl WHERE djno = @djno AND ctype = @ctype AND gname = @gname AND pno = @pno AND fpje = @fpje",
                    new
                    {
                        djno = Ansi(row.cno, 30), ctype = Ansi(row.type, 10),
                        gname = Ansi(gname, 60), pno = new DbString { Value = row.pno, IsAnsi = true, Length = 50 },
                        fpje = row.ys + row.yf,
                    }, transaction: tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>多条删除（row4 MDELE L196-250）：按游标序（datime,id）删锚点前全部行，锚点行改写为汇总行。</summary>
    public async Task MultiDeleteBeforeAsync(int anchorId, string gname, DateTime today, string per, CancellationToken ct = default)
    {
        gname = gname.Trim();
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var anchor = (await conn.QueryAsync<(DateTime datime, decimal ys, decimal yf)>(new CommandDefinition(
                "SELECT datime, ys, yf FROM tbysyf WITH (NOLOCK) WHERE id = @id",
                new { id = anchorId }, transaction: tx, cancellationToken: ct))).FirstOrDefault();
            // M_LJ = 锚点前一行的累计余额（游标序：datime,id）
            var prevLj = await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                "SELECT TOP (1) lj FROM tbysyf WITH (NOLOCK) WHERE gname = @gname AND (datime < @d OR (datime = @d AND id < @id)) " +
                "ORDER BY datime DESC, id DESC",
                new { gname = Ansi(gname, 60), d = anchor.datime, id = anchorId }, transaction: tx, cancellationToken: ct));
            var lj = prevLj ?? 0m;
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM tbysyf WHERE gname = @gname AND (datime < @d OR (datime = @d AND id < @id))",
                new { gname = Ansi(gname, 60), d = anchor.datime, id = anchorId }, transaction: tx, cancellationToken: ct));
            // 锚点行改写为汇总行（旧码 REPLACE + PASSTH update，最终态一致；xm 含锚点原日期）
            var ys = lj > 0 ? anchor.ys + lj : anchor.ys;
            var yf = lj < 0 ? anchor.yf - lj : anchor.yf;
            var skmoney = ys > yf ? ys : yf;
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE tbysyf SET type = N'其他', cno = @cno, xm = @xm, ys = @ys, yf = @yf, skmoney = @skmoney,
                                    datime = '1900-01-01', per = @per
                WHERE id = @id
                """,
                new
                {
                    id = anchorId, cno = Ansi(today.ToString("yyyy-MM-dd"), 30),
                    xm = new DbString { Value = "做过多记录删除" + anchor.datime.ToString("yyyy-MM-dd"), IsAnsi = false, Length = 250 },
                    ys, yf, skmoney, per = Ansi(per, 10),
                }, transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? string.Empty, IsAnsi = true, Length = length };
}

#endregion

#region G.5 收支流水帐（fr_lsz）

public sealed class LszRow
{
    public int Id { get; set; }                 // 哨兵行=0
    public string LsDate { get; set; } = "";    // 显示口径：明细=日期、期初金额/本期小计/期末小计/总计
    public string Guno { get; set; } = "";
    public string Ytu { get; set; } = "";
    public decimal Ys { get; set; }
    public decimal Yf { get; set; }
    public decimal Lj { get; set; }
    public string Remark { get; set; } = "";
    public string Skno { get; set; } = "";
    public string Paper { get; set; } = "";
    public string Gname { get; set; } = "";
    public string Bankname { get; set; } = "";
    public string Note { get; set; } = "";
    public bool IsSentinel { get; set; }
}

public sealed record LszManualCommand(DateTime LsDate, string Skno, string Ytu, decimal Ys, decimal Yf,
    string Remark, string Gname, string Note, string Guno);

public interface ILszRepository
{
    Task<IReadOnlyList<string>> GetBankNamesAsync(CancellationToken ct = default);
    Task<LszRow?> GetRowAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<LszRow>> GetSheetAsync(string? bankName, DateTime begin, DateTime end, CancellationToken ct = default);
    Task<bool> CanDeleteAsync(int id, CancellationToken ct = default);
    Task<int> AddManualAsync(LszManualCommand cmd, string bankName, string dpm, CancellationToken ct = default);
    Task UpdateManualAsync(int id, LszManualCommand cmd, string bankName, string dpm, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task DeleteBeforeAsync(int anchorId, string bankName, decimal anchorLj, CancellationToken ct = default);
    Task AddInitialAsync(string bankName, decimal amount, string dpm, CancellationToken ct = default);

    /// <summary>提取候选（fr_lsz 谛.row139）：销售/入库二选一，未生成过 tblsz 流水的单。</summary>
    Task<IReadOnlyList<LszTiquRow>> GetTiquAsync(string kind, DateTime dateFrom, DateTime dateTo, CancellationToken ct = default);

    /// <summary>提取确认（fr_lsz 肱.row133）：勾选候选逐条 INSERT tblsz（销售→ys/ytu=销售收入，入库→yf/ytu=入库金额）。</summary>
    Task<int> AddTiquAsync(string kind, IReadOnlyList<LszTiquRow> rows, string bankName, string dpm, CancellationToken ct = default);
}

/// <summary>
/// 收支流水帐仓储（fr_lsz 契约）。
/// 报表（setsource）：期初金额/本期小计/总计为应用层聚合哨兵行，明细按 ls_date,id；显示 LJ 顺序累计。
/// 手工行（_r.row65）：INSERT/UPDATE tblsz(ls_date,skno,ytu,ys,yf,remark,gname,note,bankname,guno,dpm)。
/// 删除（7_.row32）：初始累计数（ls_date/skno/paper 全空）拒绝；SK/FK 收付款流水拒绝；其余 DELETE by id。
/// 删除之前全部（鐷.row36）：按规则删锚点前可删行 → 清同 bankname 其他「初始累计数」→ UPDATE 锚点 lj。
/// 初始累计数（Uz.row69）：INSERT tblsz(bankname, ys|yf, remark='初始累计数')。
/// </summary>
public sealed class LszRepository(SqlConnectionFactory factory) : ILszRepository
{
    public async Task<IReadOnlyList<string>> GetBankNamesAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT RTRIM(name) FROM tbsysset WITH (NOLOCK) WHERE type = 'bankname' ORDER BY name DESC",
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<LszRow?> GetRowAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<LszRow>(new CommandDefinition(
            """
            SELECT id, CONVERT(varchar(10), ls_date, 120) AS LsDate, RTRIM(ISNULL(guno,'')) guno, RTRIM(ISNULL(ytu,'')) ytu,
                   ISNULL(ys,0) ys, ISNULL(yf,0) yf, ISNULL(lj,0) lj, RTRIM(ISNULL(remark,'')) remark,
                   RTRIM(ISNULL(skno,'')) skno, RTRIM(ISNULL(paper,'')) paper, RTRIM(ISNULL(gname,'')) gname,
                   RTRIM(ISNULL(bankname,'')) bankname, ISNULL(RTRIM(note),'') note
            FROM tblsz WITH (NOLOCK) WHERE id = @id
            """,
            new { id }, cancellationToken: ct))).FirstOrDefault();
    }

    /// <summary>报表拼装（setsource，row3 L28-92）：期初 + 明细 + 小计 + 期末小计 + 总计，LJ 顺序累计。</summary>
    public async Task<IReadOnlyList<LszRow>> GetSheetAsync(string? bankName, DateTime begin, DateTime end, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var hasBank = !string.IsNullOrWhiteSpace(bankName);
        var rows = new List<LszRow>();
        if (!hasBank)
            return rows;
        var bank = bankName!.Trim();

        var init = (await conn.QueryAsync<(decimal ys, decimal yf)>(new CommandDefinition(
            "SELECT ISNULL(SUM(ys),0), ISNULL(SUM(yf),0) FROM tblsz WITH (NOLOCK) WHERE bankname = @bank AND ls_date < @begin",
            new { bank = Ansi(bank, 40), begin }, cancellationToken: ct))).FirstOrDefault();
        var details = (await conn.QueryAsync<LszRow>(new CommandDefinition(
            """
            SELECT id, CONVERT(varchar(10), ls_date, 120) AS LsDate, RTRIM(ISNULL(guno,'')) guno, RTRIM(ISNULL(ytu,'')) ytu,
                   ISNULL(ys,0) ys, ISNULL(yf,0) yf, ISNULL(lj,0) lj, RTRIM(ISNULL(remark,'')) remark,
                   RTRIM(ISNULL(skno,'')) skno, RTRIM(ISNULL(paper,'')) paper, RTRIM(ISNULL(gname,'')) gname,
                   RTRIM(ISNULL(bankname,'')) bankname, ISNULL(RTRIM(note),'') note
            FROM tblsz WITH (NOLOCK) WHERE bankname = @bank AND ls_date >= @begin AND ls_date <= @end
            ORDER BY ls_date, id
            """,
            new { bank = Ansi(bank, 40), begin, end }, cancellationToken: ct))).AsList();
        var subtotal = (await conn.QueryAsync<(decimal ys, decimal yf)>(new CommandDefinition(
            "SELECT ISNULL(SUM(ys),0), ISNULL(SUM(yf),0) FROM tblsz WITH (NOLOCK) WHERE bankname = @bank AND ls_date >= @begin AND ls_date <= @end",
            new { bank = Ansi(bank, 40), begin, end }, cancellationToken: ct))).FirstOrDefault();
        var total = (await conn.QueryAsync<(decimal ys, decimal yf)>(new CommandDefinition(
            "SELECT ISNULL(SUM(ys),0), ISNULL(SUM(yf),0) FROM tblsz WITH (NOLOCK) WHERE bankname = @bank",
            new { bank = Ansi(bank, 40) }, cancellationToken: ct))).FirstOrDefault();

        rows.Add(new LszRow { Id = 0, LsDate = ".期初金额", Ys = init.ys, Yf = init.yf, Lj = init.ys - init.yf, Bankname = bank, IsSentinel = true });
        foreach (var d in details)
            rows.Add(d);
        rows.Add(new LszRow { Id = 0, LsDate = "本期小计", Ys = subtotal.ys, Yf = subtotal.yf, Bankname = bank, IsSentinel = true });
        rows.Add(new LszRow { Id = 0, LsDate = "期末小计", Bankname = bank, IsSentinel = true });
        rows.Add(new LszRow { Id = 0, LsDate = "总计", Ys = total.ys, Yf = total.yf, Lj = total.ys - total.yf, Bankname = bank, IsSentinel = true });

        // RESUM（__.row2）：LJ 顺序累计（前值 - YF + YS）
        decimal run = 0;
        foreach (var r in rows)
        {
            run += r.Ys - r.Yf;
            r.Lj = run;
        }
        return rows;
    }

    /// <summary>删除规则（7_.row32 L9-24）：初始累计数拒绝；SK/FK 收付款流水拒绝。</summary>
    public async Task<bool> CanDeleteAsync(int id, CancellationToken ct = default)
    {
        var row = await GetRowAsync(id, ct);
        if (row is null) return false;
        if (row.LsDate.Length == 0 && row.Skno.Length == 0 && row.Paper.Length == 0)
            return false;   // 初始累计数
        if (row.Paper.Length == 0)
            return true;
        if (row.Bankname.Length > 0 && !row.Skno.Contains("SK", StringComparison.OrdinalIgnoreCase)
                                    && !row.Skno.Contains("FK", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public async Task<int> AddManualAsync(LszManualCommand cmd, string bankName, string dpm, CancellationToken ct = default)
    {
        if (cmd.LsDate == default) throw new AppException("LSZ_DATE_REQUIRED", "请输入日期", 422);
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO tblsz (ls_date, skno, ytu, ys, yf, remark, gname, note, bankname, guno, dpm)
            OUTPUT inserted.id
            VALUES (@lsDate, @skno, @ytu, @ys, @yf, @remark, @gname, @note, @bankname, @guno, @dpm)
            """,
            new
            {
                lsDate = cmd.LsDate,
                skno = Ansi(cmd.Skno, 10),
                ytu = Ansi(cmd.Ytu, 20),
                ys = cmd.Ys, yf = cmd.Yf,
                remark = Ansi(cmd.Remark, 50),
                gname = Ansi(cmd.Gname, 60),
                note = new DbString { Value = cmd.Note ?? "", IsAnsi = false, Length = 200 },
                bankname = Ansi(bankName, 40),
                guno = Ansi(cmd.Guno, 10),
                dpm = Ansi(dpm, 10),
            }, cancellationToken: ct));
    }

    public async Task UpdateManualAsync(int id, LszManualCommand cmd, string bankName, string dpm, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tblsz SET ls_date = @lsDate, skno = @skno, ytu = @ytu, ys = @ys, yf = @yf, remark = @remark, " +
            "gname = @gname, note = @note, bankname = @bankname, guno = @guno, dpm = @dpm WHERE id = @id",
            new
            {
                id, lsDate = cmd.LsDate, skno = Ansi(cmd.Skno, 10), ytu = Ansi(cmd.Ytu, 20),
                ys = cmd.Ys, yf = cmd.Yf, remark = Ansi(cmd.Remark, 50), gname = Ansi(cmd.Gname, 60),
                note = new DbString { Value = cmd.Note ?? "", IsAnsi = false, Length = 200 },
                bankname = Ansi(bankName, 40), guno = Ansi(cmd.Guno, 10), dpm = Ansi(dpm, 10),
            }, cancellationToken: ct));
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        if (!await CanDeleteAsync(id, ct))
            throw new AppException("LSZ_DEL_BLOCKED", "不能删除此流水帐记录", 409);
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tblsz WHERE id = @id", new { id }, cancellationToken: ct));
    }

    /// <summary>删除锚点之前全部可删行（鐷.row36），再清同 bankname 其他初始累计数，锚点 lj 落库。</summary>
    public async Task DeleteBeforeAsync(int anchorId, string bankName, decimal anchorLj, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var anchor = (await conn.QueryAsync<(DateTime lsDate, int id)>(new CommandDefinition(
                "SELECT ls_date, id FROM tblsz WITH (NOLOCK) WHERE id = @id",
                new { id = anchorId }, transaction: tx, cancellationToken: ct))).FirstOrDefault();
            var deletable = (await conn.QueryAsync<int>(new CommandDefinition(
                """
                SELECT id FROM tblsz WITH (NOLOCK)
                WHERE (ls_date < @ls OR (ls_date = @ls AND id < @id))
                  AND ((ISNULL(paper,'') = '') OR (ISNULL(bankname,'') <> '' AND CHARINDEX('SK', UPPER(skno)) = 0 AND CHARINDEX('FK', UPPER(skno)) = 0))
                """,
                new { ls = anchor.lsDate, id = anchorId }, transaction: tx, cancellationToken: ct))).AsList();
            foreach (var id in deletable)
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM tblsz WHERE id = @id", new { id }, transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM tblsz WHERE remark = N'初始累计数' AND bankname = @bank AND id <> @id",
                new { bank = Ansi(bankName, 40), id = anchorId }, transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tblsz SET lj = @lj WHERE id = @id",
                new { lj = anchorLj, id = anchorId }, transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>初始累计数（Uz.row69）：值&gt;0 记 ys，否则记 yf；ls_date 取默认 1900-01-01（旧 insert 不含该列）。</summary>
    public async Task AddInitialAsync(string bankName, decimal amount, string dpm, CancellationToken ct = default)
    {
        bankName = bankName.Trim();
        if (bankName.Length == 0) throw new AppException("LSZ_BANK_REQUIRED", "请先选择收款去向", 422);
        await using var conn = await factory.OpenAsync(ct);
        if (amount > 0)
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO tblsz (bankname, ys, remark, dpm) VALUES (@bank, @ys, N'初始累计数', @dpm)",
                new { bank = Ansi(bankName, 40), ys = amount, dpm = Ansi(dpm, 10) }, cancellationToken: ct));
        else
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO tblsz (bankname, yf, remark, dpm) VALUES (@bank, @yf, N'初始累计数', @dpm)",
                new { bank = Ansi(bankName, 40), yf = amount, dpm = Ansi(dpm, 10) }, cancellationToken: ct));
    }

    /// <summary>
    /// 流水提取候选（fr_lsz 谛.row139）。销售：tbsada type in(销售,历史) guno 排除汇款/托收/挂帐，
    /// paper 不在 tblsz；入库：tbistoed type in(入库,历史) fkfs<>挂帐，cno 不在 tblsz，
    /// 单价 =「外币为含税价」valuel=1 ? iprj : iprc。
    /// </summary>
    public async Task<IReadOnlyList<LszTiquRow>> GetTiquAsync(string kind, DateTime dateFrom, DateTime dateTo, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        if (kind == "销售")
        {
            var sql = """
                SELECT outdate, RTRIM(paper) paper,
                       SUM(oprct * ckamount * (100 - yhl) / 100.0) AS jine,
                       RTRIM(ISNULL(gname,'')) gname, RTRIM(ISNULL(guno,'')) guno
                FROM tbsada WITH (NOLOCK)
                WHERE outdate >= @d1 AND outdate <= @d2 AND type IN (N'销售', N'历史')
                  AND guno <> N'汇款' AND guno <> N'托收' AND guno <> N'挂帐'
                  AND paper NOT IN (SELECT paper FROM tblsz WITH (NOLOCK))
                GROUP BY outdate, paper, gname, guno
                ORDER BY outdate, paper, gname
                """;
            var rows = await conn.QueryAsync<(DateTime outdate, string paper, decimal jine, string gname, string guno)>(
                new CommandDefinition(sql, new { d1 = dateFrom.Date, d2 = dateTo.Date }, cancellationToken: ct));
            return rows.Select(r => new LszTiquRow { Outdate = r.outdate, Paper = r.paper, Jine = r.jine, Gname = r.gname, Guno = r.guno }).ToList();
        }
        var iprcCol = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT ISNULL(valuel, 0) = 1 FROM tbsysset WITH (NOLOCK) WHERE name = N'外币为含税价' AND type = 'system'",
            cancellationToken: ct)) ? "iprj" : "iprc";
        var sqlIn = $"""
            SELECT indate AS outdate, RTRIM(cno) AS paper,
                   ROUND(SUM({iprcCol} * jkamount), 2) AS jine,
                   RTRIM(ISNULL(gyname,'')) AS gname, RTRIM(ISNULL(fkfs,'')) AS guno
            FROM tbistoed WITH (NOLOCK)
            WHERE indate >= @d1 AND indate <= @d2 AND type IN (N'入库', N'历史')
              AND fkfs <> N'挂帐'
              AND cno NOT IN (SELECT paper FROM tblsz WITH (NOLOCK))
            GROUP BY indate, cno, gyname, fkfs
            ORDER BY indate, cno, gyname
            """;
        var rowsIn = await conn.QueryAsync<(DateTime outdate, string paper, decimal jine, string gname, string guno)>(
            new CommandDefinition(sqlIn, new { d1 = dateFrom.Date, d2 = dateTo.Date }, cancellationToken: ct));
        return rowsIn.Select(r => new LszTiquRow { Outdate = r.outdate, Paper = r.paper, Jine = r.jine, Gname = r.gname, Guno = r.guno }).ToList();
    }

    /// <summary>
    /// 提取确认（fr_lsz 肱.row133）。销售：ys=JINE、ytu='销售收入'、remark='销售：'+paper；
    /// 入库：yf=JINE、ytu='入库金额'、remark='入库：'+paper；ls_date=OUTDATE、skno=paper、guno=候选 guno。
    /// 旧逐条自动提交；新侧单事务（§0A.4 备案口径，行为更严）。
    /// </summary>
    public async Task<int> AddTiquAsync(string kind, IReadOnlyList<LszTiquRow> rows, string bankName, string dpm, CancellationToken ct = default)
    {
        bankName = bankName.Trim();
        if (bankName.Length == 0) throw new AppException("LSZ_BANK_REQUIRED", "请先选择收款去向", 422);
        if (rows.Count == 0) return 0;
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var isSale = kind == "销售";
            var ytu = isSale ? "销售收入" : "入库金额";
            foreach (var r in rows)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO tblsz (ls_date, gname, ys, yf, bankname, skno, guno, remark, paper, ytu, dpm)
                    VALUES (@lsDate, @gname, @ys, @yf, @bank, @skno, @guno, @remark, @paper, @ytu, @dpm)
                    """,
                    new
                    {
                        lsDate = r.Outdate.Date,
                        gname = Ansi(r.Gname, 60),
                        ys = isSale ? r.Jine : 0m,
                        yf = isSale ? 0m : r.Jine,
                        bank = Ansi(bankName, 40),
                        skno = Ansi(r.Paper, 30),
                        guno = Ansi(r.Guno, 10),
                        remark = Ansi($"{(isSale ? "销售" : "入库")}：{r.Paper}", 50),
                        paper = Ansi(r.Paper, 30),
                        ytu = Ansi(ytu, 20),
                        dpm = Ansi(dpm, 10),
                    }, transaction: tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
            return rows.Count;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value ?? string.Empty, IsAnsi = true, Length = length };
}

#endregion
