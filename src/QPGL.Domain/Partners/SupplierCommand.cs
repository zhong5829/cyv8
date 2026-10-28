using QPGL.Domain.Common;

namespace QPGL.Domain.Partners;

/// <summary>
/// 供应商新增/修改命令（A6，M5）。供应商表=tbgugys（生产 80 行；tbgu 全为 KH 客户，type1 空）。
/// 结构契约：gyno/gyname/linkman/tel/fax/zip/cno/adr/code/code1/zq(账期)/email/note/note1/bank/checkno/sl/lb。
/// 校验参照 fr_gu 模式：gyno 唯一、gyname 必填。
/// </summary>
public sealed class SaveSupplierCommand
{
    /// <summary>null=新增；非 null=修改该 id。</summary>
    public int? Id { get; set; }

    /// <summary>供应商编号（gyno，唯一）。</summary>
    public required string Gyno { get; set; }
    /// <summary>供应商名称（gyname，必填）。</summary>
    public required string Gyname { get; set; }
    public string Linkman { get; set; } = "";
    public string Tel { get; set; } = "";
    public string Fax { get; set; } = "";
    public string Zip { get; set; } = "";
    public string Adr { get; set; } = "";
    /// <summary>账期（zq）。</summary>
    public decimal Zq { get; set; }
    public string Email { get; set; } = "";
    public string Note { get; set; } = "";
    public string Bank { get; set; } = "";
    public string Checkno { get; set; } = "";
    /// <summary>税率（sl）。</summary>
    public decimal Sl { get; set; }
    /// <summary>类别（lb）。</summary>
    public string Lb { get; set; } = "";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Gyno)) throw new AppException("GYNO_REQUIRED", "供应商编号不能为空", 422);
        if (string.IsNullOrWhiteSpace(Gyname)) throw new AppException("GYNAME_REQUIRED", "供应商名称不能为空", 422);
    }
}

/// <summary>供应商保存结果。</summary>
public sealed class SaveSupplierResult
{
    public required int Id { get; set; }
    public required bool IsCreate { get; set; }
}

/// <summary>供应商行（tbgugys）。</summary>
public sealed class SupplierRow
{
    public int Id { get; set; }
    public string Gyno { get; set; } = "";
    public string Gyname { get; set; } = "";
    public string Linkman { get; set; } = "";
    public string Tel { get; set; } = "";
    public string Fax { get; set; } = "";
    public string Adr { get; set; } = "";
    public decimal Zq { get; set; }
    public string Email { get; set; } = "";
    public string Note { get; set; } = "";
    public string Bank { get; set; } = "";
    public decimal Sl { get; set; }
    public string Lb { get; set; } = "";
}
