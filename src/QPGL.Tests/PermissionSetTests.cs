using QPGL.Domain.Auth;
using Xunit;

namespace QPGL.Tests;

/// <summary>
/// M3 权限集解析测试（契约依据：fr_setpower row16 保存格式 + fr_main pwcheck 过滤 + fr_login row12 加载）。
/// 实际数据样例（备用数据 tbrole）：",_,A0,A1,...,C1,..." / ",@,ASD,B82,..."
/// </summary>
public class PermissionSetTests
{
    [Fact]
    public void Parse_RoleUsem_MatchesMenuByCommaDelimiter()
    {
        var p = PermissionSet.Parse(",_,A0,A1,C1,C5,E2,G2,", "");
        Assert.True(p.HasMenu("C1"));
        Assert.True(p.HasMenu("E2"));
        Assert.False(p.HasMenu("C2"));   // 无此权限点
        Assert.False(p.HasMenu("W7"));   // 维修按约定排除
    }

    [Fact]
    public void Parse_SuperMark_BypassesAllChecks()
    {
        var p = PermissionSet.Parse(",@,A1,", "");
        Assert.True(p.IsSuper);
        Assert.True(p.HasMenu("Z99"));   // 任意点均放行
        Assert.True(p.HasFlag('/'));     // 操作位也放行
    }

    [Fact]
    public void Parse_PartialNumber_DoesNotFalseMatch()
    {
        // ",C11," 不应匹配 "C1"（旧子串匹配 ",C1," 语义）
        var p = PermissionSet.Parse(",C11,B1,", "");
        Assert.False(p.HasMenu("C1"));
        Assert.True(p.HasMenu("C11"));
    }

    [Fact]
    public void Parse_DataAdminFlag()
    {
        var p = PermissionSet.Parse(",$,A1,", "");
        Assert.True(p.IsDataAdmin);
        Assert.False(p.IsSuper);
        Assert.True(p.HasMenu("A1"));
    }

    [Fact]
    public void Parse_Usem1OperationFlags()
    {
        // fr_setpower 保存序：'/'修改 'C'拆分 '*'抹零 '-'成本价 'G'挂账 + 尾数字
        var p = PermissionSet.Parse(",A1,", "/C*G2");
        Assert.True(p.HasFlag('/'));
        Assert.True(p.HasFlag('*'));
        Assert.True(p.HasFlag('G'));
        Assert.False(p.HasFlag('-'));    // usem1 不含 '-'（成本价不可见）
        Assert.Equal(2, p.ClientTypeOption);
    }

    [Fact]
    public void Parse_EmptyOrNull_AllDenied()
    {
        var p = PermissionSet.Parse(null, null);
        Assert.False(p.HasMenu("C1"));
        Assert.False(p.IsSuper);
        Assert.False(p.IsDataAdmin);
        Assert.False(PermissionSet.Parse("", "").HasMenu("A1"));
    }
}

/// <summary>出库单查询排序白名单（§7.1 禁注入）。</summary>
public class OutDocSortFieldsTests
{
    [Theory]
    [InlineData("outdate", "c.outdate")]
    [InlineData("paper", "c.paper")]
    [InlineData("gname", "c.gname")]
    [InlineData("oprct", "c.oprct")]
    public void TryResolve_WhitelistedColumns_AreMapped(string input, string expected)
    {
        Assert.True(QPGL.Domain.Query.OutDocSortFields.TryResolve(input, out var col));
        Assert.Equal(expected, col);
    }

    [Theory]
    [InlineData("c.password; drop table tbsada")]
    [InlineData("mark")]
    [InlineData("")]
    [InlineData(null)]
    public void TryResolve_NonWhitelisted_AreRejected(string? input)
    {
        Assert.False(QPGL.Domain.Query.OutDocSortFields.TryResolve(input, out _));
    }
}

/// <summary>收款守卫（挂账类判断：fr_sk row369/row34 INLIST 语义）。</summary>
public class ReceiptGuardsTests
{
    [Theory]
    [InlineData("挂帐")]
    [InlineData("托收")]
    [InlineData("汇款")]
    public void IsOnCredit_CreditTerms_True(string payTerm)
    {
        Assert.True(QPGL.Domain.Receipt.ReceiptGuards.IsOnCredit(payTerm));
    }

    [Theory]
    [InlineData("现金")]
    [InlineData("刷卡")]
    [InlineData("挂帐冲预收")]
    [InlineData("")]
    public void IsOnCredit_InstantTerms_False(string payTerm)
    {
        Assert.False(QPGL.Domain.Receipt.ReceiptGuards.IsOnCredit(payTerm));
    }
}
