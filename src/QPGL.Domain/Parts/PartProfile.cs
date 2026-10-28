using QPGL.Domain.Common;

namespace QPGL.Domain.Parts;

/// <summary>
/// 配件资料（零件属性库 tbprnoty，A1，M5）。
/// 结构契约：cp/nno/na1/ty/fa/dc/unit/note/iprj/xno/nnp/nnq/uqty/tno/amjj/bmjj/code_ts/cxnno/eng/cpict/
/// nvol/suttle/note1/oprc_cp/oprc_min —— **无 id 列**，逻辑键 = cp+nno。
/// 入库（fr_isto1 row31 L213-228）新配件时自动 INSERT（cp,nno,na1,ty,fa,dc,unit,note,iprj,uqty,cxnno,oprc_cp,oprc_min,cpict）。
/// 一期：分页查询 + 修改（iprj 进价/na1/ty/note）；新增走散件入库自动生成。
/// </summary>
public sealed class PartProfileRow
{
    public string Cp { get; set; } = "";
    public string Nno { get; set; } = "";
    public string Na1 { get; set; } = "";
    public string Ty { get; set; } = "";
    public string Fa { get; set; } = "";
    public string Dc { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Iprj { get; set; }
    /// <summary>常用量（uqty）。</summary>
    public decimal Uqty { get; set; }
    /// <summary>最低售价（oprc_min）。</summary>
    public decimal OprcMin { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>配件资料查询请求（分页 + 排序白名单）。</summary>
public sealed class PartProfileQueryRequest : PagedRequest
{
    /// <summary>厂牌（前缀匹配）。</summary>
    public string? Cp { get; set; }

    /// <summary>零件号（前缀匹配）。</summary>
    public string? Nno { get; set; }

    /// <summary>名称（包含匹配）。</summary>
    public string? Na1 { get; set; }

    /// <summary>车型（包含匹配）。</summary>
    public string? Ty { get; set; }
}

/// <summary>配件资料排序白名单。</summary>
public static class PartProfileSortFields
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cp"] = "cp",
        ["nno"] = "nno",
        ["na1"] = "na1",
        ["ty"] = "ty",
        ["iprj"] = "iprj",
    };

    public static bool TryResolve(string? sort, out string column)
    {
        column = "";
        if (string.IsNullOrWhiteSpace(sort)) return false;
        return Map.TryGetValue(sort.Trim(), out column!);
    }
}

/// <summary>配件资料修改命令（WHERE cp+nno）。</summary>
public sealed class UpdatePartProfileCommand
{
    public required string Cp { get; set; }
    public required string Nno { get; set; }
    public string? Na1 { get; set; }
    public string? Ty { get; set; }
    public string? Unit { get; set; }
    public decimal? Iprj { get; set; }
    public decimal? Uqty { get; set; }
    public decimal? OprcMin { get; set; }
    public string? Note { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Cp)) throw new AppException("CP_REQUIRED", "厂牌不能为空", 422);
        if (string.IsNullOrWhiteSpace(Nno)) throw new AppException("NNO_REQUIRED", "零件号不能为空", 422);
    }
}

/// <summary>
/// 单号前缀行（tbdjgs，A9）：单号 = djjx 前缀 + dyear 后 2 位 + lsh 流水（getnewdh 机制）。
/// 生产值域（部门 A）：报价单 AJ/调拨单 AB/退货单 AH/散件入库单 RK/收款单 SK/付款单 FK/盘点单 AP/进库单 AK…
/// </summary>
public sealed class DocNoPrefixRow
{
    public int Id { get; set; }
    /// <summary>单据类型名（djname，如 销售单/退货单/收款单）。</summary>
    public string Djname { get; set; } = "";
    /// <summary>前缀（djjx，如 XS/AH/SK）。</summary>
    public string Djjx { get; set; } = "";
    /// <summary>当前流水（lsh）。</summary>
    public int Lsh { get; set; }
    /// <summary>年份（dyear，getnewdh 取 dyear=当前年）。</summary>
    public string Dyear { get; set; } = "";
    public string Dpm { get; set; } = "";
}

/// <summary>单号前缀修改命令（fr_billprefx row5：校验 SK/FK 前缀唯一后 UPDATE djjx）。</summary>
public sealed class UpdateDocNoPrefixCommand
{
    public required int Id { get; set; }
    /// <summary>新前缀（djjx，≤4 字符）。</summary>
    public required string Djjx { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Djjx)) throw new AppException("DJJX_REQUIRED", "前缀不能为空", 422);
        if (Djjx.Trim().Length > 4) throw new AppException("DJJX_TOO_LONG", "前缀不能超过 4 字符", 422);
    }
}
