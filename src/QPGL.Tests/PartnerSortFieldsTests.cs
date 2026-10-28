using QPGL.Domain.Partners;

namespace QPGL.Tests;

/// <summary>排序白名单（§6.5：排序字段必须映射真实列，防注入）。</summary>
public class PartnerSortFieldsTests
{
    [Theory]
    [InlineData("partName", true)]
    [InlineData("PARTNAME", true)]   // 大小写不敏感
    [InlineData("partNo", true)]
    [InlineData("id; DROP TABLE tbgu", false)]
    [InlineData("(SELECT 1)", false)]
    [InlineData("", true)]           // 空=默认，允许
    [InlineData(null, true)]
    public void IsAllowed_MapsWhitelist(string? sort, bool expected)
        => Assert.Equal(expected, PartnerSortFields.IsAllowed(sort));

    [Fact]
    public void Resolve_InvalidField_FallsBackToId()
    {
        var sql = PartnerSortFields.Resolve("'; DROP TABLE tbgu--", descending: true);
        Assert.Equal("id DESC", sql);
        Assert.DoesNotContain("DROP", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_ValidField_MapsRealColumn()
    {
        Assert.Equal("gname ASC", PartnerSortFields.Resolve("partName", descending: false));
        Assert.Equal("gno DESC", PartnerSortFields.Resolve("partNo", descending: true));
    }
}
