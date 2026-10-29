using System.Collections.Generic;
using System.Threading.Tasks;

namespace QPGL.Infrastructure.Vin;

/// <summary>VIN配件本地库存匹配服务 — 将外部数据源配件与本地配件/库存表进行模糊匹配</summary>
public interface IVinLocalMatchService
{
    /// <summary>对配件列表执行本地库存匹配，直接修改cards的LocalXxx字段；vehicleInfo为null时（按编号搜索）跳过车型评分</summary>
    Task EnrichWithLocalDataAsync(IEnumerable<VinPartCard> cards, VinDecodeResult? vehicleInfo);
}
