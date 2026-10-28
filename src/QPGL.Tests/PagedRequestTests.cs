using QPGL.Domain.Common;

namespace QPGL.Tests;

/// <summary>分页参数钳制（§10.2-1 强制分页：单页 ≤100，page≥1）。</summary>
public class PagedRequestTests
{
    [Fact]
    public void Page_LessThanOne_ClampedToOne()
    {
        var req = new PagedRequest { Page = 0 };
        Assert.Equal(1, req.Page);
    }

    [Theory]
    [InlineData(101, 100)]
    [InlineData(100, 100)]
    [InlineData(0, 50)]     // 非法回默认 50
    [InlineData(-5, 50)]
    public void PageSize_Clamped(int input, int expected)
    {
        var req = new PagedRequest { PageSize = input };
        Assert.Equal(expected, req.PageSize);
    }

    [Fact]
    public void Order_Default_IsDescending()
    {
        var req = new PagedRequest();
        Assert.True(req.IsDescending);

        req.Order = "asc";
        Assert.False(req.IsDescending);
    }
}
