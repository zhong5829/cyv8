using Dapper;
using Microsoft.Data.SqlClient;
using QPGL.Infrastructure.Db;

namespace QPGL.Infrastructure.Auth;

public interface IPermissionRepository
{
    /// <summary>按 部门编号+角色 取角色权限（tbrole；结构零改动）。</summary>
    Task<RolePermissionRow?> GetRoleAsync(string dpmbh, string role, CancellationToken ct = default);

    /// <summary>角色名单（用户管理/登录诊断用）。</summary>
    Task<IReadOnlyList<string>> GetRolesAsync(CancellationToken ct = default);
}

/// <summary>tbrole 行（usem 为 text 列，Dapper 映射 string）。</summary>
public sealed class RolePermissionRow
{
    public int id { get; set; }
    public string dpmbh { get; set; } = "";
    public string role { get; set; } = "";
    public string usem { get; set; } = "";
    public string usem1 { get; set; } = "";
    public string usem2 { get; set; } = "";
    public string dpmusem { get; set; } = "";
    public string dpmisto { get; set; } = "";
    public string usemcp { get; set; } = "";
    public int xlyhl { get; set; }
    public int xlpyhl { get; set; }
}

/// <summary>
/// 角色权限仓储（M3 权限完整）。
/// 旧实现出处：fr_login row12 ——
///   PASSTH("select","tbRole","*","dpmbh=?c_key.dpmbh and role=?c_key.role","i_role")
///   P_USEM=USEM（角色 usem）、P_USEM1=USEM1、P_USEM2=USEM2、
///   P_USEMDEPOTNO=USEMDEPOTNO、P_USEMPAPERTYPE=USEMPAPERTYPE、P_XLYHL=XLYHL、P_XLPYHL=XLPYHL。
/// 注意：权限一律取角色表（tbkey.usem 在备用数据全空；仅旧客户'东联'特例走 tbkey，不迁移）。
/// </summary>
public sealed class PermissionRepository(SqlConnectionFactory factory) : IPermissionRepository
{
    public async Task<RolePermissionRow?> GetRoleAsync(string dpmbh, string role, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var cmd = new CommandDefinition(
            """
            SELECT id, dpmbh, role, CAST(usem AS varchar(max)) AS usem,
                   CAST(usem1 AS varchar(max)) AS usem1, CAST(usem2 AS varchar(max)) AS usem2,
                   dpmusem, dpmisto, usemcp, xlyhl, xlpyhl
            FROM tbrole WITH (NOLOCK)
            WHERE dpmbh = @dpmbh AND role = @role
            """,
            new
            {
                dpmbh = new DbString { Value = dpmbh, IsAnsi = true, Length = 10 },
                role = new DbString { Value = role, IsAnsi = true, Length = 20 },
            },
            cancellationToken: ct);
        return await conn.QueryFirstOrDefaultAsync<RolePermissionRow>(cmd);
    }

    public async Task<IReadOnlyList<string>> GetRolesAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var cmd = new CommandDefinition(
            "SELECT DISTINCT role FROM tbrole WITH (NOLOCK) WHERE role <> '' ORDER BY role",
            cancellationToken: ct);
        return (await conn.QueryAsync<string>(cmd)).AsList().Select(r => r.Trim()).ToList();
    }
}
