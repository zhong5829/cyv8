using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Domain.Common;
using QPGL.Domain.Printing;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Printing;

public interface IPrintRepository
{
    /// <summary>票样取数。type=单据类型（旧 fr_sale __.row46 L30 `type=THISFORMSET.FLAG`：销售/报价/预售同票样 re_sada）。</summary>
    Task<SalePrintDoc> GetSalePrintDataAsync(string paper, string type = "销售", CancellationToken ct = default);
}

/// <summary>
/// 票样取数（pg_dosprint pg_sa1 复刻，结构零改动，显式列禁 SELECT *）。
/// 合计/优惠（yhl INT 取整）/大写统一 API 端计算（§8.3.2-3）。
/// 优惠语义（pg_sa1 OTHERWISE 分支）：优惠额 = INT(Σ金额 × yhl/100)；折后 = Σ金额 - 优惠额。
/// </summary>
public sealed class PrintRepository(SqlConnectionFactory factory) : IPrintRepository
{
    public async Task<SalePrintDoc> GetSalePrintDataAsync(string paper, string type = "销售", CancellationToken ct = default)
    {
        var trimmed = paper.Trim();
        var typeTrim = (type ?? "销售").Trim();
        if (typeTrim.Length == 0) typeTrim = "销售";
        var typeArg = new DbString { Value = typeTrim, IsAnsi = false, Length = 10 };
        await using var conn = await factory.OpenAsync(ct);

        var lines = (await conn.QueryAsync<LineRow>(new CommandDefinition(
            """
            SELECT id, RTRIM(nno) AS nno, RTRIM(na1) AS na1, RTRIM(ty) AS ty,
                   RTRIM(unit) AS unit, ckamount, oprc, RTRIM(posi) AS posi
            FROM tbsada WITH (NOLOCK)
            WHERE paper = @paper AND type = @type AND ckamount <> 0
            ORDER BY id
            """,
            new { paper = new DbString { Value = trimmed, IsAnsi = true, Length = 10 }, type = typeArg },
            cancellationToken: ct))).AsList();

        if (lines.Count == 0)
            throw new AppException("PRINT_DOC_EMPTY", $"单据 {trimmed} 不存在或无可打印明细行", 422);

        var head = await conn.QueryFirstOrDefaultAsync<HeadRow>(new CommandDefinition(
            """
            SELECT TOP 1
                   RTRIM(s.gname) AS gname, s.outdate, s.yhl, RTRIM(s.guno) AS guno,
                   RTRIM(s.per) AS per, RTRIM(s.pno) AS pno,
                   ISNULL(h.spname, '') AS spname,
                   g.checkno, g.adr, g.bank, g.tel, g.type1,
                   ISNULL((SELECT MAX(CASE WHEN s2.remark LIKE '%\[已作废]' ESCAPE '\' THEN '' ELSE RTRIM(s2.remark) END)
                           FROM tbsada s2 WITH (NOLOCK)
                           WHERE s2.paper = s.paper AND s2.type = s.type), '') AS remark
            FROM tbsada s WITH (NOLOCK)
            LEFT JOIN tbsada_header h WITH (NOLOCK) ON h.paper = s.paper AND h.dpm = s.dpm
            LEFT JOIN tbgu g WITH (NOLOCK) ON RTRIM(g.gname) = RTRIM(s.gname)
            WHERE s.paper = @paper AND s.type = @type
            ORDER BY s.id
            """,
            new { paper = new DbString { Value = trimmed, IsAnsi = true, Length = 10 }, type = typeArg },
            cancellationToken: ct));

        if (head is null)
            throw new AppException("PRINT_DOC_EMPTY", $"单据 {trimmed} 单头缺失", 422);

        // 公司信息（票头）与票尾（valuec1 长段优先，回退 valuec；与打印设置逻辑一致）
        var sys = await conn.QueryAsync<SettingRow>(new CommandDefinition(
            """
            SELECT RTRIM(name) AS name, ISNULL(valuec, '') AS valuec, ISNULL(valuec1, '') AS valuec1
            FROM tbsysset WITH (NOLOCK)
            WHERE RTRIM(type) = 'system'
               OR RTRIM(name) IN ('RPSET1', 'RPSET2')
            """,
            cancellationToken: ct));
        // name 可重复（如 papertype 门店/仓库两行），取每名首行（pg_dosprint LOCATE 语义）
        var map = sys.GroupBy(x => x.name.Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var footer = LongOrShort(Get(map, "RPSET1")) + LongOrShort(Get(map, "RPSET2"));
        var totalQty = lines.Sum(l => l.ckamount);
        var totalAmount = lines.Sum(l => l.ckamount * l.oprc);
        var yhl = head.yhl;
        var discount = yhl != 0 ? Math.Floor(totalAmount * yhl / 100) : 0;   // 旧 INT 取整
        var finalAmount = totalAmount - discount;
        var isRetail = head.pno.Trim().Equals("N", StringComparison.OrdinalIgnoreCase);

        return new SalePrintDoc
        {
            CompanyName = Get(map, "公司名称").valuec.Trim(),
            Tel = Get(map, "电话").valuec.Trim(),
            Fax = Get(map, "传真").valuec.Trim(),
            Address = Get(map, "地址").valuec.Trim(),
            Bank = Get(map, "开户银行").valuec.Trim(),
            Account = Get(map, "帐号").valuec.Trim(),
            TaxNo = Get(map, "税号").valuec.Trim(),
            DocMaker = head.per,
            Paper = trimmed,
            PaperSuffix = trimmed,   // 票面单号显示完整单号（如 XS26044612；旧票样 RIGHT 6 位截断按用户要求取消）
            OutDate = head.outdate,
            PrintTime = DateTime.Now,
            CustomerName = head.spname.Length > 0 ? head.spname : head.gname,
            CustomerNo = head.checkno?.Trim() ?? "",
            CustomerTaxNo = head.type1?.Trim() ?? "",
            CustomerTel = head.tel?.Trim() ?? "",
            CustomerAddress = head.adr?.Trim() ?? "",
            CustomerBank = head.bank?.Trim() ?? "",
            PayMode = head.guno,
            Operator = head.per,
            IsRetail = isRetail,
            PrintCompanyBank = head.pno.Trim().Equals("Y", StringComparison.OrdinalIgnoreCase),
            Lines = lines.Select((l, i) => new SalePrintLine
            {
                Seq = i + 1,
                Nno = l.nno,
                Cp = l.nno,
                Na1 = l.na1,
                Ty = l.ty,
                Unit = l.unit,
                Qty = l.ckamount,
                Price = l.oprc,
                Amount = l.ckamount * l.oprc,
                Posi = l.posi,
            }).ToList(),
            TotalQty = totalQty,
            TotalAmount = totalAmount,
            Discount = discount,
            FinalAmount = finalAmount,
            AmountUpper = MoneyUpper.ToUpper(finalAmount),
            Footer = footer,
            Remark = head.remark.Trim(),
        };
    }

    private sealed class LineRow
    {
        public int id { get; set; }
        public string nno { get; set; } = "";
        public string na1 { get; set; } = "";
        public string ty { get; set; } = "";
        public string unit { get; set; } = "";
        public decimal ckamount { get; set; }
        public decimal oprc { get; set; }
        public string posi { get; set; } = "";
    }

    private sealed class SettingRow
    {
        public string name { get; set; } = "";
        public string valuec { get; set; } = "";
        public string valuec1 { get; set; } = "";
    }

    private static SettingRow Get(Dictionary<string, SettingRow> map, string key) =>
        map.TryGetValue(key, out var row) ? row : new SettingRow();

    /// <summary>长段优先（valuec1 有内容用之），回退短段 valuec。</summary>
    private static string LongOrShort(SettingRow row) =>
        row.valuec1.Trim().Length > 0 ? row.valuec1.Trim() : row.valuec.Trim();

    private sealed class HeadRow
    {
        public string gname { get; set; } = "";
        public DateTime outdate { get; set; }
        public decimal yhl { get; set; }
        public string guno { get; set; } = "";
        public string per { get; set; } = "";
        public string pno { get; set; } = "";
        public string spname { get; set; } = "";
        public string? checkno { get; set; }
        public string? adr { get; set; }
        public string? bank { get; set; }
        public string? tel { get; set; }
        public string? type1 { get; set; }
        public string remark { get; set; } = "";
    }
}
