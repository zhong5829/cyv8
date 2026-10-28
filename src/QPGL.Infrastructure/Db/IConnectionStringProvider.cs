namespace QPGL.Infrastructure.Db;

/// <summary>连接串提供者（由 QPGL.Api 实现；连接串只存在于 API，§6.9）。</summary>
public interface IConnectionStringProvider
{
    string ConnectionString { get; }
}
