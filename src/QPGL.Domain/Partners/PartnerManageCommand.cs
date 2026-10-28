using QPGL.Domain.Common;

namespace QPGL.Domain.Partners;

/// <summary>
/// 客户资料新增/修改命令（A5，M5）。
/// 旧实现出处：fr_gu row5 —— M_FLDS =
///   gno,linkman,job,gname,tel,mobile,fax,zip,adr,note,note1,type1,type2,checkno,bank,name1,yhl,ywy,cwfzr,qq,web,email,sendcom,formula_name,ch,dp,cx,color,clcp,guno,dpm,pno
/// 校验：①gno 非空且（新增或改号）→ tbgu 查重；②name1（通用单位）必填；
/// ③name1≠gname 时 tbgu 必须已存在 gname=name1 的主客户（通用单位语义）；
/// ④type2 新值自动补 tbsysset（type='客户类型'）。
/// 删除（fr_gu p_.row111）：DELETE tbgu WHERE id（旧无前置检查，照抄）。
/// </summary>
public sealed class SavePartnerCommand
{
    /// <summary>null=新增；非 null=修改该 id。</summary>
    public int? Id { get; set; }

    /// <summary>客户编号（手输，如 KH0001；唯一）。</summary>
    public required string Gno { get; set; }
    /// <summary>客户名称。</summary>
    public required string Gname { get; set; }
    /// <summary>通用单位（必填；=gname 表示自身，否则须为已有主客户名）。</summary>
    public required string Name1 { get; set; }
    public string Linkman { get; set; } = "";
    public string Job { get; set; } = "";
    public string Tel { get; set; } = "";
    public string Mobile { get; set; } = "";
    public string Fax { get; set; } = "";
    public string Zip { get; set; } = "";
    public string Adr { get; set; } = "";
    public string Note { get; set; } = "";
    /// <summary>客户类型（type2，新值自动补 tbsysset type='客户类型'）。</summary>
    public string Type2 { get; set; } = "";
    /// <summary>优惠率。</summary>
    public decimal Yhl { get; set; }
    /// <summary>业务员。</summary>
    public string Ywy { get; set; } = "";
    public string Bank { get; set; } = "";
    public string Checkno { get; set; } = "";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Gno)) throw new AppException("GNO_REQUIRED", "客户号不能为空", 422);
        if (string.IsNullOrWhiteSpace(Gname)) throw new AppException("GNAME_REQUIRED", "客户名称不能为空", 422);
        if (string.IsNullOrWhiteSpace(Name1)) throw new AppException("NAME1_REQUIRED", "通用单位不能为空", 422);
    }
}

/// <summary>客商保存结果。</summary>
public sealed class SavePartnerResult
{
    public required int Id { get; set; }
    public required bool IsCreate { get; set; }
}
