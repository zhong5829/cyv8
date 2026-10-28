namespace QPGL.Infrastructure.DocNo;

/// <summary>
/// 单号服务（开发文档 §5.2 / 决策 D4）。
/// 旧实现出处：pg_proc.getnewdh → SPT('exec getnewdh ?djname,?p_dpm,?isadd')。
/// 语义：单号 = tbdjgs.djjx(前缀) + 年后2位 + 6位流水；跨年自动重置；
/// isadd=1 时 tbdjgs.lsh 自增；返回前检查业务表重号（旧系统防重逻辑照抄）。
/// 并发：getnewdh 的 SELECT lsh+1 与 UPDATE lsh=lsh+1 存在竞态窗口（无幂等字段），
/// 服务端按「部门+单据类型」进程内异步锁串行化（水平扩展前须压测，§11.3）。
/// </summary>
public interface IDocNoService
{
    /// <param name="djName">单据类型名 = tbdjgs.djname（中文，如“销售单”）</param>
    /// <param name="dpm">部门编号（tbkey.dpmbh，如“A”）</param>
    Task<string> GetNewDocNoAsync(string djName, string dpm, CancellationToken ct = default);

    /// <summary>
    /// 指定防重目标的取号（旧 pg_proc.getnewdh 的 Y_TABLE/Y_FIELD 参数化）：
    /// 销售单防重查 tbsada.paper、盘点单查 tbistopd.pdpaper 等，随调用方业务表而定。
    /// </summary>
    Task<string> GetNewDocNoAsync(string djName, string dpm, DocNoService.DocNoCheckTarget target, CancellationToken ct = default);

    /// <summary>
    /// 预览取号（旧 getnewdh isadd=0）：只返回下一单号，不推进 tbdjgs.lsh、不防重。
    /// 用于确认对话框先显示单号；正式号在确认后 GetNewDocNoAsync 取得（并发下可能与预览号不同）。
    /// </summary>
    Task<string> PreviewDocNoAsync(string djName, string dpm, CancellationToken ct = default);
}
