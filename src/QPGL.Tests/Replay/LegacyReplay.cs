using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace QPGL.Tests.Replay;

/// <summary>
/// 旧系统回放脚本：按逆向契约（fr_sale row548 / row298 / row145 / row43 + getnewdh）
/// 原样重放旧 VFP 写库行为，与新系统仓储在同一副本库上对比（§11.2）。
/// </summary>
public static partial class LegacyReplay
{
    public sealed record Batch(
        int Id, string Cp, string Nno, string Na1, string Ty, string Fa, string Dc,
        string Unit, decimal Kc, decimal Iprj, decimal Iprc, decimal Oprc,
        string Posi, string Depotno, int Xno, int Tno, string Cxnno, decimal Oprb);

    /// <summary>
    /// 种子批次：同一配件且该配件须有 ≥count 个正库存批次（从满足批次数条件的配件中取
    /// 最大批次 kcamount 最大者，保证 count 个批次可用），批序与新系统 StockAllocator 同源。
    /// </summary>
    public static List<Batch> GetSeedBatches(SqlConnection conn, int count)
    {
        var rows = conn.Query<(int id, string cp, string nno, string na1, string ty, string fa,
                string dc, string unit, decimal kc, decimal iprj, decimal iprc, decimal oprc,
                string posi, string depotno, int xno, int tno, string cxnno, decimal oprb)>(
            """
            SELECT TOP (@n) id, RTRIM(cp) cp, RTRIM(nno) nno, RTRIM(na1) na1, RTRIM(ty) ty, RTRIM(fa) fa,
                   RTRIM(dc) dc, RTRIM(unit) unit, kcamount kc, iprj, iprc, oprc,
                   RTRIM(posi) posi, RTRIM(depotno) depotno, xno, tno, RTRIM(cxnnO) cxnno, oprb
            FROM tbisto WITH (NOLOCK)
            WHERE dpm = @dpm AND kcamount > 0
              AND cp = (SELECT TOP (1) cp FROM tbisto WITH (NOLOCK) WHERE dpm = @dpm AND kcamount > 0
                        GROUP BY cp, nno HAVING COUNT(1) >= @n ORDER BY MAX(kcamount) DESC)
              AND nno = (SELECT TOP (1) nno FROM tbisto WITH (NOLOCK) WHERE dpm = @dpm AND kcamount > 0
                         GROUP BY cp, nno HAVING COUNT(1) >= @n ORDER BY MAX(kcamount) DESC)
            ORDER BY kcamount DESC, id ASC
            """,
            new { n = count, dpm = Ansi(ReplayConfig.Dept) }).ToList();
        return rows.Select(r => new Batch(r.id, r.cp, r.nno, r.na1, r.ty, r.fa, r.dc,
            r.unit, r.kc, r.iprj, r.iprc, r.oprc, r.posi, r.depotno, r.xno, r.tno, r.cxnno, r.oprb)).ToList();
    }

    /// <summary>取号（旧系统路径：exec dbo.getnewdh）。</summary>
    public static string GetNewDh(SqlConnection conn, string djName, string dpm, int isAdd)
    {
        var paper = conn.ExecuteScalar<string>(
            "dbo.getnewdh",
            new { ctype = djName, dpm = new DbString { Value = dpm, IsAnsi = true, Length = 1 }, isadd = isAdd },
            commandType: CommandType.StoredProcedure);
        return paper!.Trim();
    }

    /// <summary>取号（事务内调用变体：Dapper 必须显式传 transaction，教训 §8.3）。</summary>
    public static string GetNewDh(SqlConnection conn, SqlTransaction tx, string djName, string dpm, int isAdd)
    {
        var paper = conn.ExecuteScalar<string>(
            "dbo.getnewdh",
            new { ctype = djName, dpm = new DbString { Value = dpm, IsAnsi = true, Length = 1 }, isadd = isAdd },
            transaction: tx, commandType: CommandType.StoredProcedure);
        return paper!.Trim();
    }

    /// <summary>场景 1/2：销售开单（row548 列组合原样重放；ckamounts 为各批次出库量）。</summary>
    public static string Sale(SqlConnection conn, IReadOnlyList<(Batch b, decimal qty)> lines, string per)
    {
        var paper = GetNewDh(conn, "销售单", ReplayConfig.Dept, 1);
        foreach (var (b, qty) in lines)
        {
            conn.Execute(
                """
                INSERT INTO tbsada
                    (cp, nno, na1, fa, ty, unit, type, posi, ckamount, oprc, oprct,
                     iprj, iprc, paper, gno, gname, per, xno, tno, cxnno,
                     pnno, pna1, pty, pfa, kcamount, outdate, dpm, depotno, kcid, dc, dscp)
                VALUES
                    (@cp, @nno, @na1, @fa, @ty, @unit, N'销售', @posi, @qty, @oprc, @oprct,
                     @iprj, @iprc, @paper, @gno, @gname, @per, @xno, @tno, @cxnno,
                     @pnno, @pna1, @pty, @pfa, @kcamount, @outdate, @dpm, @depotno, @kcid, @dc, @dscp)
                """,
                new
                {
                    cp = b.Cp, nno = b.Nno, na1 = b.Na1, fa = b.Fa, ty = b.Ty, unit = b.Unit,
                    posi = b.Posi, qty, oprc = b.Oprc, oprct = b.Oprc, iprj = b.Iprj, iprc = b.Iprc,
                    paper, gno = ReplayConfig.Gno, gname = ReplayConfig.Gname, per,
                    xno = b.Xno, tno = b.Tno, cxnno = b.Cxnno,
                    pnno = b.Nno, pna1 = b.Na1, pty = b.Ty, pfa = b.Fa,
                    kcamount = b.Kc, outdate = DateTime.Today, dpm = Ansi(ReplayConfig.Dept),
                    depotno = b.Depotno, kcid = b.Id, dc = b.Dc, dscp = b.Cp,
                });
        }

        // 单头（row298）
        conn.Execute(
            """
            IF EXISTS (SELECT 1 FROM tbsada_header WHERE paper = @paper AND dpm = @dpm)
                UPDATE tbsada_header SET spname = @spname WHERE paper = @paper AND dpm = @dpm
            ELSE
                INSERT INTO tbsada_header (paper, dpm, papertype, jptype, spname)
                VALUES (@paper, @dpm, '', N'单据类型', @spname)
            """,
            new { paper, dpm = Ansi(ReplayConfig.Dept), spname = Ansi(ReplayConfig.Gname, 40) });

        return paper;
    }

    /// <summary>场景 30：报价开单（fr_sale with '报价'，row548 列集；有库存行 kcid=批次 id、kcamount=快照；
    /// 无库存行 kcid=0（row469）。type='报价' 不在 sale_in 过滤集 → 触发器豁免扣减）。</summary>
    public static string Quote(SqlConnection conn, Batch b, decimal qty, string per)
    {
        var paper = GetNewDh(conn, "报价单", ReplayConfig.Dept, 1);
        conn.Execute(
            """
            INSERT INTO tbsada
                (cp, nno, na1, fa, ty, unit, type, posi, ckamount, oprc, oprct,
                 iprj, iprc, paper, gno, gname, per, xno, tno, cxnno,
                 pnno, pna1, pty, pfa, kcamount, outdate, dpm, depotno, kcid, dc, dscp)
            VALUES
                (@cp, @nno, @na1, @fa, @ty, @unit, N'报价', @posi, @qty, @oprc, @oprc,
                 @iprj, @iprc, @paper, @gno, @gname, @per, @xno, @tno, @cxnno,
                 @pnno, @pna1, @pty, @pfa, @kcamount, @outdate, @dpm, @depotno, @kcid, @dc, @dscp)
            """,
            new
            {
                cp = b.Cp, nno = b.Nno, na1 = b.Na1, fa = b.Fa, ty = b.Ty, unit = b.Unit,
                posi = b.Posi, qty, oprc = b.Oprc, iprj = b.Iprj, iprc = b.Iprc,
                paper, gno = ReplayConfig.Gno, gname = ReplayConfig.Gname, per,
                xno = b.Xno, tno = b.Tno, cxnno = b.Cxnno,
                pnno = b.Nno, pna1 = b.Na1, pty = b.Ty, pfa = b.Fa,
                kcamount = b.Kc, outdate = DateTime.Today, dpm = Ansi(ReplayConfig.Dept),
                depotno = b.Depotno, kcid = b.Id, dc = b.Dc, dscp = b.Cp,
            });

        // 单头（row298 同构）
        conn.Execute(
            """
            IF EXISTS (SELECT 1 FROM tbsada_header WHERE paper = @paper AND dpm = @dpm)
                UPDATE tbsada_header SET spname = @spname WHERE paper = @paper AND dpm = @dpm
            ELSE
                INSERT INTO tbsada_header (paper, dpm, papertype, jptype, spname)
                VALUES (@paper, @dpm, '', N'单据类型', @spname)
            """,
            new { paper, dpm = Ansi(ReplayConfig.Dept), spname = Ansi(ReplayConfig.Gname, 40) });

        return paper;
    }

    /// <summary>场景 30：报价确认（row298 FRMOK：9 列 UPDATE WHERE paper + header upsert；报价 jzdate=outdate）。</summary>
    public static void ConfirmQuote(SqlConnection conn, string paper, DateTime outdate)
    {
        conn.Execute(
            """
            UPDATE tbsada
            SET gno = @gno, gname = @gname, pno = '', t_mode = '', yhl = 0,
                guno = N'现金', outdate = @outdate, jzdate = @outdate, remark = ''
            WHERE paper = @paper
            """,
            new
            {
                gno = Ansi(ReplayConfig.Gno, 10),
                gname = Ansi(ReplayConfig.Gname, 60),
                outdate,
                paper = Ansi(paper, 10),
            });
        conn.Execute(
            """
            IF EXISTS (SELECT 1 FROM tbsada_header WITH (NOLOCK) WHERE paper = @paper AND dpm = @dpm)
                UPDATE tbsada_header SET papertype = '', spname = '' WHERE paper = @paper AND dpm = @dpm
            ELSE
                INSERT INTO tbsada_header (paper, dpm, papertype, jptype, spname)
                VALUES (@paper, @dpm, '', N'单据类型', '')
            """,
            new { paper = Ansi(paper, 10), dpm = Ansi(ReplayConfig.Dept, 10) });
    }

    /// <summary>场景 30：报价转销售（INSERT SELECT type='销售'，kcid 原样 → sale_in 扣库存；ypaper=报价单号；无 header）。</summary>
    public static string QuoteToSale(SqlConnection conn, string quotePaper)
    {
        var paper = GetNewDh(conn, "销售单", ReplayConfig.Dept, 1);
        conn.Execute(
            """
            INSERT INTO tbsada
                (cp, nno, na1, fa, ty, unit, type, posi, ckamount, oprc, oprct, iprj, iprc, paper,
                 gno, gname, per, xno, tno, cxnno, pnno, pna1, pty, pfa, kcamount, outdate, kcid, depotno, dc, ypaper, dpm)
            SELECT cp, nno, na1, fa, ty, unit, N'销售', posi, ckamount, oprc, oprct, iprj, iprc, @newpaper,
                   gno, gname, per, xno, tno, cxnno, pnno, pna1, pty, pfa, kcamount, @outdate, kcid, depotno, dc, @quotepaper, dpm
            FROM tbsada
            WHERE paper = @quotepaper AND type = N'报价' AND dpm = @dpm
            """,
            new
            {
                newpaper = Ansi(paper, 10),
                outdate = DateTime.Today,
                quotepaper = Ansi(quotePaper, 10),
                dpm = Ansi(ReplayConfig.Dept, 10),
            });
        return paper;
    }

    /// <summary>场景 30：销售转报价（row53 销售分支：INSERT SELECT 急件恢复 + DELETE 原单 → sale_del 回补库存）。</summary>
    public static string SaleToQuote(SqlConnection conn, string salePaper)
    {
        var paper = GetNewDh(conn, "报价单", ReplayConfig.Dept, 1);
        conn.Execute(
            """
            INSERT INTO tbsada
                (cp, nno, na1, fa, ty, unit, type, posi, ckamount, oprc, oprct, iprj, iprc, paper,
                 gno, gname, per, xno, tno, cxnno, pnno, pna1, pty, pfa, kcamount, outdate, kcid, depotno, dc, ypaper, dpm)
            SELECT CASE WHEN cp = N'急件' THEN dscp ELSE cp END, nno, na1, fa, ty, unit, N'报价', posi, ckamount,
                   oprc, oprct, iprj, iprc, @newpaper, gno, gname, per, xno, tno, cxnno, pnno, pna1, pty, pfa,
                   kcamount, outdate, kcid, depotno, dc, @oldpaper, dpm
            FROM tbsada
            WHERE paper = @oldpaper
            """,
            new { newpaper = Ansi(paper, 10), oldpaper = Ansi(salePaper, 10) });
        conn.Execute(
            "DELETE FROM tbsada WHERE paper = @paper AND dpm = @dpm AND type = N'销售'",
            new { paper = Ansi(salePaper, 10), dpm = Ansi(ReplayConfig.Dept, 10) });
        return paper;
    }

    /// <summary>场景 3：销售退货（row145 序列原样：INSERT ckamount=0 → UPDATE 负数 → UPDATE 原行 ro → header）。</summary>
    public static string ReturnOneLine(SqlConnection conn, string sourcePaper, decimal qty, string per)
    {
        var src = conn.QueryFirst<(int id, string paper, string cp, string nno, string na1, string ty,
                string dc, string fa, string unit, string note, string depotno, string posi,
                decimal iprc, decimal iprj, decimal oprc, decimal oprct, decimal yhl, string gno,
                string gname, string pno, string guno, int xno, string t_mode, decimal ro, string dscp,
                bool trigger_mark, int kcid)>(
            """
            SELECT TOP 1 id, RTRIM(paper) paper, RTRIM(cp) cp, RTRIM(nno) nno, RTRIM(na1) na1,
                   RTRIM(ty) ty, RTRIM(dc) dc, RTRIM(fa) fa, RTRIM(unit) unit, RTRIM(note) note,
                   RTRIM(depotno) depotno, RTRIM(posi) posi, iprc, iprj, oprc, oprct, yhl,
                   RTRIM(gno) gno, RTRIM(gname) gname, RTRIM(pno) pno, RTRIM(guno) guno,
                   xno, RTRIM(t_mode) t_mode, ro, RTRIM(dscp) dscp, trigger_mark, kcid
            FROM tbsada WITH (NOLOCK)
            WHERE paper = @paper AND type = N'销售' AND ckamount > 0
            ORDER BY id
            """,
            new { paper = Ansi(sourcePaper, 10) });

        var retPaper = GetNewDh(conn, "退货单", ReplayConfig.Dept, 1);

        // 1) INSERT ckamount=0（避开 sale_in）
        var newId = conn.ExecuteScalar<int>(
            """
            INSERT INTO tbsada
                (paper, ypaper, type, cp, nno, na1, ty, dc, fa, unit, remark, note,
                 depotno, posi, iprc, iprj, oprc, oprct, ckamount, yhl, gno, gname, per,
                 outdate, pno, guno, xno, pnno, pna1, pty, pfa, kcid, dpm, t_mode, ro, dscp, trigger_mark)
            VALUES
                (@paper, @ypaper, N'销售', @cp, @nno, @na1, @ty, @dc, @fa, @unit, '', @note,
                 @depotno, @posi, @iprc, @iprj, @oprc, @oprct, 0, @yhl, @gno, @gname, @per,
                 @outdate, @pno, @guno, @xno, @pnno, @pna1, @pty, @pfa, @kcid, @dpm, @t_mode, @ro, @dscp, @triggerMark);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new
            {
                paper = retPaper, ypaper = src.paper, cp = src.cp, nno = src.nno, na1 = src.na1,
                ty = src.ty, dc = src.dc, fa = src.fa, unit = src.unit, note = src.note,
                depotno = src.depotno, posi = src.posi, iprc = src.iprc, iprj = src.iprj,
                oprc = src.oprc, oprct = src.oprct, yhl = src.yhl, gno = src.gno, gname = src.gname,
                per, outdate = DateTime.Today, pno = src.pno, guno = src.guno, xno = src.xno,
                pnno = src.nno, pna1 = src.na1, pty = src.ty, pfa = src.fa, kcid = src.kcid,
                dpm = Ansi(ReplayConfig.Dept), t_mode = src.t_mode, ro = src.id, dscp = src.dscp,
                triggerMark = src.trigger_mark,
            });

        // 2) UPDATE 负数（触发 sale_up 回补）
        conn.Execute("UPDATE tbsada SET ckamount = @ck WHERE id = @id",
            new { ck = -qty, id = newId });

        // 3) 原行 ro（应用层）
        conn.Execute("UPDATE tbsada SET ro = ro + @qty WHERE id = @id",
            new { qty, id = src.id });

        // 4) 单头（row145：无 spname）
        conn.Execute(
            """
            IF EXISTS (SELECT 1 FROM tbsada_header WHERE paper = @paper AND dpm = @dpm)
                UPDATE tbsada_header SET jptype = N'单据类型' WHERE paper = @paper AND dpm = @dpm
            ELSE
                INSERT INTO tbsada_header (paper, dpm, papertype, jptype)
                VALUES (@paper, @dpm, '', N'单据类型')
            """,
            new { paper = retPaper, dpm = Ansi(ReplayConfig.Dept) });

        return retPaper;
    }

    /// <summary>场景 4：作废/删单（row43：DELETE，sale_del 触发器回补；header 保留）。</summary>
    public static int VoidSale(SqlConnection conn, string paper, string dpm)
        => conn.Execute(
            "DELETE FROM tbsada WHERE paper = @paper AND dpm = @dpm AND type = N'销售'",
            new { paper = Ansi(paper, 10), dpm = Ansi(dpm, 10) });

    /// <summary>
    /// 场景 7：散件入库生效（fr_isor 收货 + fr_isto1 row31 + ISAPPE P_WAY=1 原样重放，纯应用层 SQL 无触发器）。
    /// 序列：INSERT tbistosm（PASSTH 补 dpm）→ UPDATE tbistosm 单头字段 → 逐行批次匹配
    /// （cp+nno+iprc+posi+depotno+dpm；未命中 INSERT tbisto 新行、命中 UPDATE kcamount+=jk）→
    /// UPDATE tbprnoty → INSERT tbistoed（TYPE='入库'，KCAMOUNT=入库后快照）→ DELETE tbistosm。
    /// </summary>
    public static void InStock(SqlConnection conn, string dept, string cno, IReadOnlyList<InStockLine> lines, string per)
    {
        foreach (var l in lines)
        {
            conn.Execute(
                """
                INSERT INTO tbistosm
                    (cp, dpm, nno, na1, ty, fa, dc, unit, jkamount, iprj, iprc, oprc, oprb, posi,
                     cno, gyno, gyname, pno, note, fkfs, depotno, ywr, Freight)
                VALUES
                    (@cp, @dpm, @nno, @na1, @ty, @fa, @dc, @unit, @jkamount, @iprj, @iprc, @oprc, @oprb, @posi,
                     @cno, @gyno, @gyname, @pno, @note, @fkfs, @depotno, @ywr, @freight)
                """,
                new
                {
                    cp = Ansi(l.Cp, 10), dpm = Ansi(dept, 10), nno = Ansi(l.Nno, 18), na1 = Ansi(l.Na1, 40),
                    ty = Ansi(l.Ty, 14), fa = Ansi(l.Fa, 8), dc = Ansi(l.Dc, 8), unit = Ansi(l.Unit, 4),
                    jkamount = l.Jkamount, iprj = l.Iprj, iprc = l.Iprc, oprc = l.Oprc, oprb = l.Oprb,
                    posi = Ansi(l.Posi, 10), cno = Ansi(cno, 15), gyno = Ansi("", 10), gyname = Ansi("", 60),
                    pno = Ansi("", 12), note = Ansi(l.Note, 100), fkfs = Ansi("现金", 10),
                    depotno = Ansi(l.Depotno, 10), ywr = Ansi("", 10), freight = 0m,
                });
        }

        conn.Execute("UPDATE tbistosm SET indate = @indate, per = @per, gyname = '', pno = '', fkfs = N'现金' WHERE cno = @cno",
            new { indate = DateTime.Today, per = Ansi(per, 6), cno = Ansi(cno, 15) });

        var item = 0;
        var rows = conn.Query<(int id, string cp, string nno, string na1, string ty, string fa, string dc,
            string unit, decimal jkamount, decimal iprj, decimal iprc, decimal oprc, decimal oprb,
            string posi, string depotno, string note, int xno, string cxnno)>(
            "SELECT id, RTRIM(cp) cp, RTRIM(nno) nno, RTRIM(na1) na1, RTRIM(ty) ty, RTRIM(fa) fa, RTRIM(dc) dc, " +
            "RTRIM(unit) unit, jkamount, iprj, iprc, oprc, oprb, RTRIM(posi) posi, RTRIM(depotno) depotno, " +
            "RTRIM(note) note, xno, RTRIM(cxnnO) cxnno FROM tbistosm WITH (UPDLOCK) WHERE cno = @cno ORDER BY id",
            new { cno = Ansi(cno, 15) }).ToList();

        foreach (var r in rows)
        {
            item++;
            var batch = conn.QueryFirstOrDefault<(int id, decimal kc)>(
                "SELECT id, kcamount FROM tbisto WITH (UPDLOCK) WHERE cp = @cp AND nno = @nno AND iprc = @iprc " +
                "AND posi = @posi AND depotno = @depotno AND dpm = @dpm",
                new
                {
                    cp = Ansi(r.cp, 10), nno = Ansi(r.nno, 18), iprc = r.iprc,
                    posi = Ansi(r.posi, 10), depotno = Ansi(r.depotno, 10), dpm = Ansi(dept, 10),
                });

            int batchId;
            if (batch.id == 0)
            {
                conn.Execute(
                    """
                    INSERT INTO tbisto
                        (dpm, cp, nno, na1, unit, kcamount, ty, fa, cxnno, iprj, iprc, oprc, posi, depotno, note, xno, dc, kcbhdate, oprb)
                    VALUES
                        (@dpm, @cp, @nno, @na1, @unit, @kcamount, @ty, @fa, @cxnno, @iprj, @iprc, @oprc, @posi, @depotno, @note, @xno, @dc, @kcbhdate, @oprb)
                    """,
                    new
                    {
                        dpm = Ansi(dept, 10), cp = Ansi(r.cp, 10), nno = Ansi(r.nno, 18),
                        na1 = Ansi(r.na1, 40), unit = Ansi(r.unit, 4), kcamount = r.jkamount,
                        ty = Ansi(r.ty, 14), fa = Ansi(r.fa, 8), cxnno = Ansi(r.cxnno, 18),
                        iprj = r.iprj, iprc = r.iprc, oprc = r.oprc, posi = Ansi(r.posi, 10),
                        depotno = Ansi(r.depotno, 10), note = Ansi(r.note, 100), xno = r.xno,
                        dc = Ansi(r.dc, 8), kcbhdate = DateTime.Today, oprb = r.oprb,
                    });
                batchId = conn.ExecuteScalar<int>("SELECT CAST(SCOPE_IDENTITY() AS int)");
            }
            else
            {
                conn.Execute("UPDATE tbisto SET kcamount = kcamount + @jkamount, kcbhdate = @kcbhdate WHERE id = @id",
                    new { jkamount = r.jkamount, kcbhdate = DateTime.Today, id = batch.id });
                batchId = batch.id;
            }
            var kcAfter = (batch.id == 0 ? 0 : batch.kc) + r.jkamount;

            conn.Execute("UPDATE tbprnoty SET na1 = @na1, ty = @ty, unit = @unit WHERE cp = @cp AND nno = @nno",
                new { na1 = Ansi(r.na1, 40), ty = Ansi(r.ty, 14), unit = Ansi(r.unit, 4), cp = Ansi(r.cp, 10), nno = Ansi(r.nno, 18) });

            // 属性库新配件自动建档（旧 row31 L206-228）
            var profileExists = conn.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM tbprnoty WITH (NOLOCK) WHERE cp = @cp AND nno = @nno",
                new { cp = Ansi(r.cp, 10), nno = Ansi(r.nno, 18) });
            if (profileExists == 0)
            {
                conn.Execute(
                    "INSERT INTO tbprnoty (cp, nno, na1, ty, fa, dc, unit, note, iprj, uqty, cxnno, oprc_cp, oprc_min, cpict) " +
                    "VALUES (@cp, @nno, @na1, @ty, @fa, @dc, @unit, @note, @iprj, 1, @cxnno, 0, 0, '')",
                    new
                    {
                        cp = Ansi(r.cp, 10), nno = Ansi(r.nno, 18), na1 = Ansi(r.na1, 40),
                        ty = Ansi(r.ty, 14), fa = Ansi(r.fa, 8), dc = Ansi(r.dc, 8),
                        unit = Ansi(r.unit, 4), note = Ansi(r.note, 254), iprj = r.iprj,
                        cxnno = Ansi(r.cxnno, 18),
                    });
            }

            conn.Execute(
                """
                INSERT INTO tbistoed
                    (CP, NNO, NA1, TY, FA, DC, UNIT, JKAMOUNT, KCAMOUNT, IPRJ, IPRC, OPRC, POSI, DEPOTNO,
                     INDATE, ITEM, CNO, BOX, SEANO, isord, PNO, GYNO, GYNAME, PER, NOTE, CXNNO, FKFS, TYPE, YWR, Freight, dpm)
                VALUES
                    (@cp, @nno, @na1, @ty, @fa, @dc, @unit, @jkamount, @kcamount, @iprj, @iprc, @oprc, @posi, @depotno,
                     @indate, @item, @cno, @box, @seano, @isord, @pno, @gyno, @gyname, @per, @note, @cxnno, @fkfs, @type, @ywr, @freight, @dpm)
                """,
                new
                {
                    cp = Ansi(r.cp, 10), nno = Ansi(r.nno, 18), na1 = Ansi(r.na1, 40),
                    ty = Ansi(r.ty, 14), fa = Ansi(r.fa, 8), dc = Ansi(r.dc, 8), unit = Ansi(r.unit, 4),
                    jkamount = r.jkamount, kcamount = kcAfter, iprj = r.iprj, iprc = r.iprc, oprc = r.oprc,
                    posi = Ansi(r.posi, 10), depotno = Ansi(r.depotno, 10), indate = DateTime.Today, item = item,
                    cno = Ansi(cno, 15), box = Ansi("", 10), seano = Ansi(cno, 15), isord = Ansi("N", 10),
                    pno = Ansi("", 12), gyno = Ansi("", 10), gyname = Ansi("", 60), per = Ansi(per, 10),
                    note = Ansi(r.note, 100), cxnno = Ansi(r.cxnno, 18), fkfs = Ansi("现金", 10),
                    type = Ansi("入库", 10), ywr = Ansi("", 10), freight = 0m, dpm = Ansi(dept, 10),
                });

            conn.Execute("DELETE tbistosm WHERE id = @id", new { id = r.id });
        }
    }

    public sealed record InStockLine(
        string Cp, string Nno, string Na1, string Ty, string Fa, string Dc, string Unit,
        decimal Jkamount, decimal Iprj, decimal Iprc, decimal Oprc, decimal Oprb,
        string Posi, string Depotno, string Note);

    /// <summary>
    /// 场景 5：库存盘点全流程（fr_istopd row114/row95/鐶.row47/row49/row92 原样重放）。
    /// 序列：tbdjgs 前缀保障 → getnewdh('盘点单') 取号 → INSERT tbistopd SELECT FROM tbisto（row114）→
    /// 逐行录实盘（row95：pdposi/npd/nyk=npd-kcamount）→ 调仓位（鐶.row47：tbistopd.posi + tbisto.posi
    /// + tbistoch 历史）→ 确认（row49：nyk<>0 → zt='1',mark='1'；nyk=0 → DELETE）→
    /// 生成散件入库草稿（row92：nyk<>0 → INSERT tbistosm，GYNO='PDRK'，JKAMOUNT=NYK 可负）。
    /// </summary>
    public static string Stocktake(SqlConnection conn, string dept, string cp, string per, string nnoPrefix,
        IReadOnlyList<(decimal npdDelta, string? pdposi)> countPlan)
    {
        // 1) tbdjgs 前缀保障（row114 L16-24）
        var exists = conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM tbdjgs WHERE dpm = @dpm AND djname = N'盘点单'",
            new { dpm = Ansi(dept, 10) });
        if (exists == 0)
            conn.Execute(
                "INSERT INTO tbdjgs (djname, djjx, lsh, dyear, dpm) VALUES (N'盘点单', @djjx, 0, @dyear, @dpm)",
                new { djjx = Ansi(dept + "P", 4), dyear = Ansi(DateTime.Now.Year.ToString(), 4), dpm = Ansi(dept.ToUpperInvariant(), 10) });

        // 2) 取号（row114 L29）
        var paper = GetNewDh(conn, "盘点单", dept, 1);

        // 3) INSERT...SELECT（row114 L33-34 原样）
        conn.Execute(
            """
            INSERT INTO tbistopd
                (cp, dpm, nno, cxnno, na1, ty, fa, unit, iprj, kcamount, iprc, posi, oprc,
                 npd, nyk, dc, depotno, kcid, pdpaper, per, pd_date, mark, pdposi)
            SELECT cp, dpm, nno, cxnnO, na1, ty, fa, unit, iprj, ISNULL(kcamount, 0), iprc, posi, oprc,
                   ISNULL(kcamount, 0), 0, dc, depotno, id, @paper, @per, @pdDate, 0, posi
            FROM tbisto
            WHERE cp = @cp AND RTRIM(nno) LIKE @nno + '%' AND dpm = @dpm
            """,
            new
            {
                paper = Ansi(paper, 15), per = Ansi(per, 10), pdDate = DateTime.Today,
                cp = Ansi(cp, 10), nno = Ansi(nnoPrefix, 18), dpm = Ansi(dept, 10),
            });

        // 4) 录实盘（row95）：m.NYK = m.NPD - KCAMOUNT（客户端算）→ UPDATE pdposi,npd,nyk
        var lines = conn.Query<(int id, decimal kc, string posi)>(
            "SELECT id, kcamount, RTRIM(posi) FROM tbistopd WHERE pdpaper = @pdpaper ORDER BY id",
            new { pdpaper = Ansi(paper, 15) }).ToList();
        if (lines.Count != countPlan.Count)
            throw new InvalidOperationException($"盘点行数不符：预期 {countPlan.Count} 实际 {lines.Count}");
        for (var i = 0; i < lines.Count; i++)
        {
            var (npdDelta, pdposi) = countPlan[i];
            var npd = lines[i].kc + npdDelta;
            var pd = pdposi ?? lines[i].posi;
            conn.Execute(
                "UPDATE tbistopd SET pdposi = @pdposi, npd = @npd, nyk = @nyk WHERE id = @id",
                new { pdposi = Ansi(pd, 10), npd, nyk = npd - lines[i].kc, id = lines[i].id });
        }

        // 5) 调仓位（鐶.row47）：pdposi<>posi 行 → tbistopd.posi + tbisto.posi(kcid) + tbistoch
        var adjustRows = conn.Query<(int id, int kcid, string posi, string pdposi, string cp, string nno,
            string na1, string ty, string unit, string depotno)>(
            """
            SELECT id, kcid, RTRIM(posi), RTRIM(pdposi), RTRIM(cp), RTRIM(nno), RTRIM(na1), RTRIM(ty),
                   RTRIM(unit), RTRIM(depotno)
            FROM tbistopd WHERE pdpaper = @pdpaper AND pdposi <> posi
            """,
            new { pdpaper = Ansi(paper, 15) }).ToList();
        foreach (var r in adjustRows)
        {
            conn.Execute("UPDATE tbistopd SET posi = @pdposi WHERE id = @id", new { pdposi = Ansi(r.pdposi, 10), id = r.id });
            conn.Execute("UPDATE tbisto SET posi = @pdposi WHERE id = @kcid", new { pdposi = Ansi(r.pdposi, 10), kcid = r.kcid });
            conn.Execute(
                """
                INSERT INTO tbistoch (cp, dpm, nno, na1, ty, unit, depotno, posi, xgdate, per, note)
                VALUES (@cp, @dpm, @nno, @na1, @ty, @unit, @depotno, @posi, @xgdate, @per, @note)
                """,
                new
                {
                    cp = Ansi(r.cp, 10), dpm = Ansi("", 10), nno = Ansi(r.nno, 18), na1 = Ansi(r.na1, 40),
                    ty = Ansi(r.ty, 14), unit = Ansi(r.unit, 4), depotno = Ansi(r.depotno, 10),
                    posi = Ansi(r.pdposi, 10), xgdate = DateTime.Today, per = Ansi(per, 6),
                    note = Ansi($"修改仓位：{r.posi} -> {r.pdposi}", 100),
                });
        }

        // 6) 确认（row49 L25-26）
        conn.Execute(
            "UPDATE tbistopd SET pd_date = @pdDate, zt = '1', mark = '1' WHERE nyk <> 0 AND pdpaper = @pdpaper",
            new { pdDate = DateTime.Today, pdpaper = Ansi(paper, 15) });
        conn.Execute(
            "DELETE FROM tbistopd WHERE nyk = 0 AND pdpaper = @pdpaper",
            new { pdpaper = Ansi(paper, 15) });

        // 7) 生成散件入库草稿（row92 L41-61）
        var hasDraft = conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM tbistosm WHERE cno = @cno", new { cno = Ansi(paper, 15) }) > 0;
        var mx = conn.Query<(string cp, string dpm, string nno, string na1, string ty, string fa, string dc,
            string unit, decimal nyk, decimal iprj, decimal iprc, decimal oprc, string pdposi, string depotno,
            string note)>(
            """
            SELECT RTRIM(cp), RTRIM(dpm), RTRIM(nno), RTRIM(na1), RTRIM(ty), RTRIM(fa), RTRIM(dc), RTRIM(unit),
                   nyk, iprj, iprc, oprc, RTRIM(pdposi), RTRIM(depotno), RTRIM(note)
            FROM tbistopd WHERE pdpaper = @pdpaper ORDER BY id
            """,
            new { pdpaper = Ansi(paper, 15) }).ToList();
        foreach (var l in mx)
        {
            if (l.nyk == 0)
                continue;
            var oprb = conn.ExecuteScalar<decimal?>(
                "SELECT MAX(oprb) FROM tbisto WHERE cp = @cp AND nno = @nno",
                new { cp = Ansi(l.cp, 10), nno = Ansi(l.nno, 18) }) ?? 0m;
            conn.Execute(
                """
                INSERT INTO tbistosm
                    (cp, dpm, nno, na1, ty, fa, dc, unit, jkamount, iprj, iprc, oprc, posi, depotno,
                     indate, cno, gyno, gyname, per, note, fkfs, oprb)
                VALUES
                    (@cp, @dpm, @nno, @na1, @ty, @fa, @dc, @unit, @jkamount, @iprj, @iprc, @oprc, @posi, @depotno,
                     @indate, @cno, @gyno, @gyname, @per, @note, @fkfs, @oprb)
                """,
                new
                {
                    cp = Ansi(l.cp, 10), dpm = Ansi(l.dpm, 10), nno = Ansi(l.nno, 18), na1 = Ansi(l.na1, 40),
                    ty = Ansi(l.ty, 14), fa = Ansi(l.fa, 8), dc = Ansi(l.dc, 8), unit = Ansi(l.unit, 4),
                    jkamount = l.nyk, iprj = l.iprj, iprc = l.iprc, oprc = l.oprc, posi = Ansi(l.pdposi, 10),
                    depotno = Ansi(l.depotno, 10), indate = DateTime.Today, cno = Ansi(paper, 15),
                    gyno = Ansi("PDRK", 10), gyname = Ansi("盘点入库", 60), per = Ansi(per, 6),
                    note = Ansi(l.note, 100), fkfs = Ansi("现金", 10), oprb,
                });
        }
        _ = hasDraft; // 回放场景单次执行恒走 INSERT 分支（row92 L59）
        return paper;
    }

    /// <summary>
    /// 场景 6a：调拨开单（fr_sale Lf_.row511 L197 列集原样 + row298 单头，type='调拨'，指定批次单批出库）。
    /// </summary>
    public static string Transfer(SqlConnection conn, Batch b, decimal qty, string per, string note)
    {
        var paper = GetNewDh(conn, "调拨单", ReplayConfig.Dept, 1);
        conn.Execute(
            """
            INSERT INTO tbsada
                (cp, nno, na1, fa, ty, unit, type, posi, ckamount, oprc, oprct,
                 iprj, iprc, paper, gno, gname, per, xno, tno, cxnno,
                 pnno, pna1, pty, pfa, kcamount, outdate, kcid, depotno, dc, note, dpm)
            VALUES
                (@cp, @nno, @na1, @fa, @ty, @unit, N'调拨', @posi, @qty, @oprc, @oprct,
                 @iprj, @iprc, @paper, @gno, @gname, @per, @xno, @tno, @cxnno,
                 @pnno, @pna1, @pty, @pfa, @kcamount, @outdate, @kcid, @depotno, @dc, @note, @dpm)
            """,
            new
            {
                cp = b.Cp, nno = b.Nno, na1 = b.Na1, fa = b.Fa, ty = b.Ty, unit = b.Unit,
                posi = b.Posi, qty, oprc = b.Oprc, oprct = b.Oprc, iprj = b.Iprj, iprc = b.Iprc,
                paper, gno = ReplayConfig.Gno, gname = ReplayConfig.Gname, per,
                xno = b.Xno, tno = b.Tno, cxnno = b.Cxnno,
                pnno = b.Nno, pna1 = b.Na1, pty = b.Ty, pfa = b.Fa,
                kcamount = b.Kc, outdate = DateTime.Today, kcid = b.Id, depotno = b.Depotno,
                dc = b.Dc, note = Ansi(note, 250), dpm = Ansi(ReplayConfig.Dept, 10),
            });

        // 单头（row298 L287-293）
        conn.Execute(
            """
            IF EXISTS (SELECT 1 FROM tbsada_header WHERE paper = @paper AND dpm = @dpm)
                UPDATE tbsada_header SET spname = @spname WHERE paper = @paper AND dpm = @dpm
            ELSE
                INSERT INTO tbsada_header (paper, dpm, papertype, jptype, spname)
                VALUES (@paper, @dpm, '', N'单据类型', @spname)
            """,
            new { paper, dpm = Ansi(ReplayConfig.Dept, 10), spname = Ansi(ReplayConfig.Gname, 40) });
        return paper;
    }

    /// <summary>
    /// 场景 6b：调仓生成+确认（fr_sadc a_.row150 + DJLSH('调仓单',1) + __.row11 ISAPPE P_WAY=1 原样重放）。
    /// </summary>
    public static string StockAdjust(SqlConnection conn, string dept, Batch source, decimal qty,
        string depotno1, string posi1, string per)
    {
        // 取号（djlsh L945-997：应用层拼号，PASSTH 补 dpm → WHERE djname+dpm）
        var row = conn.QueryFirstOrDefault<(int id, string djjx, int lsh, string dyear)>(
            "SELECT id, RTRIM(djjx), lsh, RTRIM(dyear) FROM tbdjgs WHERE djname = N'调仓单' AND dpm = @dpm",
            new { dpm = Ansi(dept, 10) });
        if (row.id == 0)
            throw new InvalidOperationException("tbdjgs 无「调仓单」前缀行");
        var year = DateTime.Now.Year.ToString();
        var lsh = row.dyear == year ? row.lsh + 1 : 1;
        conn.Execute("UPDATE tbdjgs SET dyear = @year, lsh = @lsh WHERE id = @id",
            new { year = Ansi(year, 4), lsh, id = row.id });
        var paper = row.djjx + year[^2..] + lsh.ToString("D6");

        // 生成明细（a_.row150 L61：源批次字段快照 + 目标仓位）
        conn.Execute(
            """
            INSERT INTO tbsadc
                (cp, dpm, nno, na1, ty, fa, unit, depotno, posi, depotno1, posi1, paper, outdate,
                 ckamount, xno, iprc, oprc, iprj, oprb, per, note, kcid, dc)
            VALUES
                (@cp, @dpm, @nno, @na1, @ty, @fa, @unit, @depotno, @posi, @depotno1, @posi1, @paper, @outdate,
                 @ckamount, @xno, @iprc, @oprc, @iprj, @oprb, @per, @note, @kcid, @dc)
            """,
            new
            {
                cp = Ansi(source.Cp, 10), dpm = Ansi(dept, 10), nno = Ansi(source.Nno, 18),
                na1 = Ansi(source.Na1, 40), ty = Ansi(source.Ty, 14), fa = Ansi(source.Fa, 10),
                unit = Ansi(source.Unit, 4), depotno = Ansi(source.Depotno, 10), posi = Ansi(source.Posi, 10),
                depotno1 = Ansi(depotno1, 10), posi1 = Ansi(posi1, 10), paper = Ansi(paper, 10),
                outdate = DateTime.Today, ckamount = qty, xno = source.Xno, iprc = source.Iprc,
                oprc = source.Oprc, iprj = source.Iprj, oprb = source.Oprb, per = Ansi(per, 10),
                note = Ansi("", 100), kcid = source.Id, dc = Ansi(source.Dc, 8),
            });

        // 确认（__.row11：校验 → ISAPPE ×2 → m='*'）
        var kc = conn.ExecuteScalar<decimal>(
            """
            SELECT ISNULL(SUM(kcamount), 0) FROM tbisto
            WHERE id = @kcid AND cp = @cp AND nno = @nno AND depotno = @depotno AND posi = @posi AND iprc = @iprc
            """,
            new
            {
                kcid = source.Id, cp = Ansi(source.Cp, 10), nno = Ansi(source.Nno, 18),
                depotno = Ansi(source.Depotno, 10), posi = Ansi(source.Posi, 10), iprc = source.Iprc,
            });
        if (kc >= qty)
        {
            var adj = new AdjustRowSnapshot(source.Cp, source.Nno, source.Na1, source.Ty, source.Fa, source.Unit,
                qty, source.Iprc, source.Iprj, source.Oprc, source.Oprb, dept);
            Isappe(conn, adj, source.Depotno, source.Posi, -qty);   // 源减
            Isappe(conn, adj, depotno1, posi1, +qty);               // 目标加（TYPE=1 → posi1/depotno1）
            conn.Execute("UPDATE tbsadc SET m = '*' WHERE paper = @paper", new { paper = Ansi(paper, 10) });
        }
        return paper;
    }

    private sealed record AdjustRowSnapshot(
        string Cp, string Nno, string Na1, string Ty, string Fa, string Unit,
        decimal Ckamount, decimal Iprc, decimal Iprj, decimal Oprc, decimal Oprb, string Dpm);

    /// <summary>ISAPPE P_WAY=1 复刻（pg_proc L1580-1614，调仓分支：tbsadc 行字段 + 指定目标仓位）。</summary>
    private static void Isappe(SqlConnection conn, AdjustRowSnapshot r, string depotno, string posi, decimal amoui)
    {
        var batch = conn.QueryFirstOrDefault<(int id, decimal oprc, decimal oprb)>(
            """
            SELECT TOP (1) id, oprc, oprb FROM tbisto
            WHERE cp = @cp AND nno = @nno AND iprc = @iprc AND posi = @posi AND depotno = @depotno AND dpm = @dpm
            ORDER BY kcamount DESC
            """,
            new
            {
                cp = Ansi(r.Cp, 10), nno = Ansi(r.Nno, 18), iprc = r.Iprc,
                posi = Ansi(posi, 10), depotno = Ansi(depotno, 10), dpm = Ansi(r.Dpm, 10),
            });
        if (batch.id != 0)
        {
            // L1608-1612：kcamount+=Δ、kcbhdate、iprj（tbsadc 行值）、oprc/oprb=库内原值
            conn.Execute(
                "UPDATE tbisto SET kcamount = kcamount + @amoui, kcbhdate = @today, iprj = @iprj, oprc = @oprc, oprb = @oprb WHERE id = @id",
                new { amoui, today = DateTime.Today, iprj = r.Iprj, oprc = batch.oprc, oprb = batch.oprb, id = batch.id });
        }
        else
        {
            // L1602-1606：INSERT 新批次行（cxnno=S_NNO(nno)，oprc=OPRCT 优先，这里 oprct=0 用 oprc）
            var cxnno = Snno(conn, r.Cp, r.Nno);
            conn.Execute(
                """
                INSERT INTO tbisto
                    (dpm, cp, nno, na1, unit, kcamount, ty, fa, cxnno, iprj, iprc, oprc, posi, depotno, note, xno, dc, kcbhdate, oprb)
                VALUES
                    (@dpm, @cp, @nno, @na1, @unit, @kcamount, @ty, @fa, @cxnno, @iprj, @iprc, @oprc, @posi, @depotno, @note, @xno, @dc, @today, @oprb)
                """,
                new
                {
                    dpm = Ansi(r.Dpm, 10), cp = Ansi(r.Cp, 10), nno = Ansi(r.Nno, 18), na1 = Ansi(r.Na1, 40),
                    unit = Ansi(r.Unit, 4), kcamount = amoui, ty = Ansi(r.Ty, 14), fa = Ansi(r.Fa, 8),
                    cxnno = Ansi(cxnno, 18), iprj = r.Iprj, iprc = r.Iprc, oprc = r.Oprc,
                    posi = Ansi(posi, 10), depotno = Ansi(depotno, 10), note = Ansi("", 100),
                    xno = 0, dc = Ansi("", 8), today = DateTime.Today, oprb = r.Oprb,
                });
        }
    }

    /// <summary>S_NNO 复刻（pg_proc L980-990：tbsysset type='cp' 行 cxnno 串号模板映射，生产未配置=''）。</summary>
    private static string Snno(SqlConnection conn, string cp, string nno)
    {
        var template = (conn.ExecuteScalar<string?>(
            "SELECT TOP (1) RTRIM(cxnno) FROM tbsysset WHERE RTRIM(name) = @cp AND type = 'cp'",
            new { cp = Ansi(cp, 10) }) ?? "").Trim();
        var result = "";
        foreach (var ch in template)
        {
            var at = char.IsDigit(ch) ? ch - '0' : ch - 'A' + 10;   // VFP SUBSTR 1-based，越界取''
            result += at >= 1 && at <= nno.Length ? nno[at - 1].ToString() : "";
        }
        return result;
    }

    /// <summary>B2 种子：n 个不同配件的正库存批次（每个配件取 kcamount 最大批次）。</summary>
    public static List<Batch> GetSeedParts(SqlConnection conn, int n)
    {
        var rows = conn.Query<(int id, string cp, string nno, string na1, string ty, string fa,
                string dc, string unit, decimal kc, decimal iprj, decimal iprc, decimal oprc,
                string posi, string depotno, int xno, int tno, string cxnno, decimal oprb)>(
            """
            SELECT TOP (@n) id, RTRIM(cp) cp, RTRIM(nno) nno, RTRIM(na1) na1, RTRIM(ty) ty, RTRIM(fa) fa,
                   RTRIM(dc) dc, RTRIM(unit) unit, kcamount kc, iprj, iprc, oprc,
                   RTRIM(posi) posi, RTRIM(depotno) depotno, xno, tno, RTRIM(cxnnO) cxnno, oprb
            FROM tbisto WITH (NOLOCK)
            WHERE dpm = @dpm AND kcamount > 0
              AND nno IN (SELECT TOP (@n) nno FROM tbisto WITH (NOLOCK) WHERE dpm = @dpm AND kcamount > 0
                          GROUP BY nno ORDER BY MAX(kcamount) DESC)
            AND id IN (SELECT MAX(id) FROM tbisto WITH (NOLOCK) WHERE dpm = @dpm AND kcamount > 0
                       GROUP BY nno)
            ORDER BY kcamount DESC
            """,
            new { n = n, dpm = Ansi(ReplayConfig.Dept) }).ToList();
        return rows.Select(r => new Batch(r.id, r.cp, r.nno, r.na1, r.ty, r.fa, r.dc,
            r.unit, r.kc, r.iprj, r.iprc, r.oprc, r.posi, r.depotno, r.xno, r.tno, r.cxnno, r.oprb)).ToList();
    }

    /// <summary>
    /// 场景 10 种子：订单入库用 tborder 合同行（type='历史'，oqty&gt;qty，含一个代订 gname 行）。
    /// </summary>
    public static void SeedOrderIn(SqlConnection conn, string dept, IReadOnlyList<(Batch part, decimal oqty, string gname, int item)> lines, string cno)
    {
        foreach (var (part, oqty, gname, item) in lines)
        {
            conn.Execute(
                """
                INSERT INTO tborder (cp, dpm, orno, cno, item, nno, na1, ty, fa, dc, unit, oqty, qty, iprj,
                                     getdate, itemc, note, orderdate, type, gyname, gname, fkfs, per, oprc)
                VALUES (@cp, @dpm, @orno, @cno, @item, @nno, @na1, @ty, @fa, @dc, @unit, @oqty, 0, @iprj,
                        @getdate, 0, '', @orderdate, N'历史', '', @gname, N'现金', @per, @oprc)
                """,
                new
                {
                    cp = Ansi(part.Cp, 10), dpm = Ansi(dept, 10), orno = Ansi($"RO{item:D3}", 14),
                    cno = Ansi(cno, 15), item, nno = Ansi(part.Nno, 18), na1 = Ansi(part.Na1, 40),
                    ty = Ansi(part.Ty, 14), fa = Ansi(part.Fa, 8), dc = Ansi(part.Dc, 8),
                    unit = Ansi(part.Unit, 4), oqty, iprj = part.Iprj, getdate = DateTime.Today,
                    orderdate = new DateTime(2026, 1, 1), gname = Ansi(gname, 40),
                    per = Ansi(ReplayConfig.Operator, 10), oprc = part.Oprc,
                });
        }
    }

    /// <summary>场景 10：订单入库整单导入（row156 原样重放：tborder 缺口行 → tbisorsm）。</summary>
    public static void OrderInImport(SqlConnection conn, string dept, string cp, string per, string seano, string cno,
        decimal bl1, decimal bl2, decimal bl3, bool wbtohs, string pFpcw)
    {
        var iprcExpr = wbtohs
            ? "CASE WHEN @bl1 = 0 THEN CAST(0 AS numeric(18,4)) ELSE iprj / @bl1 END"
            : "iprj * @bl1";
        const string cols = """
            item, RTRIM(nno) nno, RTRIM(na1) na1, RTRIM(ty) ty, RTRIM(fa) fa, RTRIM(dc) dc, RTRIM(unit) unit,
            (oqty - qty) zt, iprj, {iprc} iprc, {oprc} oprc, iprj * @bl3 oprb,
            RTRIM(orno) orno, RTRIM(fkfs) fkfs, RTRIM(gyname) gyname, RTRIM(note) note, ISNULL(gname, '') gname
            """;
        List<(int item, string nno, string na1, string ty, string fa, string dc, string unit,
            decimal zt, decimal iprj, decimal iprc, decimal oprc, decimal oprb, string orno,
            string fkfs, string gyname, string note, string gname)> Query(string oprcExpr)
        {
            var sql = cols.Replace("{iprc}", iprcExpr).Replace("{oprc}", oprcExpr);
            return conn.Query<(int item, string nno, string na1, string ty, string fa, string dc, string unit,
                decimal zt, decimal iprj, decimal iprc, decimal oprc, decimal oprb, string orno,
                string fkfs, string gyname, string note, string gname)>(
                $"SELECT {sql} FROM tborder WHERE cno = @cno AND cp = @cp AND oqty > qty AND dpm = @dpm ORDER BY nno, orno",
                new { cno = Ansi(cno, 15), cp = Ansi(cp, 10), dpm = Ansi(dept, 10), bl1, bl2, bl3 }).ToList();
        }

        var orders = Query("oprc");
        if (orders.Count > 0 && string.IsNullOrWhiteSpace(orders[0].gname))
            orders = Query("iprj * @bl2");

        foreach (var o in orders)
        {
            var posi = conn.ExecuteScalar<string?>(
                "SELECT TOP (1) RTRIM(posi) FROM tbisto WHERE nno = @nno AND cp = @cp AND depotno = @depotno " +
                "AND dpm = @dpm AND RTRIM(posi) <> @fpcw",
                new { nno = Ansi(o.nno, 18), cp = Ansi(cp, 10), depotno = Ansi("", 10), dpm = Ansi(dept, 10), fpcw = Ansi(pFpcw, 10) });
            conn.Execute(
                """
                INSERT INTO tbisorsm
                    (pno, seano, box, cno, item, nno, na1, fa, ty, jkamount, fpamount, iprj, iprc, oprc, oprb,
                     posi, depotno, cp, fkfs, gyname, unit, dc, note, indate, per, orno, dpm)
                VALUES
                    (@pno, @seano, @box, @cno, @item, @nno, @na1, @fa, @ty, @jkamount, @fpamount, @iprj, @iprc, @oprc, @oprb,
                     @posi, @depotno, @cp, @fkfs, @gyname, @unit, @dc, @note, @indate, @per, @orno, @dpm)
                """,
                new
                {
                    pno = Ansi("", 12), seano = Ansi(seano, 15), box = Ansi("", 15), cno = Ansi(cno, 15),
                    item = o.item, nno = Ansi(o.nno, 18), na1 = Ansi(o.na1, 40), fa = Ansi(o.fa, 8),
                    ty = Ansi(o.ty, 14), jkamount = o.zt, fpamount = o.zt, iprj = o.iprj, iprc = o.iprc,
                    oprc = o.oprc, oprb = o.oprb, posi = Ansi(posi ?? "", 10), depotno = Ansi("", 10),
                    cp = Ansi(cp, 10), fkfs = Ansi(o.fkfs, 10), gyname = Ansi(o.gyname, 60),
                    unit = Ansi(o.unit, 4), dc = Ansi(o.dc, 8), note = Ansi(o.note, 100),
                    indate = DateTime.Today, per = Ansi(per, 6), orno = Ansi(o.orno, 14), dpm = Ansi(dept, 10),
                });
        }
    }

    /// <summary>
    /// 场景 10：订单入库确认（row220 原样重放，行级事务）：
    /// tbistoed（ISORD='Y'，KCAMOUNT=入库前 sum）→ tbprnoty 缺档建档(uqty=0) →
    /// gname 非空：GETNEWDH('销售单')+INSERT tbsada(kcid 缺省 0) / 否则 ISAPPE →
    /// tborder.qty=MIN(qty+行量,oqty) → DELETE tbisorsm。
    /// </summary>
    public static void OrderInConfirm(SqlConnection conn, string dept, string cp, string per, string seano,
        string pDepotno, out List<string> papers)
    {
        papers = [];
        if (conn.State != ConnectionState.Open) conn.Open();   // RunEx 传入未打开连接（Dapper 查询后自动关闭）
        var today = DateTime.Today;
        // 运行参数（与旧登录装载一致：均价缺省 2）
        var pwayRow = conn.ExecuteScalar<decimal?>("SELECT valuen FROM tbsysset WHERE RTRIM(name) = N'均价' AND type = 'system'");
        var pway = pwayRow is null or < 1 or > 2 ? 2 : (int)pwayRow.Value;
        var wbtohs = conn.ExecuteScalar<bool>("SELECT ISNULL(valuel, 0) FROM tbsysset WHERE RTRIM(name) = N'外币为含税价' AND type = 'system'");
        var xzfa = conn.ExecuteScalar<bool>("SELECT ISNULL(valuel, 0) FROM tbsysset WHERE RTRIM(name) = N'库存是否分产地' AND type = 'system'");

        var rows = conn.Query<(int id, string cp, string cno, int item, string nno, string na1, string ty, string fa,
            string dc, string unit, decimal jkamount, decimal iprj, decimal iprc, decimal oprc, decimal oprb,
            int xno, string posi, string depotno, string box, string seano, string fkfs, string gyname,
            string orno, string note, string gname, decimal freight)>(
            $"""
            SELECT a.id, RTRIM(a.cp) cp, RTRIM(a.cno) cno, a.item, RTRIM(a.nno) nno, RTRIM(a.na1) na1,
                   RTRIM(a.ty) ty, RTRIM(a.fa) fa, RTRIM(a.dc) dc, RTRIM(a.unit) unit, a.jkamount,
                   a.iprj, a.iprc, a.oprc, a.oprb, a.xno, RTRIM(a.posi) posi,
                   depotno = CASE WHEN ISNULL(g.gname, '') <> '' THEN @pDepotno ELSE RTRIM(a.depotno) END,
                   RTRIM(a.box) box, RTRIM(a.seano) seano, RTRIM(a.fkfs) fkfs, RTRIM(a.gyname) gyname,
                   RTRIM(a.orno) orno, RTRIM(a.note) note, ISNULL(g.gname, '') gname, ISNULL(a.Freight, 0) freight
            FROM tbisorsm a
            OUTER APPLY (SELECT TOP (1) o.gname FROM tborder o
                         WHERE o.dpm = @dpm AND o.cno = a.cno AND o.orno = a.orno
                           AND RTRIM(o.type) = '历史' AND o.cp = a.cp AND o.nno = a.nno) g
            WHERE a.seano = @seano AND a.cp = @cp AND a.dpm = @dpm
            ORDER BY ISNULL(g.gname, ''), a.id
            """,
            new { seano = Ansi(seano, 15), cp = Ansi(cp, 10), dpm = Ansi(dept, 10), pDepotno = Ansi(pDepotno, 10) }).ToList();

        string? lastGyname = null, lastSaleGname = null, paper = "", gyno = "";
        foreach (var r in rows)
        {
            using var tx = conn.BeginTransaction();
            try
            {
                var presum = conn.ExecuteScalar<decimal>(
                    "SELECT ISNULL(SUM(kcamount), 0) FROM tbisto WHERE cp = @cp AND nno = @nno AND depotno = @depotno AND dpm = @dpm",
                    new { cp = Ansi(r.cp, 10), nno = Ansi(r.nno, 18), depotno = Ansi(r.depotno, 10), dpm = Ansi(dept, 10) },
                    tx);

                if (r.gyname.Trim() != lastGyname)
                {
                    gyno = conn.ExecuteScalar<string?>(
                        "SELECT TOP (1) RTRIM(gyno) FROM tbgugys WHERE gyname = @gyname",
                        new { gyname = Ansi(r.gyname, 60) }, tx) ?? "";
                    lastGyname = r.gyname.Trim();
                }

                conn.Execute(
                    """
                    INSERT INTO tbistoed
                        (pno, cno, box, seano, isord, cp, nno, na1, ty, fa, unit, dc, item, posi, type, fkfs,
                         gyname, gyno, depotno, jkamount, kcamount, iprj, iprc, oprc, indate, per, Freight, dpm)
                    VALUES
                        (@pno, @cno, @box, @seano, 'Y', @cp, @nno, @na1, @ty, @fa, @unit, @dc, @item, @posi, N'入库', @fkfs,
                         @gyname, @gyno, @depotno, @jkamount, @presum, @iprj, @iprc, @oprc, @indate, @per, @freight, @dpm)
                    """,
                    new
                    {
                        pno = Ansi("", 12), cno = Ansi(r.cno, 15), box = Ansi(r.box, 10), seano = Ansi(r.seano, 15),
                        cp = Ansi(r.cp, 10), nno = Ansi(r.nno, 18), na1 = Ansi(r.na1, 40), ty = Ansi(r.ty, 14),
                        fa = Ansi(r.fa, 8), unit = Ansi(r.unit, 4), dc = Ansi(r.dc, 8), r.item,
                        posi = Ansi(r.posi, 10), fkfs = Ansi(r.fkfs, 10), gyname = Ansi(r.gyname, 60),
                        gyno = Ansi(gyno, 10), depotno = Ansi(r.depotno, 10), r.jkamount, presum,
                        iprj = r.iprj, iprc = r.iprc, oprc = r.oprc, indate = today, per = Ansi(per, 10),
                        freight = r.freight, dpm = Ansi(dept, 10),
                    }, tx);

                var profile = conn.QueryFirstOrDefault<(int id, decimal iprj)>(
                    "SELECT TOP (1) id, iprj FROM tbprnoty WHERE cp = @cp AND nno = @nno",
                    new { cp = Ansi(r.cp, 10), nno = Ansi(r.nno, 18) }, tx);
                if (profile.id == 0)
                {
                    conn.Execute(
                        "INSERT INTO tbprnoty (cp, nno, na1, ty, fa, dc, unit, note, iprj, uqty, cxnno, oprc_cp, oprc_min, cpict) " +
                        "VALUES (@cp, @nno, @na1, @ty, @fa, @dc, @unit, @note, @iprj, 0, @cxnno, 0, 0, '')",
                        new
                        {
                            cp = Ansi(r.cp, 10), nno = Ansi(r.nno, 18), na1 = Ansi(r.na1, 40), ty = Ansi(r.ty, 14),
                            fa = Ansi(r.fa, 8), dc = Ansi(r.dc, 8), unit = Ansi(r.unit, 4), note = Ansi("", 254),
                            iprj = r.iprj, cxnno = Ansi("", 18),
                        }, tx);
                }

                var direct = !string.IsNullOrWhiteSpace(r.gname) && r.jkamount != 0;
                if (direct)
                {
                    if (string.IsNullOrEmpty(paper) || lastSaleGname != r.gname)
                    {
                        paper = GetNewDh(conn, tx, "销售单", dept, 1);
                        papers.Add(paper);
                        lastSaleGname = r.gname;
                    }
                    var tp = conn.QueryFirstOrDefault<(int xno, int tno, string cxnno, string dc)>(
                        "SELECT TOP (1) ISNULL(xno, 0), ISNULL(tno, 0), RTRIM(ISNULL(cxnnO, '')), RTRIM(ISNULL(dc, '')) " +
                        "FROM tbprnoty WHERE cp = @cp AND nno = @nno",
                        new { cp = Ansi(r.cp, 10), nno = Ansi(r.nno, 18) }, tx);
                    var gno = conn.ExecuteScalar<string?>(
                        "SELECT TOP (1) RTRIM(gno) FROM tbgu WHERE gname = @gname",
                        new { gname = Ansi(r.gname, 40) }, tx) ?? "";
                    conn.Execute(
                        """
                        INSERT INTO tbsada
                            (cp, nno, na1, fa, ty, unit, type, posi, ckamount, oprc, oprct, iprj, iprc, paper,
                             gno, gname, per, xno, tno, cxnno, pnno, pna1, pty, pfa, kcamount, outdate, depotno, dc, dpm)
                        VALUES
                            (@cp, @nno, @na1, @fa, @ty, @unit, N'销售', @posi, @jkamount, @oprc, @oprc, @iprj, @iprc, @paper,
                             @gno, @gname, @per, @xno, @tno, @cxnno, @nno, @na1, @ty, @fa, @presum, @outdate, @depotno, @dc, @dpm)
                        """,
                        new
                        {
                            cp = Ansi(r.cp, 10), nno = Ansi(r.nno, 18), na1 = Ansi(r.na1, 40), fa = Ansi(r.fa, 8),
                            ty = Ansi(r.ty, 14), unit = Ansi(r.unit, 4), posi = Ansi(r.posi, 10), r.jkamount,
                            oprc = r.oprc, iprj = r.iprj, iprc = r.iprc, paper = Ansi(paper, 15),
                            gno = Ansi(gno, 10), gname = Ansi(r.gname, 40), per = Ansi(per, 10),
                            xno = tp.xno, tno = tp.tno, cxnno = Ansi(tp.cxnno, 18), presum, outdate = today,
                            depotno = Ansi(r.depotno, 10), dc = Ansi(tp.dc, 8), dpm = Ansi(dept, 10),
                        }, tx);
                }
                else
                {
                    LegacyIsappe(conn, tx, dept, r.cp, r.nno, r.na1, r.ty, r.fa, r.unit,
                        r.iprj, r.iprc, r.oprc, r.oprb, r.posi, r.depotno, r.note, r.xno, r.dc,
                        r.jkamount, pway, wbtohs, xzfa, today);
                }

                var orderWhere = "cp = @cp AND cno = @cno AND nno = @nno AND dpm = @dpm" +
                                 (string.IsNullOrWhiteSpace(r.orno) ? "" : " AND orno = @orno");
                var order = conn.QueryFirstOrDefault<(int id, decimal oqty, decimal qty)>(
                    $"SELECT TOP (1) id, oqty, qty FROM tborder WHERE {orderWhere}",
                    new { cp = Ansi(r.cp, 10), cno = Ansi(r.cno, 15), nno = Ansi(r.nno, 18), dpm = Ansi(dept, 10), orno = Ansi(r.orno, 14) }, tx);
                if (order.id != 0)
                    conn.Execute("UPDATE tborder SET qty = @qty WHERE id = @id",
                        new { qty = Math.Min(order.qty + r.jkamount, order.oqty), id = order.id }, tx);

                conn.Execute("DELETE FROM tbisorsm WHERE id = @id AND dpm = @dpm",
                    new { r.id, dpm = Ansi(dept, 10) }, tx);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    /// <summary>ISAPPE 原样重放（pg_proc_client L620-728，P_WAY 运行时；与新系统 IsappeEngine 同契约独立实现）。</summary>
    private static void LegacyIsappe(SqlConnection conn, SqlTransaction tx, string dpm, string cp, string nno,
        string na1, string ty, string fa, string unit, decimal iprj, decimal iprc, decimal oprc, decimal oprb,
        string posi, string depotno, string note, int xno, string dc, decimal amount, int pway, bool wbtohs,
        bool xzfa, DateTime today)
    {
        var faFilter = xzfa ? " AND fa = @fa" : "";
        var (sql, args) = pway switch
        {
            1 => ($"SELECT TOP (1) id, kcamount, iprj, iprc, oprc, oprb FROM tbisto WHERE cp = @cp AND nno = @nno " +
                  $"AND iprc = @iprc AND posi = @posi AND depotno = @depotno AND dpm = @dpm{faFilter}",
                  (object)new { cp = Ansi(cp, 10), nno = Ansi(nno, 18), iprc, fa = Ansi(fa, 8),
                                posi = Ansi(posi, 10), depotno = Ansi(depotno, 10), dpm = Ansi(dpm, 10) }),
            _ => ($"SELECT TOP (1) id, kcamount, iprj, iprc, oprc, oprb FROM tbisto WHERE cp = @cp AND nno = @nno " +
                  $"AND posi = @posi AND depotno = @depotno AND dpm = @dpm{faFilter}",
                  (object)new { cp = Ansi(cp, 10), nno = Ansi(nno, 18), fa = Ansi(fa, 8),
                                posi = Ansi(posi, 10), depotno = Ansi(depotno, 10), dpm = Ansi(dpm, 10) }),
        };
        var batch = conn.QueryFirstOrDefault<(int id, decimal kc, decimal iprj, decimal iprc, decimal oprc, decimal oprb)>(sql, args, tx);
        var effOprc = batch.id != 0 ? batch.oprc : oprc;

        if (batch.id != 0)
        {
            var kcAfter = batch.kc + amount;
            decimal newIprc, newIprj;
            if (pway == 2 && kcAfter != 0)
            {
                newIprc = Math.Round((Math.Max(batch.iprc * batch.kc, 0) + iprc * amount) / (Math.Max(batch.kc, 0) + amount), 4);
                newIprj = wbtohs
                    ? Math.Round((batch.iprj * batch.kc + iprj * amount) / (batch.kc + amount), 2)
                    : batch.iprj;
            }
            else
            {
                newIprc = batch.iprc;
                newIprj = batch.iprj;
            }
            conn.Execute(
                "UPDATE tbisto SET kcamount = kcamount + @amount, kcbhdate = @today, iprj = @iprj, oprc = @oprc, oprb = @oprb, iprc = @iprc WHERE id = @id",
                new { amount, today, iprj = newIprj, oprc = effOprc, oprb = batch.oprb, iprc = newIprc, id = batch.id }, tx);
            return;
        }

        var cxnno = Snno(conn, cp, nno);
        conn.Execute(
            """
            INSERT INTO tbisto
                (dpm, cp, nno, na1, unit, kcamount, ty, fa, cxnno, iprj, iprc, oprc, posi, depotno, note, xno, dc, kcbhdate, oprb)
            VALUES
                (@dpm, @cp, @nno, @na1, @unit, @kcamount, @ty, @fa, @cxnno, @iprj, @iprc, @oprc, @posi, @depotno, @note, @xno, @dc, @today, @oprb)
            """,
            new
            {
                dpm = Ansi(dpm, 10), cp = Ansi(cp, 10), nno = Ansi(nno, 18), na1 = Ansi(na1, 40),
                unit = Ansi(unit, 4), kcamount = amount, ty = Ansi(ty, 14), fa = Ansi(fa, 8),
                cxnno = Ansi(cxnno, 18), iprj, iprc, oprc = effOprc,
                posi = Ansi(posi, 10), depotno = Ansi(depotno, 10), note = Ansi(note, 100),
                xno, dc = Ansi(dc, 8), today, oprb,
            }, tx);
    }

    private static DbString Ansi(string value, int length = 30) =>
        new() { Value = value, IsAnsi = true, Length = length };

    /// <summary>匿名参数对象转字典并合并附加参数（tbxzb 变量注入用）。</summary>
    private static Dictionary<string, object?> MergeWith(this object anon, Dictionary<string, object?> extra)
    {
        var d = new Dictionary<string, object?>();
        foreach (var p in anon.GetType().GetProperties())
            d[p.Name] = p.GetValue(anon);
        foreach (var kv in extra) d[kv.Key] = kv.Value;
        return d;
    }

    /// <summary>
    /// 场景 11/12 种子：日营业统计用销售单（type='销售'，mark=''，kcid=0 不动库存）+ 入库流水 + 单头 spname。
    /// </summary>
    public static void SeedDailyStat(SqlConnection conn, string dept, DateTime date, string per, string cno)
    {
        var d = date.Date;
        // 销售组 1：挂帐（写 tbysyf）+ KH 差额行
        conn.Execute(
            """
            INSERT INTO tbsada (cp, nno, na1, ty, unit, type, ckamount, oprc, oprct, iprj, iprc, paper, gno,
                                gname, per, pnno, pna1, pty, pfa, kcamount, outdate, dpm, yhl, pno, per1, guno, kcid, mark)
            VALUES (N'中驭', N'REPLAYSEED1', N'回放统计配件', N'', N'', N'销售', 2, 100, 90, 60, 50, @paper, @gno,
                    @gname, @per, N'REPLAYSEED1', N'回放统计配件', N'', N'', 0, @outdate, @dpm, 5, N'PNO1', @per, N'挂帐', 0, '')
            """,
            new { paper = Ansi("REPLAYXS1", 15), gno = Ansi(ReplayConfig.Gno, 10), gname = Ansi(ReplayConfig.Gname, 40),
                   per = Ansi(per, 10), outdate = d, dpm = Ansi(dept, 10) });
        // 销售组 2：现金（不写 tbysyf，仅 type/jzdate 翻转）
        conn.Execute(
            """
            INSERT INTO tbsada (cp, nno, na1, ty, unit, type, ckamount, oprc, oprct, iprj, iprc, paper, gno,
                                gname, per, pnno, pna1, pty, pfa, kcamount, outdate, dpm, yhl, pno, per1, guno, kcid, mark)
            VALUES (N'中驭', N'REPLAYSEED2', N'回放统计配件二', N'', N'', N'销售', 1, 200, 200, 120, 100, @paper, @gno,
                    @gname, @per, N'REPLAYSEED2', N'回放统计配件二', N'', N'', 0, @outdate, @dpm, 0, N'PNO2', @per, N'现金', 0, '')
            """,
            new { paper = Ansi("REPLAYXS2", 15), gno = Ansi(ReplayConfig.Gno, 10), gname = Ansi(ReplayConfig.Gname, 40),
                   per = Ansi(per, 10), outdate = d, dpm = Ansi(dept, 10) });
        // 单头 spname
        foreach (var paper in new[] { "REPLAYXS1", "REPLAYXS2" })
            conn.Execute(
                "INSERT INTO tbsada_header (paper, dpm, papertype, jptype, spname) VALUES (@paper, @dpm, '', N'单据类型', @spname)",
                new { paper = Ansi(paper, 15), dpm = Ansi(dept, 10), spname = Ansi(ReplayConfig.Gname, 40) });
        // 入库流水（挂帐 → 写 tbysyf 应付）
        conn.Execute(
            """
            INSERT INTO tbistoed (cp, dpm, nno, na1, ty, unit, jkamount, kcamount, iprj, iprc, oprc, indate,
                                  cno, seano, isord, pno, gyname, per, type, mark, fkfs)
            VALUES (N'中驭', @dpm, N'REPLAYSEED1', N'回放统计配件', N'', N'', 3, 0, 60, 50, 80, @indate,
                    @cno, @seano, N'N', N'PNORK', @gyname, @per, N'入库', '', N'挂帐')
            """,
            new { dpm = Ansi(dept, 10), indate = d, cno = Ansi(cno, 15), seano = Ansi("REPLAYRK1", 15),
                   gyname = Ansi(ReplayConfig.Gname, 60), per = Ansi(per, 10) });
    }

    /// <summary>场景 11：日营业统计确认（fr_rtj row5 原样重放，单事务）。</summary>
    public static void DailyStatPost(SqlConnection conn, string dept, string per, DateTime date)
    {
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        var d = date.Date;
        var d2 = d.AddDays(1);
        var ifrtjNoJj = conn.ExecuteScalar<bool>("SELECT ISNULL(CAST(valuel AS bit), 0) FROM tbsysset WHERE RTRIM(name) = N'日统计不统计急件入库' AND type = 'system'");
        var ifrtjNoQr = conn.ExecuteScalar<bool>("SELECT ISNULL(CAST(valuel AS bit), 0) FROM tbsysset WHERE RTRIM(name) = N'不统计财务未确认' AND type = 'system'");
        var wbtohs = conn.ExecuteScalar<bool>("SELECT ISNULL(CAST(valuel AS bit), 0) FROM tbsysset WHERE RTRIM(name) = N'外币为含税价' AND type = 'system'");
        var tjJj = ifrtjNoJj ? " AND cp <> N'急件'" : "";
        var tjWqr = ifrtjNoQr ? " AND mark <> ''" : "";

        using var tx = conn.BeginTransaction();
        try
        {
            var groups = conn.Query<(string paper, string gname, string guno, string pno, string per1)>(
                $"""
                SELECT DISTINCT RTRIM(paper), RTRIM(ISNULL(gname, '')), RTRIM(ISNULL(guno, '')),
                       RTRIM(ISNULL(pno, '')), RTRIM(ISNULL(per1, ''))
                FROM tbsada WHERE outdate >= @d AND outdate < @d2 AND type = N'销售' AND dpm = @dpm{tjWqr} ORDER BY 1
                """,
                new { d, d2, dpm = Ansi(dept, 10) }, tx).ToList();

            foreach (var g in groups)
            {
                var agg = conn.QueryFirstOrDefault<(decimal pt, decimal ptsj)>(
                    """
                    SELECT ROUND(SUM(oprc * (100 - ISNULL(yhl, 0)) / 100.0 * ckamount), 2),
                           ROUND(SUM(oprct * (100 - ISNULL(yhl, 0)) / 100.0 * ckamount), 2)
                    FROM tbsada WHERE type = N'销售' AND outdate >= @d AND outdate < @d2 AND guno = @guno
                      AND paper = @paper AND pno = @pno AND dpm = @dpm AND mark = ''
                    """,
                    new { d, d2, guno = Ansi(g.guno, 10), paper = Ansi(g.paper, 15), pno = Ansi(g.pno, 12), dpm = Ansi(dept, 10) }, tx);
                if (agg.pt == 0) continue;
                var spname = conn.ExecuteScalar<string?>(
                    "SELECT TOP (1) ISNULL(spname, '') FROM tbsada_header WHERE paper = @paper",
                    new { paper = Ansi(g.paper, 15) }, tx) ?? "";
                if (g.guno is "汇款" or "托收" or "挂帐")
                {
                    var prev = conn.QueryFirstOrDefault<(decimal cb, decimal tick, decimal yjb)>(
                        "SELECT TOP (1) ISNULL(cb, 0), ISNULL(tick, 0), ISNULL(yjb, 0) FROM tbysyf WHERE gname = @gname ORDER BY datime",
                        new { gname = Ansi(g.gname, 40) }, tx);
                    conn.Execute(
                        "INSERT INTO tbysyf (gname, datime, cno, ys, pno, fkfs, type, cb, yjb, tick, xm, spname, dpm) " +
                        "VALUES (@gname, @datime, @cno, @ys, @pno, @fkfs, N'销售', @cb, @yjb, @tick, @xm, @spname, @dpm)",
                        new { gname = Ansi(g.gname, 40), datime = d, cno = Ansi(g.paper, 15), ys = agg.pt,
                               pno = Ansi((g.pno + " " + g.per1).Trim(), 12), fkfs = Ansi(g.guno, 10),
                               cb = prev.cb, yjb = prev.yjb, tick = prev.tick, xm = Ansi(g.guno, 10),
                               spname = Ansi(spname, 40), dpm = Ansi(dept, 10) }, tx);
                    if (agg.ptsj - agg.pt != 0)
                        conn.Execute(
                            "INSERT INTO tbysyf (gname, datime, cno, ys, pno, fkfs, type, cb, yjb, tick, xm, spname, dpm) " +
                            "VALUES (@gname, @datime, @cno, @ys, N'KH', @fkfs, N'销售', @cb, @yjb, @tick, @xm, @spname, @dpm)",
                            new { gname = Ansi(g.gname, 40), datime = d, cno = Ansi(g.paper, 15), ys = agg.ptsj - agg.pt,
                                   fkfs = Ansi(g.guno, 10), cb = prev.cb, yjb = prev.yjb, tick = prev.tick,
                                   xm = Ansi(g.guno, 10), spname = Ansi(spname, 40), dpm = Ansi(dept, 10) }, tx);
                }
            }

            conn.Execute($"UPDATE tbsada SET type = N'历史' WHERE outdate >= @d AND outdate < @d2 AND type = N'销售' AND dpm = @dpm{tjWqr}",
                new { d, d2, dpm = Ansi(dept, 10) }, tx);
            conn.Execute(
                """
                UPDATE tbsada SET jzdate = '1900-01-01'
                WHERE outdate >= @d AND outdate < @d2 AND type = N'历史'
                  AND (guno = N'汇款' OR guno = N'托收' OR guno = N'挂帐') AND dpm = @dpm
                  AND paper NOT IN (SELECT cno FROM tbysyf WHERE datime >= @d AND datime < @d2
                                    AND type = N'销售' AND fkfs <> '' AND skmoney <> 0)
                """,
                new { d, d2, dpm = Ansi(dept, 10) }, tx);
            conn.Execute(
                """
                UPDATE tbsada SET jzdate = @d
                WHERE outdate >= @d AND outdate < @d2 AND type = N'历史'
                  AND (guno <> N'汇款' AND guno <> N'托收' AND guno <> N'挂帐') AND dpm = @dpm
                """,
                new { d, d2, dpm = Ansi(dept, 10) }, tx);

            var inGroups = conn.Query<(string seano, string gyname, string fkfs, string pno, string per, string isord)>(
                $"""
                SELECT DISTINCT RTRIM(seano), RTRIM(ISNULL(gyname, '')), RTRIM(ISNULL(fkfs, '')),
                       RTRIM(ISNULL(pno, '')), RTRIM(ISNULL(per, '')), RTRIM(ISNULL(isord, ''))
                FROM tbistoed WHERE indate >= @d AND indate < @d2 AND type = N'入库' AND dpm = @dpm{tjJj} ORDER BY 1
                """,
                new { d, d2, dpm = Ansi(dept, 10) }, tx).ToList();
            foreach (var g in inGroups)
            {
                var fkfs = string.IsNullOrWhiteSpace(g.fkfs) ? "入库" : g.fkfs;
                var agg = conn.QueryFirstOrDefault<(decimal pt, decimal freight)>(
                    $"""
                    SELECT {(wbtohs ? "ROUND(SUM(iprj * jkamount), 2)" : "ROUND(SUM(CASE WHEN ISNULL(freight, 0) = 0 THEN iprc ELSE freight END * jkamount), 2)")},
                           SUM((iprc - CASE WHEN ISNULL(freight, 0) = 0 THEN iprc ELSE freight END) * jkamount)
                    FROM tbistoed WHERE type = N'入库' AND indate >= @d AND indate < @d2 AND isord = @isord
                      AND seano = @seano AND gyname = @gyname AND dpm = @dpm AND per = @per AND pno = @pno{tjJj} AND mark = ''
                    """,
                    new { d, d2, isord = Ansi(g.isord, 10), seano = Ansi(g.seano, 15), gyname = Ansi(g.gyname, 60),
                           dpm = Ansi(dept, 10), per = Ansi(g.per, 10), pno = Ansi(g.pno, 12) }, tx);
                if (agg.pt == 0) continue;
                if (fkfs == "挂帐" && !string.IsNullOrWhiteSpace(g.gyname))
                {
                    var prev = conn.QueryFirstOrDefault<(decimal cb, decimal tick, decimal yjb)>(
                        "SELECT TOP (1) ISNULL(cb, 0), ISNULL(tick, 0), ISNULL(yjb, 0) FROM tbysyf WHERE gname = @gname ORDER BY datime",
                        new { gname = Ansi(g.gyname, 40) }, tx);
                    var xm = agg.freight == 0 ? fkfs : fkfs + "有运费：" + agg.freight.ToString("F2").PadLeft(10);
                    conn.Execute(
                        "INSERT INTO tbysyf (gname, datime, cno, yf, pno, fkfs, type, cb, yjb, tick, xm, isord, dpm) " +
                        "VALUES (@gname, @datime, @cno, @yf, @pno, @fkfs, N'入库', @cb, @yjb, @tick, @xm, @isord, @dpm)",
                        new { gname = Ansi(g.gyname, 40), datime = d, cno = Ansi(g.seano, 15), yf = agg.pt,
                               pno = Ansi((g.pno + " " + g.per).Trim(), 12), fkfs = Ansi(fkfs, 10),
                               cb = prev.cb, yjb = prev.yjb, tick = prev.tick, xm = Ansi(xm, 10),
                               isord = Ansi(g.isord, 10), dpm = Ansi(dept, 10) }, tx);
                }
            }
            conn.Execute($"UPDATE tbistoed SET type = N'历史' WHERE indate >= @d AND indate < @d2 AND type = N'入库' AND dpm = @dpm{tjJj}",
                new { d, d2, dpm = Ansi(dept, 10) }, tx);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>场景 12：恢复未统计（fr_hftj 0_.row5 原样重放，单事务）。</summary>
    public static void DailyStatUnpost(SqlConnection conn, string dept, string per, DateTime date)
    {
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        var d = date.Date;
        var d2 = d.AddDays(1);
        using var tx = conn.BeginTransaction();
        try
        {
            var yjEnabled = conn.ExecuteScalar<bool>(
                "SELECT ISNULL(CAST(valuel AS bit), 0) FROM tbsysset WHERE RTRIM(name) = N'启用财务月结' AND type = 'system'", transaction: tx);
            if (yjEnabled)
            {
                var yjDate = conn.ExecuteScalar<DateTime?>("SELECT MAX(yjdate) FROM tbyj", transaction: tx);
                if (yjDate.HasValue && yjDate.Value.Date.AddDays(1) > d)
                    throw new InvalidOperationException("已做月结，不能恢复统计");
            }
            conn.Execute(
                """
                UPDATE tbsada SET type = N'销售'
                WHERE outdate >= @d AND outdate < @d2 AND type = N'历史' AND dpm = @dpm
                  AND (guno = N'挂帐冲预收' OR paper NOT IN
                       (SELECT cno FROM tbysyf WHERE datime = @d AND type = N'销售' AND fkfs <> '' AND skmoney <> 0))
                """,
                new { d, d2, dpm = Ansi(dept, 10) }, tx);
            conn.Execute(
                """
                UPDATE tbistoed SET type = N'入库'
                WHERE indate >= @d AND indate < @d2 AND type = N'历史' AND dpm = @dpm
                  AND seano NOT IN (SELECT cno FROM tbysyf WHERE datime = @d AND type = N'入库' AND fkfs <> '' AND skmoney <> 0)
                """,
                new { d, d2, dpm = Ansi(dept, 10) }, tx);
            conn.Execute(
                """
                DELETE FROM tbysyf
                WHERE datime >= @d AND type = N'销售' AND skmoney = 0 AND dpm = @dpm
                  AND cno IN (SELECT DISTINCT paper FROM tbsada WHERE type = N'销售' AND mark = ''
                              AND outdate >= @d AND outdate < @d2 AND dpm = @dpm)
                """,
                new { d, d2, dpm = Ansi(dept, 10) }, tx);
            conn.Execute(
                """
                DELETE FROM tbysyf
                WHERE datime >= @d AND datime < @d2 AND type = N'入库' AND skmoney = 0 AND dpm = @dpm
                  AND cno IN (SELECT DISTINCT seano FROM tbistoed WHERE type = N'入库' AND mark = ''
                              AND indate >= @d AND indate < @d2 AND dpm = @dpm)
                """,
                new { d, d2, dpm = Ansi(dept, 10) }, tx);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // =================================================================================
    // 三期（本批）：入库退货 / 提货管理 / 订单族（fr_istk / fr_th / fr_order3 / fr_order4）
    // 契约出处：开发文档/契约摘录/{入库退货fr_istk,提货管理fr_th,订货库fr_order3,订单生成fr_order4}契约.md
    // 时间归一：fhdate/indate 等含时刻列消费方仅用日期 → 两侧统一 DateTime.Today（§7 约定）。
    // =================================================================================

    /// <summary>P_WAY 运行时（tbsysset「均价」，缺省 2——IsappeEngine.LoadParams 同口径）。</summary>
    public static int GetPWay(SqlConnection conn)
    {
        var v = conn.ExecuteScalar<decimal?>(
            "SELECT valuen FROM tbsysset WHERE type = 'system' AND name = N'均价'");
        return v is null or < 1 or > 2 ? 2 : (int)v.Value;
    }

    /// <summary>选一条可退入库流水（两侧同源种子：dpm 内 type='入库'、jkamount>0、最新优先）。</summary>
    public static (int id, string cno, string cp, string nno, string na1, string ty, string fa, string dc,
        string unit, string posi, string depotno, string pno, string gyno, string gyname, string note,
        string cxnno, string ywr, decimal jkamount, decimal ro, decimal iprc, decimal iprj, decimal oprc, int item)
        GetReturnable(SqlConnection conn, string dpm)
    {
        return conn.QueryFirst<(int, string, string, string, string, string, string, string, string,
            string, string, string, string, string, string, string, string, decimal, decimal, decimal, decimal, decimal, int)>(
            """
            SELECT TOP 1 id, RTRIM(cno) cno, RTRIM(cp) cp, RTRIM(nno) nno, RTRIM(na1) na1, RTRIM(ty) ty,
                   RTRIM(fa) fa, RTRIM(dc) dc, RTRIM(unit) unit, RTRIM(posi) posi, RTRIM(depotno) depotno,
                   RTRIM(pno) pno, RTRIM(gyno) gyno, RTRIM(gyname) gyname, RTRIM(note) note,
                   RTRIM(cxnno) cxnno, RTRIM(ywr) ywr, jkamount, ro, iprc, iprj, oprc, item
            FROM tbistoed e WITH (NOLOCK)
            WHERE e.dpm = @dpm AND e.type = N'入库' AND e.jkamount > 0 AND e.ro = 0
              AND EXISTS (SELECT 1 FROM tbisto b WITH (NOLOCK)
                          WHERE b.cp = e.cp AND b.nno = e.nno AND b.dpm = e.dpm
                            AND b.depotno = e.depotno AND b.kcamount > 0)
            ORDER BY e.id DESC
            """,
            new { dpm = Ansi(dpm, 10) });
    }

    /// <summary>ISAPPE 主程序库版（pg_proc L1546-1668）冲减：返回冲减前批次库存（RETURN NRT）；未命中抛异常。</summary>
    private static decimal IsappeMain(SqlConnection conn, SqlTransaction tx, string dpm, string cp, string nno,
        decimal iprc, decimal iprj, string posi, string depotno, decimal amoui, int pway, bool xzfa)
    {
        var faFilter = xzfa ? " AND fa = (SELECT TOP 1 fa FROM tbisto WHERE cp = @cp AND nno = @nno)" : "";
        var matchSql = pway == 1
            ? "SELECT TOP 1 id, kcamount, oprc, oprb FROM tbisto WITH (UPDLOCK) WHERE cp = @cp AND nno = @nno AND iprc = @iprc AND posi = @posi AND depotno = @depotno AND dpm = @dpm" + faFilter + " ORDER BY kcamount DESC"
            : "SELECT TOP 1 id, kcamount, oprc, oprb FROM tbisto WITH (UPDLOCK) WHERE cp = @cp AND nno = @nno AND posi = @posi AND depotno = @depotno AND dpm = @dpm" + faFilter + " ORDER BY kcamount DESC";
        var batch = conn.QueryFirstOrDefault<(int id, decimal kc, decimal oprc, decimal oprb)>(
            matchSql,
            new { cp = Ansi(cp, 10), nno = Ansi(nno, 18), iprc, posi = Ansi(posi, 10), depotno = Ansi(depotno, 10), dpm = Ansi(dpm, 10) },
            tx);
        if (batch.id == 0) throw new InvalidOperationException("ISAPPE 未命中批次");
        // L1612/L1652（M_NOUPOPRC=.T. → 不写 oprc/oprb；本就取库内原值，省略等价）
        conn.Execute(
            "UPDATE tbisto SET kcamount = kcamount + @amoui, kcbhdate = @today, iprj = @iprj, iprc = CASE WHEN @pway = 2 AND (kcamount + @amoui) <> 0 THEN ROUND((CASE WHEN kcamount > 0 THEN iprc * kcamount ELSE 0 END + @iprc * @amoui) / (CASE WHEN kcamount > 0 THEN kcamount ELSE 0 END + @amoui), 4) ELSE CASE WHEN @pway = 2 THEN @iprc ELSE iprc END END WHERE id = @id",
            new { amoui, today = DateTime.Today, iprj, pway, id = batch.id, iprc }, tx);
        return batch.kc;
    }

    /// <summary>
    /// 场景 13 旧侧：入库退货确认（fr_istk 嗟.row132:L819-849 序列原样）。
    /// 取号 GETNEWDH('散件入库单') → UPDATE ro → 定位仓位 → INSERT 负数流水（ro=源行 id）→
    /// ISAPPE（主程序库版，M_NOUPOPRC=.T.）→ 回写 kcamount=冲减前/iprc=编辑值。
    /// </summary>
    public static string PurchaseReturn(SqlConnection conn, string dept, string per, int sourceId, decimal amount,
        decimal iprj, decimal lineIprc, string posi, string depotno, string fkfs, string reason)
    {
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        var pway = GetPWay(conn);
        var src = conn.QueryFirst<(string cp, string nno, string na1, string ty, string fa, string dc, string unit,
            string posi, string depotno, string pno, string gyno, string gyname, string note, string cxnno,
            string ywr, decimal jkamount, decimal ro, decimal iprc, int item)>(
            """
            SELECT RTRIM(cp) cp, RTRIM(nno) nno, RTRIM(na1) na1, RTRIM(ty) ty, RTRIM(fa) fa, RTRIM(dc) dc,
                   RTRIM(unit) unit, RTRIM(posi) posi, RTRIM(depotno) depotno, RTRIM(pno) pno,
                   RTRIM(gyno) gyno, RTRIM(gyname) gyname, RTRIM(note) note, RTRIM(cxnno) cxnno,
                   RTRIM(ywr) ywr, jkamount, ro, iprc, item
            FROM tbistoed WITH (NOLOCK) WHERE id = @id
            """,
            new { id = sourceId });
        var xzfa = conn.ExecuteScalar<bool>("SELECT ISNULL(valuel, 0) FROM tbsysset WHERE type = 'system' AND name = N'库存是否分产地'");

        // 仓位定位（L834-840；PASSTH 追加 dpm）
        var foundPosi = conn.ExecuteScalar<string?>(
            "SELECT TOP 1 RTRIM(posi) FROM tbisto WITH (NOLOCK) WHERE cp = @cp AND nno = @nno AND posi = @posi AND depotno = @depotno AND kcamount >= @amount AND dpm = @dpm",
            new { cp = Ansi(src.cp, 10), nno = Ansi(src.nno, 18), posi = Ansi(posi, 10), depotno = Ansi(depotno, 10), amount, dpm = Ansi(dept, 10) });
        if (string.IsNullOrEmpty(foundPosi))
            foundPosi = conn.ExecuteScalar<string?>(
                "SELECT TOP 1 RTRIM(posi) FROM tbisto WITH (NOLOCK) WHERE cp = @cp AND nno = @nno AND depotno = @depotno AND kcamount >= @amount AND dpm = @dpm",
                new { cp = Ansi(src.cp, 10), nno = Ansi(src.nno, 18), depotno = Ansi(depotno, 10), amount, dpm = Ansi(dept, 10) });
        var finalPosi = string.IsNullOrEmpty(foundPosi) ? posi : foundPosi;

        var cno = GetNewDh(conn, "散件入库单", dept, 1);
        var insertIprc = pway == 1 ? src.iprc : lineIprc;
        var note = src.note + (string.IsNullOrWhiteSpace(reason) ? "" : $"[退货原因：{reason}]");

        using var tx = conn.BeginTransaction();
        try
        {
            conn.Execute("UPDATE tbistoed SET ro = ro + @amount WHERE id = @id",
                new { amount, id = sourceId }, tx);
            var newId = conn.ExecuteScalar<int>(
                """
                INSERT INTO tbistoed
                    (CP, NNO, NA1, TY, FA, DC, UNIT, JKAMOUNT, KCAMOUNT, IPRJ, IPRC, OPRC, POSI, INDATE, ITEM,
                     CNO, BOX, SEANO, PNO, GYNO, GYNAME, PER, NOTE, CXNNO, FKFS, DEPOTNO, TYPE, ro, ywr, dpm)
                VALUES
                    (@cp, @nno, @na1, @ty, @fa, @dc, @unit, @jkamount, 0, @iprj, @iprc, @oprc, @posi, @indate, @item,
                     @cno, @box, @seano, @pno, @gyno, @gyname, @per, @note, @cxnno, @fkfs, @depotno, N'入库', @ro, @ywr, @dpm);
                SELECT CAST(SCOPE_IDENTITY() AS int);
                """,
                new
                {
                    cp = Ansi(src.cp, 10), nno = Ansi(src.nno, 18), na1 = Ansi(src.na1, 40),
                    ty = Ansi(src.ty, 14), fa = Ansi(src.fa, 8), dc = Ansi(src.dc, 8), unit = Ansi(src.unit, 4),
                    jkamount = -amount, iprj, iprc = insertIprc, oprc = src.iprc,
                    posi = Ansi(finalPosi, 10), indate = DateTime.Today, item = src.item,
                    cno = Ansi(cno, 15), box = Ansi("", 10), seano = Ansi(cno, 15),
                    pno = Ansi(src.pno, 12), gyno = Ansi(src.gyno, 10), gyname = Ansi(src.gyname, 60),
                    per = Ansi(per, 10), note = Ansi(note, 100), cxnno = Ansi(src.cxnno, 18),
                    fkfs = Ansi(fkfs, 10), depotno = Ansi(depotno, 10), ro = (decimal)sourceId,
                    ywr = Ansi(src.ywr, 10), dpm = Ansi(dept, 10),
                }, tx);
            var kcBefore = IsappeMain(conn, tx, dept, src.cp, src.nno, insertIprc, iprj, finalPosi, depotno,
                -amount, pway, xzfa);
            conn.Execute("UPDATE tbistoed SET kcamount = @kc, iprc = @iprc WHERE id = @id",
                new { kc = kcBefore, iprc = lineIprc, id = newId }, tx);
            tx.Commit();
            return cno;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>场景 14 种子：一张 mark='Y' 已确认销售单两行（tbsada per=REPLAY 进固定窗口）。</summary>
    public static void SeedDeliveryOrder(SqlConnection conn, string dept, string per, string paper,
        IReadOnlyList<Batch> parts)
    {
        for (var i = 0; i < parts.Count; i++)
        {
            var b = parts[i];
            conn.Execute(
                """
                INSERT INTO tbsada
                    (cp, nno, na1, fa, ty, unit, type, posi, ckamount, oprc, oprct, iprj, iprc,
                     paper, gno, gname, per, outdate, dpm, depotno, kcid, mark, guno, yhl, remark)
                VALUES
                    (@cp, @nno, @na1, @fa, @ty, @unit, N'销售', @posi, @qty, @oprc, @oprct, @iprj, @iprc,
                     @paper, @gno, @gname, @per, @outdate, @dpm, @depotno, @kcid, 'Y', N'回放收银', 0, '')
                """,
                new
                {
                    cp = b.Cp, nno = b.Nno, na1 = b.Na1, fa = b.Fa, ty = b.Ty, unit = b.Unit, posi = b.Posi,
                    qty = 2m + i, oprc = b.Oprc, oprct = b.Oprc, iprj = b.Iprj, iprc = b.Iprc,
                    paper = Ansi(paper, 10), gno = ReplayConfig.Gno, gname = ReplayConfig.Gname,
                    per = Ansi(per, 10), outdate = DateTime.Today, dpm = Ansi(dept), depotno = b.Depotno,
                    kcid = b.Id,
                });
        }
    }

    private static List<int> DeliveryLineIds(SqlConnection conn, SqlTransaction tx, string paper)
        => conn.Query<int>("SELECT id FROM tbsada WITH (NOLOCK) WHERE paper = @paper ORDER BY id",
            new { paper = Ansi(paper, 10) }, tx).ToList();

    /// <summary>场景 14 旧侧：发货确认 B（fr_th G__.row467 序列原样；fhdate 归一 Today）。
    /// lineIds = 本次发货的行（旧 c_mx 勾选且未发货行；null=全部行）。</summary>
    public static void DeliveryConfirmB(SqlConnection conn, string dept, string per, string paper,
        string back1, string remark, string spname, string jhr, string fhPaper, string perDbr, string muzhan,
        bool stockOnly, IReadOnlyList<int>? lineIds = null)
    {
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        var retailGuno = conn.ExecuteScalar<string?>(
            "SELECT TOP 1 RTRIM(valuec) FROM tbsysset WHERE type = 'system' AND name = N'零售标识' AND dpm = @dpm",
            new { dpm = Ansi(dept, 10) }) ?? "";
        var mark = conn.ExecuteScalar<string?>("SELECT RTRIM(mark) FROM tbsada WHERE paper = @paper",
            new { paper = Ansi(paper, 10) });
        var guno = conn.ExecuteScalar<string?>("SELECT RTRIM(guno) FROM tbsada WHERE paper = @paper",
            new { paper = Ansi(paper, 10) });
        var isRetail = guno == retailGuno;
        var doStockOnly = stockOnly && mark != "Y" && !isRetail;

        using var tx = conn.BeginTransaction();
        try
        {
            if (!doStockOnly)
            {
                var ids = lineIds ?? DeliveryLineIds(conn, tx, paper);
                foreach (var id in ids)
                    conn.Execute(
                        "UPDATE tbsada SET fhdate = @fhdate, back1 = @back1, remark = @remark WHERE id = @id AND (fhdate IS NULL OR fhdate = '1900-01-01')",
                        new { fhdate = DateTime.Today, back1 = Ansi(back1, 10), remark = Ansi(remark, 100), id }, tx);
            }

            // header spname upsert（row467 步骤 2）
            conn.Execute(
                """
                IF EXISTS (SELECT 1 FROM tbsada_header WHERE paper = @paper AND dpm = @dpm)
                    UPDATE tbsada_header SET spname = @spname WHERE paper = @paper AND dpm = @dpm
                ELSE
                    INSERT INTO tbsada_header (paper, dpm, spname) VALUES (@paper, @dpm, @spname)
                """,
                new { paper = Ansi(paper, 10), dpm = Ansi(dept, 10), spname = Ansi(spname, 60) }, tx);

            // tbfh upsert（row467 步骤 3）
            conn.Execute(
                """
                IF EXISTS (SELECT 1 FROM tbfh WHERE sada_id IN (SELECT id FROM tbsada WHERE paper = @paper AND dpm = @dpm))
                    UPDATE tbfh SET per_jhr = @jhr, fh_paper = @fhPaper, fhgs = @spname, per_dbr = @perDbr
                    WHERE sada_id IN (SELECT id FROM tbsada WHERE paper = @paper AND dpm = @dpm)
                ELSE
                    INSERT INTO tbfh (fh_paper, sada_paper, sada_id, nno, na1, ty, fa, unit, fhdate, box, per_jhr, fhamount, per_dbr, fhgs, fhcount, fhgl, gname, ckamount)
                    SELECT @fhPaper, paper, id, nno, na1, ty, fa, unit, ISNULL(fhdate, '1900-01-01'), '', @jhr, 0, @perDbr, @spname, 0, 0, gname, ckamount
                    FROM tbsada WHERE paper = @paper AND dpm = @dpm
                """,
                new
                {
                    paper = Ansi(paper, 10), dpm = Ansi(dept, 10), jhr = Ansi(jhr, 10), fhPaper = Ansi(fhPaper, 15),
                    spname = Ansi(spname, 20), perDbr = Ansi(perDbr, 10),
                }, tx);

            // tbwlfh（row467 步骤 4）
            conn.Execute(
                """
                IF EXISTS (SELECT 1 FROM tbwlfh WHERE fh_paper = @fhPaper AND paper = @paper)
                    UPDATE tbwlfh SET spname = @spname, muzhan = @muzhan, fh_paper = @fhPaper WHERE paper = @paper
                ELSE
                    INSERT INTO tbwlfh (spname, fh_paper, fhamount, tscount, fhdate, outdate, paper, salsecount, gname, per, dpm, cp, ckamount, muzhan)
                    SELECT a.fhgs, @fhPaper, 0, 0, a.fhdate, b.outdate, a.sada_paper,
                           SUM(b.ckamount * b.oprc * (100 - b.yhl) / 100), a.gname, b.per, b.dpm, '', SUM(b.ckamount), @muzhan
                    FROM tbfh a LEFT JOIN tbsada b ON a.sada_id = b.id
                    WHERE a.sada_paper = @paper
                    GROUP BY a.fhgs, a.fhdate, b.outdate, a.sada_paper, a.gname, b.per, b.dpm
                """,
                new { fhPaper = Ansi(fhPaper, 15), spname = Ansi(spname, 60), muzhan = Ansi(muzhan, 20), paper = Ansi(paper, 10) }, tx);

            if (doStockOnly)
            {
                // 备货分支（row467 步骤 7）
                conn.Execute(
                    """
                    IF EXISTS (SELECT 1 FROM tbsada_header WHERE paper = @paper AND dpm = @dpm)
                        UPDATE tbsada_header SET bhmark = 'bh' WHERE paper = @paper AND dpm = @dpm
                    ELSE
                        INSERT INTO tbsada_header (paper, dpm, bhmark) VALUES (@paper, @dpm, 'bh')
                    """,
                    new { paper = Ansi(paper, 10), dpm = Ansi(dept, 10) }, tx);
            }
            else
            {
                // fhdate 同步（步骤 5）
                conn.Execute("UPDATE tbfh SET fhdate = @fhdate WHERE sada_paper = @paper",
                    new { fhdate = DateTime.Today, paper = Ansi(paper, 10) }, tx);
                conn.Execute("UPDATE tbwlfh SET fhdate = @fhdate WHERE paper = @paper",
                    new { fhdate = DateTime.Today, paper = Ansi(paper, 10) }, tx);
                // 整单判定（步骤 6）
                var pending = conn.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM tbsada WHERE type IN (N'销售', N'历史') AND paper = @paper AND ISNULL(fhdate, '1900-01-01') < '1900-01-02'",
                    new { paper = Ansi(paper, 10) }, tx);
                if (pending == 0)
                    conn.Execute("UPDATE tbsada SET mark = 'T' WHERE type IN (N'销售', N'历史') AND paper = @paper",
                        new { paper = Ansi(paper, 10) }, tx);
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // =================================================================================
    // G 组（出纳管理）旧侧回放 —— 契约：开发文档/契约摘录/出纳管理G组契约.md
    // 财务凭证 finvouch / 积分 integral_xlsk 分支 FINVOUCH=.F. 不重放（与本部署一致）。
    // 部署常量分支（沙河丰田/庆丰丰田/万德/宝狮）不适用，按主路径。
    // =================================================================================

    /// <summary>种子：销售应收单（tbsada 行 + tbysyf type='销售' 挂帐行，供收款结清联动 jzdate）。</summary>
    public static void SeedReceivableSale(SqlConnection conn, string dept, string per, string paper,
        string cno, decimal ys, string fkfs)
    {
        conn.Execute(
            """
            INSERT INTO tbsada (cp, nno, na1, fa, ty, unit, type, posi, ckamount, oprc, oprct, iprj, iprc,
                                paper, gno, gname, per, outdate, dpm, depotno, kcid, mark, guno, yhl, remark, jzdate)
            VALUES (N'RE', N'REPLAY16', N'回放配件G', N'', N'回放车型', N'个', N'销售', N'', @ys, 0, 0, 0, 0,
                    @paper, @gno, @gname, @per, @outdate, @dpm, N'', 0, N'', N'挂帐', 0, '', '1900-01-01')
            """,
            new
            {
                ys, paper = Ansi(paper, 10), gno = ReplayConfig.Gno, gname = ReplayConfig.Gname,
                per = Ansi(per, 10), outdate = DateTime.Today, dpm = Ansi(dept),
            });
        conn.Execute(
            """
            INSERT INTO tbysyf (type, cno, gname, xm, datime, fkfs, ys, yf, skmoney, lj, per, dpm)
            VALUES (N'销售', @cno, @gname, N'回放应收', @datime, @fkfs, @ys, 0, 0, 0, @per, @dpm)
            """,
            new
            {
                cno = Ansi(cno, 30), gname = Ansi(ReplayConfig.Gname, 60), datime = DateTime.Today,
                fkfs = Ansi(fkfs, 10), ys, per = Ansi(per, 10), dpm = Ansi(dept),
            });
    }

    /// <summary>种子：入库应付行（tbysyf type='入库'，供付款）。</summary>
    public static void SeedPayableInStock(SqlConnection conn, string dept, string per, string cno, decimal yf, string fkfs)
    {
        conn.Execute(
            """
            INSERT INTO tbysyf (type, cno, gname, xm, datime, fkfs, ys, yf, skmoney, lj, per, dpm)
            VALUES (N'入库', @cno, @gname, N'回放应付', @datime, @fkfs, 0, @yf, 0, 0, @per, @dpm)
            """,
            new
            {
                cno = Ansi(cno, 30), gname = Ansi(ReplayConfig.Gname, 60), datime = DateTime.Today,
                fkfs = Ansi(fkfs, 10), yf, per = Ansi(per, 10), dpm = Ansi(dept),
            });
    }

    /// <summary>载入未结清单（CmdGU，鏤.row41）：返回待结算 tbysyf 行（id/type/cno/ys/yf/skmoney）。</summary>
    public static List<(int id, string type, string cno, decimal ys, decimal yf, decimal skmoney)>
        XlskLoadOutstanding(SqlConnection conn, string gname, string type)
        => conn.Query<(int id, string type, string cno, decimal ys, decimal yf, decimal skmoney)>(
            """
            SELECT id, RTRIM(type), RTRIM(cno), ys, yf, skmoney
            FROM tbysyf WITH (NOLOCK)
            WHERE gname = @gname AND type <> N'预收'
              AND ((@t = N'收款' AND ys <> 0) OR (@t = N'付款' AND yf <> 0))
              AND ys - yf <> 0 AND ABS(skmoney) <> ABS(ys - yf)
            ORDER BY datime
            """,
            new { gname = Ansi(gname, 60), t = type }).AsList();

    /// <summary>客户余额 TXTYUE（CmdGU 全行 Σ(ys-yf)，含被过滤行）。</summary>
    public static decimal XlskCustomerYue(SqlConnection conn, string gname)
        => conn.ExecuteScalar<decimal>(
            "SELECT ISNULL(SUM(ys - yf), 0) FROM tbysyf WITH (NOLOCK) WHERE gname = @gname",
            new { gname = Ansi(gname, 60) });

    /// <summary>还原行推导（两侧共用；= 载入已有单 skno 后的游标值：BCJE=流水额、WSJE=max(JE-skmoney,0)）。</summary>
    public static List<(int id, string paper, string lineType, decimal bcje, decimal wsje, bool check)>
        XlskDeriveRestoreLines(SqlConnection conn, string skno, string type)
    {
        var flows = conn.Query<(int fid, string paper, decimal fys, decimal fyf)>(
            "SELECT id, RTRIM(ISNULL(paper,'')), ISNULL(ys,0), ISNULL(yf,0) FROM tblsz WITH (NOLOCK) WHERE skno = @skno ORDER BY id",
            new { skno = Ansi(skno, 10) }).AsList();
        var result = new List<(int id, string paper, string lineType, decimal bcje, decimal wsje, bool check)>();
        foreach (var f in flows)
        {
            (int id, decimal ys, decimal yf, decimal skmoney) tgt;
            if (f.paper.StartsWith("id:", StringComparison.Ordinal) && int.TryParse(f.paper[3..], out var refId))
                tgt = conn.QuerySingle<(int id, decimal ys, decimal yf, decimal skmoney)>(
                    "SELECT TOP (1) id, ys, yf, skmoney FROM tbysyf WITH (NOLOCK) WHERE id = @id", new { id = refId });
            else
                tgt = conn.QuerySingle<(int id, decimal ys, decimal yf, decimal skmoney)>(
                    "SELECT TOP (1) id, ys, yf, skmoney FROM tbysyf WITH (NOLOCK) WHERE cno = @cno",
                    new { cno = Ansi(f.paper, 30) });
            var je = type == "收款" ? tgt.ys : tgt.yf;
            var lineType = conn.ExecuteScalar<string>(
                "SELECT TOP (1) RTRIM(type) FROM tbysyf WITH (NOLOCK) WHERE id = @id", new { id = tgt.id }) ?? "";
            result.Add((tgt.id, f.paper, lineType, type == "收款" ? f.fys : f.fyf, Math.Max(je - tgt.skmoney, 0), true));
        }
        return result;
    }

    /// <summary>收款/付款确认（A_.row75 序列原样；FINVOUCH 分支不重放）。</summary>
    public static string XlskSave(SqlConnection conn, string type, string gname, string fkfs, string bankname,
        DateTime date, DateTime lszDate, string pno, decimal paySum, decimal ljValue,
        IReadOnlyList<(int id, string paper, string lineType, decimal bcje, decimal wsje)> lines,
        string dpm, string per)
    {
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        // 1) tblsz 去向保障（row75 L36-43）
        if (conn.ExecuteScalar<int>("SELECT COUNT(1) FROM tblsz WITH (NOLOCK) WHERE guno = @fkfs",
                new { fkfs = Ansi(fkfs, 10) }) == 0)
            conn.Execute("INSERT INTO tblsz (lj, guno, dpm) VALUES (0, @fkfs, @dpm)",
                new { fkfs = Ansi(fkfs, 10), dpm = Ansi(dpm, 10) });
        // 2) 取号（row2 getnewdh：先保障前缀，再 GETNEWDH(1, djname, tbysyf, cno)）
        var djName = type + "单";
        var prefix = type == "收款" ? "SK" : "FK";
        conn.Execute(
            """
            IF NOT EXISTS (SELECT 1 FROM tbdjgs WHERE dpm = @dpm AND djname = @dj)
                INSERT INTO tbdjgs (djname, djjx, dyear, dpm) VALUES (@dj, @prefix, @year, @dpm)
            """,
            new { dpm = Ansi(dpm, 10), dj = djName, prefix = Ansi(prefix, 4), year = DateTime.Today.Year.ToString() });
        var skno = GetNewDh(conn, djName, dpm, 1);
        // 3) 汇总行（row75 L64-76；PASSTH 自动补 dpm）
        var summaryId = conn.ExecuteScalar<int>(
            """
            INSERT INTO tbysyf (type, cno, gname, xm, datime, fkfs, pno, ys, yf, lj, per, dpm)
            VALUES (@type, @cno, @gname, N'', @datime, @fkfs, @pno, @ys, @yf, @lj, @per, @dpm);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new
            {
                type = Ansi(type, 8), cno = Ansi(skno, 30), gname = Ansi(gname, 60), datime = date,
                fkfs = Ansi(fkfs, 10), pno = new DbString { Value = pno, IsAnsi = true, Length = 50 },
                ys = type == "收款" ? -paySum : 0m, yf = type == "付款" ? -paySum : 0m,
                lj = ljValue, per = Ansi(per, 10), dpm = Ansi(dpm, 10),
            });
        var totalBcje = lines.Sum(l => Math.Abs(l.bcje));
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var line in lines)
            {
                // 4a. skmoney 累加（收/付款类行取绝对值）
                var delta = line.lineType is "收款" or "付款" ? Math.Abs(line.bcje) : line.bcje;
                conn.Execute("UPDATE tbysyf SET skmoney = skmoney + @delta WHERE id = @id",
                    new { delta, line.id }, tx);
                // 4b. 流水行（row75 L92-112）
                var paper = string.IsNullOrWhiteSpace(line.paper) ? "id:" + (line.id == 0 ? summaryId : line.id) : line.paper;
                conn.Execute(
                    """
                    INSERT INTO tblsz (ls_date, guno, ytu, remark, ys, yf, skno, paper, gname, bankname, dpm, pno)
                    VALUES (@lsDate, @guno, @ytu, @remark, @ys, @yf, @skno, @paper, @gname, @bankname, @dpm, '')
                    """,
                    new
                    {
                        lsDate = lszDate, guno = Ansi(fkfs, 10), ytu = Ansi(type, 20),
                        remark = Ansi($"{line.lineType}：{line.paper}", 50),
                        ys = type == "收款" ? line.bcje : 0m, yf = type == "付款" ? line.bcje : 0m,
                        skno = Ansi(skno, 10), paper = Ansi(paper, 30), gname = Ansi(gname, 60),
                        bankname = Ansi(bankname, 40), dpm = Ansi(dpm, 10),
                    }, tx);
                // 4c. 结清联动 + 二次冲抵（row75 L116-163；先 REPLACE WSJE=max(WSJE-BCJE,0) 再判=0；
                //     内层 tblsz 反查块只拼日志不执行——旧码原样）
                if (line.wsje - line.bcje <= 0)
                {
                    if (line.lineType == "销售")
                        conn.Execute("UPDATE tbsada SET jzdate = @date WHERE paper = @paper",
                            new { date, paper = Ansi(line.paper, 15) }, tx);
                    else if (line.lineType == "维修")
                    {
                        if (conn.ExecuteScalar<int>("SELECT COUNT(1) FROM tbysyf WITH (NOLOCK) WHERE cno = @paper AND ys <> skmoney",
                                new { paper = Ansi(line.paper, 30) }, tx) == 0)
                            conn.Execute("UPDATE tbxlrc SET jzdate = @date WHERE gh = @paper",
                                new { date, paper = Ansi(line.paper, 15) }, tx);
                    }
                }
                // 4d. 汇总行 skmoney=勾选总额（row75 L163，逐行重复执行）
                conn.Execute("UPDATE tbysyf SET skmoney = @total WHERE cno = @cno AND type = @type AND dpm = @dpm",
                    new { total = totalBcje, cno = Ansi(skno, 30), type = Ansi(type, 8), dpm = Ansi(dpm, 10) }, tx);
            }
            // 5) 维修行（row75 L166-188）
            foreach (var line in lines.Where(l => l.lineType == "维修"))
                conn.Execute("UPDATE tbxlrc SET colmoney = colmoney + @bcje WHERE gh = @gh",
                    new { bcje = line.bcje, gh = Ansi(line.paper, 15) }, tx);
            if (pno.Trim().Length > 1)
                foreach (var line in lines.Where(l => l.lineType == "维修"))
                {
                    var xm = conn.ExecuteScalar<string?>("SELECT TOP (1) RTRIM(ISNULL(xm,'')) FROM tbxlrc WITH (NOLOCK) WHERE gh = @gh",
                        new { gh = Ansi(line.paper, 15) }, tx) ?? "";
                    if (!xm.Contains(pno.Trim(), StringComparison.Ordinal))
                        conn.Execute("UPDATE tbxlrc SET xm = @xm WHERE gh = @gh",
                            new { xm = new DbString { Value = xm + (xm.Length == 0 ? "" : ",") + pno.Trim(), IsAnsi = false, Length = 250 },
                                  gh = Ansi(line.paper, 15) }, tx);
                }
            // 6) 发票管理（row75 L244-265）
            if (!string.IsNullOrWhiteSpace(pno))
                conn.Execute(
                    """
                    INSERT INTO tbfpgl (pno, gname, fpdate, fpje, jsr, djno, ctype, fptype, note, fkfs, dpm)
                    VALUES (@pno, @gname, @fpdate, @fpje, @jsr, @djno, @ctype, '', '', @fkfs, @dpm)
                    """,
                    new
                    {
                        pno = new DbString { Value = pno.Trim(), IsAnsi = true, Length = 50 },
                        gname = Ansi(gname, 60), fpdate = date, fpje = paySum, jsr = Ansi(per, 10),
                        djno = Ansi(skno, 30), ctype = Ansi(lines[0].lineType, 10), fkfs = Ansi(fkfs, 10),
                        dpm = Ansi(dpm, 10),
                    }, tx);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        // 7) 余额刷新（row75 L267-269）
        var yue = new DynamicParameters();
        yue.Add("@gname", gname, System.Data.DbType.AnsiString, size: 50);
        yue.Add("@yue", dbType: System.Data.DbType.Decimal, direction: System.Data.ParameterDirection.Output, precision: 18, scale: 2);
        conn.Execute("dbo.ysyf_refresh", yue, commandType: System.Data.CommandType.StoredProcedure);
        _ = yue.Get<decimal>("@yue");
        return skno;
    }

    /// <summary>还原收款/付款（c_.row74 序列原样；月结保护/权限为前置校验不重放）。</summary>
    public static void XlskRestore(SqlConnection conn, string type, string gname, string skno,
        IReadOnlyList<(int id, string paper, string lineType, decimal bcje, decimal wsje, bool check)> lines,
        string dpm, string per)
    {
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            // 4) 删除汇总行（L47）
            conn.Execute("DELETE FROM tbysyf WHERE cno = @cno", new { cno = Ansi(skno, 30) }, tx);
            // 5) 维修 colmoney 回退（L50-57，全部载入维修行）
            foreach (var line in lines.Where(l => l.lineType == "维修"))
                conn.Execute("UPDATE tbxlrc SET colmoney = colmoney - @bcje WHERE gh = @gh",
                    new { bcje = line.bcje, gh = Ansi(line.paper, 15) }, tx);
            // 6) 逐勾选行（L60-95）
            foreach (var line in lines.Where(l => l.check))
            {
                var delta = line.lineType is "收款" or "付款" ? Math.Abs(line.bcje) : line.bcje;
                conn.Execute("UPDATE tbysyf SET skmoney = skmoney - @delta WHERE id = @id",
                    new { delta, line.id }, tx);
                if (line.wsje == 0)
                {
                    if (line.lineType == "销售")
                        conn.Execute("UPDATE tbsada SET jzdate = '1900-01-01' WHERE paper = @paper",
                            new { paper = Ansi(line.paper, 15) }, tx);
                    else if (line.lineType == "维修")
                        conn.Execute("UPDATE tbxlrc SET jzdate = '1900-01-01' WHERE gh = @paper",
                            new { paper = Ansi(line.paper, 15) }, tx);
                    // 二次冲抵（L84-94，结清分支内实际执行）
                    var flows = conn.Query<(string skno, decimal ys, decimal yf)>(
                        "SELECT RTRIM(skno), ISNULL(ys,0), ISNULL(yf,0) FROM tblsz WITH (NOLOCK) WHERE paper = @paper",
                        new { paper = Ansi(string.IsNullOrWhiteSpace(line.paper) ? "id:" + line.id : line.paper, 30) }, tx).AsList();
                    foreach (var f in flows.Where(f => f.skno == skno))
                    {
                        var je = type == "收款" ? f.ys : f.yf;
                        conn.Execute("UPDATE tbysyf SET skmoney = skmoney - @je WHERE cno = @skno AND type = @type",
                            new { je, skno = Ansi(f.skno, 10), type = Ansi(type, 8) }, tx);
                    }
                }
            }
            // 7) 删除流水（L97）
            conn.Execute("DELETE FROM tblsz WHERE skno = @cno", new { cno = Ansi(skno, 10) }, tx);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        var yue = new DynamicParameters();
        yue.Add("@gname", gname, System.Data.DbType.AnsiString, size: 50);
        yue.Add("@yue", dbType: System.Data.DbType.Decimal, direction: System.Data.ParameterDirection.Output, precision: 18, scale: 2);
        conn.Execute("dbo.ysyf_refresh", yue, commandType: System.Data.CommandType.StoredProcedure);
        _ = yue.Get<decimal>("@yue");
    }

    /// <summary>应收应付：添加客户空行（婅_.row140 add；PASSTH 自动补 dpm）。</summary>
    public static void YsyfAddCustomer(SqlConnection conn, string gname, decimal cb, decimal tick, decimal yjb, string htno, string dpm)
        => conn.Execute(
            "INSERT INTO tbysyf (gname, cb, tick, yjb, htno, dpm) VALUES (@g, @cb, @tick, @yjb, @htno, @dpm)",
            new { g = Ansi(gname, 60), cb, tick = (int)tick, yjb, htno = new DbString { Value = htno, IsAnsi = true, Length = 50 }, dpm = Ansi(dpm, 10) });

    /// <summary>应收应付：添加明细行（___.row119 add）。</summary>
    public static int YsyfAddDetail(SqlConnection conn, string gname, DateTime datime, string cno, string xm,
        string pno, decimal ys, decimal yf, string type, string fkfs, string note, string dpm, string per)
    {
        var id = conn.ExecuteScalar<int>(
            """
            INSERT INTO tbysyf (datime, cno, gname, xm, pno, ys, yf, type, fkfs, per, note, dpm)
            VALUES (@datime, @cno, @gname, @xm, @pno, @ys, @yf, @type, @fkfs, @per, @note, @dpm);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new
            {
                datime, cno = Ansi(cno, 30), gname = Ansi(gname, 60),
                xm = new DbString { Value = xm, IsAnsi = false, Length = 250 },
                pno = new DbString { Value = pno, IsAnsi = true, Length = 50 },
                ys, yf, type = Ansi(type, 8), fkfs = Ansi(fkfs, 10), per = Ansi(per, 10),
                note = new DbString { Value = note, IsAnsi = false, Length = 250 }, dpm = Ansi(dpm, 10),
            });
        // 发票管理 upsert（row119 L43-53）
        if (!string.IsNullOrWhiteSpace(pno))
        {
            if (conn.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM tbfpgl WITH (NOLOCK) WHERE djno = @djno AND ctype = @ctype AND gname = @gname AND pno = @pno",
                    new { djno = Ansi(cno, 30), ctype = Ansi(type, 10), gname = Ansi(gname, 60), pno = new DbString { Value = pno, IsAnsi = true, Length = 50 } }) == 0)
                conn.Execute(
                    """
                    INSERT INTO tbfpgl (pno, gname, fpdate, fpje, jsr, djno, ctype, fptype, note, fkfs, dpm)
                    VALUES (@pno, @gname, @fpdate, @fpje, @jsr, @djno, @ctype, '', '', @fkfs, @dpm)
                    """,
                    new
                    {
                        pno = new DbString { Value = pno.Trim(), IsAnsi = true, Length = 50 },
                        gname = Ansi(gname, 60), fpdate = datime, fpje = Math.Abs(ys + yf),
                        jsr = Ansi(per, 10), djno = Ansi(cno, 30), ctype = Ansi(type, 10),
                        fkfs = Ansi(fkfs, 10), dpm = Ansi(dpm, 10),
                    });
        }
        return id;
    }

    /// <summary>应收应付：修改明细行（___.row119 edit，per 保留原值）。</summary>
    public static void YsyfUpdateDetail(SqlConnection conn, int id, DateTime datime, string cno, string xm,
        string pno, decimal ys, decimal yf, string type, string fkfs, string note)
        => conn.Execute(
            "UPDATE tbysyf SET datime = @datime, cno = @cno, xm = @xm, pno = @pno, ys = @ys, yf = @yf, type = @type, fkfs = @fkfs, note = @note WHERE id = @id",
            new
            {
                id, datime, cno = Ansi(cno, 30),
                xm = new DbString { Value = xm, IsAnsi = false, Length = 250 },
                pno = new DbString { Value = pno, IsAnsi = true, Length = 50 },
                ys, yf, type = Ansi(type, 8), fkfs = Ansi(fkfs, 10),
                note = new DbString { Value = note, IsAnsi = false, Length = 250 },
            });

    /// <summary>应收应付：删除明细行（row4 DELEMX，发票联动删除）。</summary>
    public static void YsyfDeleteDetail(SqlConnection conn, int id, bool deleteInvoice)
    {
        var row = conn.QuerySingle<(string type, decimal skmoney, decimal ys, decimal yf, string cno, string pno, string gname)>(
            "SELECT RTRIM(type), skmoney, ys, yf, RTRIM(cno), ISNULL(RTRIM(pno),''), RTRIM(gname) FROM tbysyf WITH (NOLOCK) WHERE id = @id",
            new { id });
        if (row.type is "收款" or "付款") throw new InvalidOperationException($"请用{row.type}管理还原。");
        if (row.skmoney != 0 && (row.ys > 0 || row.yf > 0)) throw new InvalidOperationException("该项已有收款记录，请先还原收款单再删除。");
        conn.Execute("DELETE FROM tbysyf WHERE id = @id", new { id });
        if (deleteInvoice && row.pno.Length > 0)
            conn.Execute(
                "DELETE FROM tbfpgl WHERE djno = @djno AND ctype = @ctype AND gname = @gname AND pno = @pno AND fpje = @fpje",
                new { djno = Ansi(row.cno, 30), ctype = Ansi(row.type, 10), gname = Ansi(row.gname, 60),
                      pno = new DbString { Value = row.pno, IsAnsi = true, Length = 50 }, fpje = row.ys + row.yf });
    }

    /// <summary>应收应付：多条删除（row4 MDELE，按 datime,id 游标序删锚点前全部行，锚点行归并汇总行）。</summary>
    public static void YsyfMultiDeleteBefore(SqlConnection conn, int anchorId, string gname, DateTime today, string per)
    {
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            var anchor = conn.QuerySingle<(DateTime datime, decimal ys, decimal yf)>(
                "SELECT datime, ys, yf FROM tbysyf WITH (NOLOCK) WHERE id = @id", new { id = anchorId }, tx);
            var prevLj = conn.ExecuteScalar<decimal?>(
                "SELECT TOP (1) lj FROM tbysyf WITH (NOLOCK) WHERE gname = @gname AND (datime < @d OR (datime = @d AND id < @id)) ORDER BY datime DESC, id DESC",
                new { gname = Ansi(gname, 60), d = anchor.datime, id = anchorId }, tx);
            var lj = prevLj ?? 0m;
            conn.Execute("DELETE FROM tbysyf WHERE gname = @gname AND (datime < @d OR (datime = @d AND id < @id))",
                new { gname = Ansi(gname, 60), d = anchor.datime, id = anchorId }, tx);
            var ys = lj > 0 ? anchor.ys + lj : anchor.ys;
            var yf = lj < 0 ? anchor.yf - lj : anchor.yf;
            var skmoney = ys > yf ? ys : yf;
            conn.Execute(
                """
                UPDATE tbysyf SET type = N'其他', cno = @cno, xm = @xm, ys = @ys, yf = @yf, skmoney = @skmoney,
                                    datime = '1900-01-01', per = @per
                WHERE id = @id
                """,
                new
                {
                    id = anchorId, cno = Ansi(today.ToString("yyyy-MM-dd"), 30),
                    xm = new DbString { Value = "做过多记录删除" + anchor.datime.ToString("yyyy-MM-dd"), IsAnsi = false, Length = 250 },
                    ys, yf, skmoney, per = Ansi(per, 10),
                }, tx);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>收支流水：初始累计数（Uz.row69；ls_date 取默认 1900-01-01）。</summary>
    public static void LszAddInitial(SqlConnection conn, string bankname, decimal amount, string dpm)
    {
        if (amount > 0)
            conn.Execute("INSERT INTO tblsz (bankname, ys, remark, dpm) VALUES (@bank, @ys, N'初始累计数', @dpm)",
                new { bank = Ansi(bankname, 40), ys = amount, dpm = Ansi(dpm, 10) });
        else
            conn.Execute("INSERT INTO tblsz (bankname, yf, remark, dpm) VALUES (@bank, @yf, N'初始累计数', @dpm)",
                new { bank = Ansi(bankname, 40), yf = amount, dpm = Ansi(dpm, 10) });
    }

    /// <summary>收支流水：手工新增行（_r.row65 add）。</summary>
    public static int LszAddManual(SqlConnection conn, DateTime lsDate, string skno, string ytu, decimal ys,
        decimal yf, string remark, string gname, string note, string guno, string bankname, string dpm)
        => conn.ExecuteScalar<int>(
            """
            INSERT INTO tblsz (ls_date, skno, ytu, ys, yf, remark, gname, note, bankname, guno, dpm)
            OUTPUT inserted.id
            VALUES (@lsDate, @skno, @ytu, @ys, @yf, @remark, @gname, @note, @bankname, @guno, @dpm)
            """,
            new
            {
                lsDate, skno = Ansi(skno, 10), ytu = Ansi(ytu, 20), ys, yf, remark = Ansi(remark, 50),
                gname = Ansi(gname, 60), note = new DbString { Value = note, IsAnsi = false, Length = 200 },
                bankname = Ansi(bankname, 40), guno = Ansi(guno, 10), dpm = Ansi(dpm, 10),
            });

    /// <summary>收支流水：手工修改行（_r.row65 edit）。</summary>
    public static void LszUpdateManual(SqlConnection conn, int id, DateTime lsDate, string skno, string ytu,
        decimal ys, decimal yf, string remark, string gname, string note, string guno, string bankname, string dpm)
        => conn.Execute(
            "UPDATE tblsz SET ls_date = @lsDate, skno = @skno, ytu = @ytu, ys = @ys, yf = @yf, remark = @remark, gname = @gname, note = @note, bankname = @bankname, guno = @guno, dpm = @dpm WHERE id = @id",
            new
            {
                id, lsDate, skno = Ansi(skno, 10), ytu = Ansi(ytu, 20), ys, yf, remark = Ansi(remark, 50),
                gname = Ansi(gname, 60), note = new DbString { Value = note, IsAnsi = false, Length = 200 },
                bankname = Ansi(bankname, 40), guno = Ansi(guno, 10), dpm = Ansi(dpm, 10),
            });

    /// <summary>收支流水：删除单行（7_.row32 删除规则；SK/FK 收付款流水拒绝）。</summary>
    public static void LszDeleteRow(SqlConnection conn, int id)
    {
        var row = conn.QuerySingle<(DateTime lsDate, string skno, string paper, string bankname)>(
            "SELECT ls_date, RTRIM(ISNULL(skno,'')), RTRIM(ISNULL(paper,'')), RTRIM(ISNULL(bankname,'')) FROM tblsz WITH (NOLOCK) WHERE id = @id",
            new { id });
        if (row.lsDate == default && row.skno.Length == 0 && row.paper.Length == 0)
            throw new InvalidOperationException("不能删除初始累计数");
        if (row.paper.Length > 0 &&
            !(row.bankname.Length > 0 && !row.skno.Contains("SK", StringComparison.OrdinalIgnoreCase)
                                        && !row.skno.Contains("FK", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("不能删除此流水帐记录");
        conn.Execute("DELETE FROM tblsz WHERE id = @id", new { id });
    }

    /// <summary>收支流水：删除锚点之前全部可删行 + 清其他初始累计数 + 锚点 lj 落库（鐷.row36）。</summary>
    public static void LszDeleteBefore(SqlConnection conn, int anchorId, string bankname, decimal anchorLj)
    {
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            var anchor = conn.QuerySingle<(DateTime lsDate, int id)>(
                "SELECT ls_date, id FROM tblsz WITH (NOLOCK) WHERE id = @id", new { id = anchorId }, tx);
            var deletable = conn.Query<int>(
                """
                SELECT id FROM tblsz WITH (NOLOCK)
                WHERE (ls_date < @ls OR (ls_date = @ls AND id < @id))
                  AND ((ISNULL(paper,'') = '') OR (ISNULL(bankname,'') <> '' AND CHARINDEX('SK', UPPER(skno)) = 0 AND CHARINDEX('FK', UPPER(skno)) = 0))
                """,
                new { ls = anchor.lsDate, id = anchorId }, tx).AsList();
            foreach (var id in deletable)
                conn.Execute("DELETE FROM tblsz WHERE id = @id", new { id }, tx);
            conn.Execute("DELETE FROM tblsz WHERE remark = N'初始累计数' AND bankname = @bank AND id <> @id",
                new { bank = Ansi(bankname, 40), id = anchorId }, tx);
            conn.Execute("UPDATE tblsz SET lj = @lj WHERE id = @id", new { lj = anchorLj, id = anchorId }, tx);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>场景 15 种子：一条历史合同行（tborder type='历史'，cno/orno 手工）。</summary>
    public static void SeedOrderContract(SqlConnection conn, string dept, string cp, string per, string cno, Batch part)
    {
        conn.Execute(
            """
            INSERT INTO tborder
                (cp, cno, item, nno, na1, oqty, iprj, orderdate, ty, fa, type, gyname, fkfs, bzlx, oprc, orno, per, dpm)
            VALUES
                (@cp, @cno, 1, @nno, @na1, 10, @iprj, @orderdate, @ty, @fa, '历史', N'回放供应商', N'挂帐', '', @oprc, @orno, @per, @dpm)
            """,
            new
            {
                cp = Ansi(cp, 10), cno = Ansi(cno, 15), nno = Ansi(part.Nno, 18), na1 = Ansi(part.Na1, 40),
                iprj = part.Iprj, orderdate = DateTime.Today, ty = Ansi(part.Ty, 14), fa = Ansi(part.Fa, 8),
                oprc = part.Oprc, orno = Ansi(cno + "O1", 15), per = Ansi(per, 10), dpm = Ansi(dept, 10),
            });
    }

    /// <summary>场景 15 旧侧：fr_order3 W1 新增行（Qs.row99:L21，19 列 + dpm；建档跳过——件已存在）。</summary>
    public static void Order3AddLine(SqlConnection conn, string dept, string cp, string per, string cno,
        string orno, Batch part, decimal oqty)
    {
        var maxItem = conn.ExecuteScalar<int?>("SELECT MAX(item) FROM tborder WHERE cno = @cno",
            new { cno = Ansi(cno, 15) });
        conn.Execute(
            """
            INSERT INTO tborder
                (cp, cno, item, nno, na1, oqty, iprj, orderdate, ty, fa, note, type, gyname, gname, fkfs, bzlx, oprc, orno, per, dpm)
            VALUES
                (@cp, @cno, @item, @nno, @na1, @oqty, @iprj, @orderdate, @ty, @fa, '', '历史', N'回放供应商', '', N'挂帐', '', @oprc, @orno, @per, @dpm)
            """,
            new
            {
                cp = Ansi(cp, 10), cno = Ansi(cno, 15), item = (maxItem ?? 0) + 1, nno = Ansi(part.Nno, 18),
                na1 = Ansi(part.Na1, 40), oqty, iprj = part.Iprj, orderdate = DateTime.Today,
                ty = Ansi(part.Ty, 14), fa = Ansi(part.Fa, 8), oprc = part.Oprc, orno = Ansi(orno, 15),
                per = Ansi(per, 10), dpm = Ansi(dept, 10),
            });
    }

    /// <summary>场景 15 旧侧：fr_order4 I2/U4 新增计划行（___.row352:L32/L39：同编码已存在相加，否则 INSERT）。</summary>
    public static void Order4AddLine(SqlConnection conn, string dept, string cp, string per, string cno,
        Batch part, decimal oqty)
    {
        var existing = conn.ExecuteScalar<int?>(
            "SELECT TOP 1 id FROM tborder WHERE cno = @cno AND type = '' AND nno = @nno AND cp = @xcp AND @p_dpmusem LIKE '%' + RTRIM(dpm) + '%'",
            new { cno = Ansi(cno, 15), nno = Ansi(part.Nno, 18), xcp = Ansi(cp, 10), p_dpmusem = Ansi(cp, 50) });
        if (existing is { } id)
        {
            // U4（___.row352:L32）：oqty 累加
            conn.Execute("UPDATE tborder SET oqty = oqty + @oqty WHERE id = @id", new { oqty, id });
            return;
        }
        conn.Execute(
            """
            INSERT INTO tborder
                (cp, nno, na1, ty, fa, unit, dc, oqty, iprj, cno, orderdate, bzlx, fkfs, gyname, gname, per, dpm)
            VALUES
                (@cp, @nno, @na1, @ty, @fa, @unit, @dc, @oqty, @iprj, @cno, @orderdate, '', N'挂帐', N'回放供应商', '', @per, @dpm)
            """,
            new
            {
                cp = Ansi(cp, 10), nno = Ansi(part.Nno, 18), na1 = Ansi(part.Na1, 40), ty = Ansi(part.Ty, 14),
                fa = Ansi(part.Fa, 8), unit = Ansi(part.Unit, 4), dc = Ansi(part.Dc, 8), oqty, iprj = part.Iprj,
                cno = Ansi(cno, 15), orderdate = DateTime.Today, per = Ansi(per, 10), dpm = Ansi(dept, 10),
            });
    }

    /// <summary>场景 15 旧侧：fr_order4 U1 并入在途（燂.row162:L30：type='历史'、orno=原 cno、cno=''、item 重排）。</summary>
    public static void Order4MergeToTransit(SqlConnection conn, string dept, string cp, string cno)
    {
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        var ids = conn.Query<int>(
            "SELECT id FROM tborder WITH (UPDLOCK) WHERE cno = @cno AND type = '' AND cp = @cp AND @p_dpmusem LIKE '%' + RTRIM(dpm) + '%' ORDER BY id",
            new { cno = Ansi(cno, 15), cp = Ansi(cp, 10), p_dpmusem = Ansi(cp, 50) }).ToList();
        var n = 0;
        foreach (var id in ids)
        {
            n++;
            conn.Execute(
                "UPDATE tborder SET type = '历史', orno = @orno, cno = '', item = @item, orderdate = @orderdate WHERE id = @id",
                new { orno = Ansi(cno, 15), item = n, orderdate = DateTime.Today, id });
        }
    }

    // ================= 场景 26：E 组万能查询族（第十一批） =================

    /// <summary>场景 26 种子：E1/E4/E5/E6/E7/E8/E9/EA 共用回放窗口（cp='回放厂牌R'、dpm=@dept、per='REPLAY'）。
    /// tbsada 种子 kcid=0（sale_in 触发器不动库存）；tbistosav 备份量与 tbisto 实库刻意差 1 → E9 必有差异行。</summary>
    public static void SeedUniversalQuery(SqlConnection conn, string dept, string cp, DateTime date)
    {
        var d = date.Date;
        // 库存（tbisto）：RN26A 批次 100 / RN26B 批次 50，kcbhdate=当天（E6 库存路窗口命中）
        foreach (var (nno, na1, kc) in new[] { ("RN26A", "回放配件R", 100m), ("RN26B", "回放配件R2", 50m) })
            conn.Execute(
                """
                IF NOT EXISTS (SELECT 1 FROM tbisto WHERE rtrim(nno) = @nno AND rtrim(cp) = @cp AND dpm = @dpm AND rtrim(depotno) = '01')
                    INSERT INTO tbisto (cp, nno, na1, ty, unit, kcamount, iprc, iprj, oprc, oprb, posi, depotno, dpm, cxnno, kcbhdate)
                    VALUES (@cp, @nno, @na1, N'回放类型', N'个', @kc, 50, 60, 80, 75, N'A1', '01', @dpm, N'', @kcbhdate)
                """,
                new { nno = Ansi(nno, 18), na1 = Ansi(na1, 40), kc, cp = Ansi(cp, 10), dpm = Ansi(dept, 10), kcbhdate = d });
        // 进库流水（tbistoed）：两行（cno RN26RK1/RK2，当天，已确认）
        var inrows = new[] { (Cno: "RN26RK1", Nno: "RN26A", Na1: "回放配件R", Jk: 10m), (Cno: "RN26RK2", Nno: "RN26B", Na1: "回放配件R2", Jk: 5m) };
        foreach (var r in inrows)
            conn.Execute(
                """
                INSERT INTO tbistoed (cp, nno, na1, ty, unit, jkamount, iprj, iprc, oprc, posi, depotno, dpm, indate, cno, seano, isord,
                                      per, fkfs, pno, gyname, gyno, ywr, note, mark, type, box, dc, fa, qrdate, freight)
                VALUES (@cp, @nno, @na1, N'回放类型', N'个', @jk, 60, 50, 80, N'A1', '01', @dpm, @indate, @cno, N'', N'N',
                        N'REPLAY', N'挂帐', N'PN26', N'回放供应商R', N'GYN26', N'YWR26', N'回放入库备注', N'已确认', N'', N'', N'A', N'回放产地', @qrdate, 0)
                """,
                new { cp = Ansi(cp, 10), nno = Ansi(r.Nno, 18), na1 = Ansi(r.Na1, 40), jk = r.Jk, dpm = Ansi(dept, 10),
                       indate = d, cno = Ansi(r.Cno, 15), qrdate = d });
        // 销售/预售/调拨/历史（tbsada，kcid=0 不动库存；paper RN26*）
        var sarows = new[]
        {
            (Paper: "RN26XS1", Type: "销售", Nno: "RN26A", Na1: "回放配件R", Ck: 2m, Oprc: 100m, Oprct: 90m, Yhl: 10m),
            (Paper: "RN26XS2", Type: "历史", Nno: "RN26B", Na1: "回放配件R2", Ck: 1m, Oprc: 90m, Oprct: 90m, Yhl: 0m),
            (Paper: "RN26XS3", Type: "预售", Nno: "RN26A", Na1: "回放配件R", Ck: 3m, Oprc: 95m, Oprct: 95m, Yhl: 0m),
            (Paper: "RN26XS4", Type: "调拨", Nno: "RN26B", Na1: "回放配件R2", Ck: 2m, Oprc: 85m, Oprct: 85m, Yhl: 0m),
        };
        foreach (var r in sarows)
            conn.Execute(
                """
                INSERT INTO tbsada (cp, nno, na1, ty, unit, type, ckamount, oprc, oprct, iprj, iprc, paper, gno, gname,
                                    per, kcid, outdate, dpm, yhl, guno, mark, depotno, posi)
                VALUES (@cp, @nno, @na1, N'回放类型', N'个', @type, @ck, @oprc, @oprct, 60, 50, @paper, N'RN26G', N'回放客户R',
                        N'REPLAY', 0, @outdate, @dpm, @yhl, N'挂帐', '', '01', N'A1')
                """,
                new { cp = Ansi(cp, 10), nno = Ansi(r.Nno, 18), na1 = Ansi(r.Na1, 40), type = Ansi(r.Type, 10), ck = r.Ck,
                       oprc = r.Oprc, oprct = r.Oprct, paper = Ansi(r.Paper, 15), outdate = d, dpm = Ansi(dept, 10), yhl = r.Yhl });
        // 库存修改（tbistoch）：+3 / -2（当天）
        foreach (var (nno, xg) in new[] { ("RN26A", 3m), ("RN26B", -2m) })
            conn.Execute(
                """
                INSERT INTO tbistoch (cp, nno, na1, ty, xgamount, iprc, xgdate, per, posi, depotno, dpm, note)
                VALUES (@cp, @nno, N'回放配件', N'回放类型', @xg, 50, @xgdate, N'REPLAY', N'A1', '01', @dpm, N'回放修改')
                """,
                new { cp = Ansi(cp, 10), nno = Ansi(nno, 18), xg, xgdate = d, dpm = Ansi(dept, 10) });
        // 出库修改（tbsaxg，无 ty/note 列）：editdate=当天、outdate=昨天（outdate<备份日 条件命中）
        conn.Execute(
            """
            INSERT INTO tbsaxg (cp, nno, na1, type, ckamount, outdate, editdate, per, dpm)
            VALUES (@cp, N'RN26A', N'回放配件R', N'回放类型', 1, @outdate, @editdate, N'REPLAY', @dpm)
            """,
            new { cp = Ansi(cp, 10), outdate = d.AddDays(-1), editdate = d, dpm = Ansi(dept, 10) });
        // 库存备份（tbistosav，无 na1/ty 列）：RN26A=101 / RN26B=49（与 tbisto 100/50 差 1 → E9 差异行）
        foreach (var (nno, kc) in new[] { ("RN26A", 101m), ("RN26B", 49m) })
            conn.Execute(
                """
                INSERT INTO tbistosav (cp, nno, kcamount, iprc, iprj, posi, depotno, dpm, back_date)
                VALUES (@cp, @nno, @kc, 50, 60, N'A1', '01', @dpm, @backdate)
                """,
                new { cp = Ansi(cp, 10), nno = Ansi(nno, 18), kc, dpm = Ansi(dept, 10), backdate = d });
        // 订货（tborder type='历史'）：RN26HT1/RN26HT2
        foreach (var (cno, nno, na1, oq, q) in new[] { ("RN26HT1", "RN26A", "回放配件R", 10m, 3m), ("RN26HT2", "RN26B", "回放配件R2", 8m, 0m) })
            conn.Execute(
                """
                IF NOT EXISTS (SELECT 1 FROM tborder WHERE rtrim(cno) = @cno AND rtrim(nno) = @nno AND dpm = @dpm)
                    INSERT INTO tborder (cp, cno, item, nno, na1, oqty, qty, iprj, orderdate, ty, fa, type, fkfs, per, dpm)
                    VALUES (@cp, @cno, 1, @nno, @na1, @oq, @q, 60, @orderdate, N'回放类型', N'回放产地', '历史', N'挂帐', N'REPLAY', @dpm)
                """,
                new { cp = Ansi(cp, 10), cno = Ansi(cno, 15), nno = Ansi(nno, 18), na1 = Ansi(na1, 40), oq, q,
                       orderdate = d, dpm = Ansi(dept, 10) });
        // 月销售数（tbrtsamo）：一行 12 列，oamou1=30/20（一行 INSERT 后 UPDATE 尾列；yields 月均 30/12、20/12）
        foreach (var (nno, na1, v) in new[] { ("RN26A", "回放配件R", 30m), ("RN26B", "回放配件R2", 20m) })
        {
            conn.Execute(
                "IF NOT EXISTS (SELECT 1 FROM tbrtsamo WHERE rtrim(nno) = @nno AND rtrim(cp) = @cp AND dpm = @dpm) " +
                "INSERT INTO tbrtsamo (dpm, cp, nno, na1, ty, unit, oamou1) VALUES (@dpm, @cp, @nno, @na1, N'回放类型', N'个', @v)",
                new { dpm = Ansi(dept, 10), cp = Ansi(cp, 10), nno = Ansi(nno, 18), na1 = Ansi(na1, 40), v });
        }
    }

    /// <summary>场景 26 旧侧：E1 进库单查询原样重放（fr_seissm row58 P_CA 分支：调用方 dpmusem + tbxzb 双份 + 大 CASE + freight）。</summary>
    public static List<string> UniversalIssmLegacy(SqlConnection conn, string dept, string dpmusem, bool showRemain)
    {
        // 列序与元组一致（Dapper 元组按位置映射）：id,nno,na1,jkamount,iprj,iprc,[xk,xs,freight],indate,cno
        const string cols = "id, rtrim(nno) nno, rtrim(na1) na1, jkamount, iprj, iprc";
        const string remainCase = """
            ,CASE when indate<='1900-01-01' or isnull((select sum(jkamount) from tbistoed a WITH(nolock) where ((id>tbistoed.id and indate=tbistoed.indate) or indate>tbistoed.indate) and nno=tbistoed.nno and dpm=@dpm),0)
                >ISNULL((select sum(kcamount) from tbisto WITH(nolock) where nno=tbistoed.nno and dpm=@dpm),0)
              then 0 else
              CASE when jkamount>ISNULL((select sum(kcamount) from tbisto WITH(nolock) where nno=tbistoed.nno and dpm=@dpm),0)
                -isnull((select sum(jkamount) from tbistoed a WITH(nolock) where ((id>tbistoed.id and indate=tbistoed.indate) or indate>tbistoed.indate) and nno=tbistoed.nno and dpm=@dpm),0)
              then ISNULL((select sum(kcamount) from tbisto WITH(nolock) where nno=tbistoed.nno and dpm=@dpm),0)
                -isnull((select sum(jkamount) from tbistoed a WITH(nolock) where ((id>tbistoed.id and indate=tbistoed.indate) or indate>tbistoed.indate) and nno=tbistoed.nno and dpm=@dpm),0)
              else jkamount end end as xkcamou
            ,CASE when indate<='1900-01-01' or isnull((select sum(jkamount) from tbistoed a WITH(nolock) where ((id>tbistoed.id and indate=tbistoed.indate) or indate>tbistoed.indate) and nno=tbistoed.nno and dpm=@dpm),0)
                >ISNULL((select sum(kcamount) from tbisto WITH(nolock) where nno=tbistoed.nno and dpm=@dpm),0)
              then jkamount-0 else
              CASE when jkamount>ISNULL((select sum(kcamount) from tbisto WITH(nolock) where nno=tbistoed.nno and dpm=@dpm),0)
                -isnull((select sum(jkamount) from tbistoed a WITH(nolock) where ((id>tbistoed.id and indate=tbistoed.indate) or indate>tbistoed.indate) and nno=tbistoed.nno and dpm=@dpm),0)
              then jkamount-(ISNULL((select sum(kcamount) from tbisto WITH(nolock) where nno=tbistoed.nno and dpm=@dpm),0)
                -isnull((select sum(jkamount) from tbistoed a WITH(nolock) where ((id>tbistoed.id and indate=tbistoed.indate) or indate>tbistoed.indate) and nno=tbistoed.nno and dpm=@dpm),0))
              else jkamount-jkamount end end as xsamou
            ,CAST((iprc-CASE WHEN freight=0 THEN iprc ELSE Freight END)*jkamount AS NUMERIC(18,2)) AS freight
            """;
        var colSql = showRemain ? cols + "\n" + remainCase + "\n, indate, rtrim(cno) cno" : cols + "\n, 0 as xkcamou, 0 as xsamou, CAST((iprc-CASE WHEN freight=0 THEN iprc ELSE Freight END)*jkamount AS NUMERIC(18,2)) AS freight, indate, rtrim(cno) cno";
        // fr_find 快捷条件「已确认」(mark='已确认') 且「配件名称 包含 回放」→ (mark='已确认' and na1 like '%回放%')
        var where = "(mark='已确认' and na1 like '%回放%') and @p_dpmusem like '%'+rtrim(dpm)+'%'" +
                    " and @p_dpmusem like '%'+rtrim(dpm)+'%'"; // BUILD_CA tbxzb 再追加一条（双份）
        var rows = conn.Query<(long Id, string Nno, string Na1, decimal Jk, decimal Iprj, decimal Iprc, decimal Xk, decimal Xs, decimal Freight, DateTime Indate, string Cno)>(
            $"SELECT {colSql} FROM tbistoed WITH (NOLOCK) WHERE {where} ORDER BY indate DESC, ID DESC",
            new { dpm = Ansi(dept, 10), p_dpmusem = Ansi(dpmusem, 60) }).AsList();
        var msgs = rows.Select(r => $"ISSM|{r.Id}|{r.Nno}|{r.Na1}|{r.Jk:0.##}|{r.Iprj:0.##}|{r.Iprc:0.##}|{r.Xk:0.##}|{r.Xs:0.##}|{r.Freight:0.##}|{r.Indate:yyyy-MM-dd}|{r.Cno}").ToList();
        // 合计（row62 CHECK1=1、P_WBTOHS 分支、P_IPRCCOUNT=4）
        msgs.Add($"ISSMTOT|{rows.Count}|{rows.Sum(r => (double)Math.Truncate(r.Jk)):0.##}|{rows.Sum(r => (double)r.Xs):0.##}|{rows.Sum(r => (double)r.Xk):0.##}|" +
                 $"{rows.Sum(r => (double)Math.Round(r.Jk * r.Iprj, 2)):0.##}|{rows.Sum(r => (double)Math.Round(r.Iprc * r.Jk, 4)):0.##}|" +
                 $"{rows.Sum(r => (double)Math.Round(r.Iprj * r.Xs, 2)):0.##}|{rows.Sum(r => (double)Math.Round(r.Iprj * r.Xk, 2)):0.##}|{rows.Sum(r => (double)r.Freight):0.##}");
        return msgs;
    }

    /// <summary>场景 26 旧侧：E4 订货单查询（fr_seorder row23：MM and type='历史' + tbxzb cp+dpmusem）。</summary>
    public static List<string> UniversalOrderLegacy(SqlConnection conn, string dept, string cp, string dpmusem)
    {
        var where = "(nno like '%RN26%') and type='历史' and cp=@p_cp and @p_dpmusem like '%'+rtrim(dpm)+'%'";
        var rows = conn.Query<(long Id, string Cno, int Item, string Nno, string Na1, decimal Oq, decimal Q, decimal Zt, decimal Iprj, DateTime Od)>(
            $"""
            SELECT id, rtrim(cno), item, rtrim(nno), rtrim(na1), oqty, qty, oqty - qty AS zt, iprj, orderdate
            FROM tborder WITH (NOLOCK) WHERE {where} ORDER BY id
            """,
            new { p_cp = Ansi(cp, 10), p_dpmusem = Ansi(dpmusem, 60) }).AsList();
        var msgs = rows.Select(r => $"ORD|{r.Id}|{r.Cno}|{r.Item}|{r.Nno}|{r.Na1}|{r.Oq:0.##}|{r.Q:0.##}|{r.Zt:0.##}|{r.Iprj:0.##}|{r.Od:yyyy-MM-dd}").ToList();
        // 合计（row26）
        msgs.Add($"ORDTOT|{rows.Count}|{rows.Sum(r => (double)Math.Truncate(r.Oq)):0.##}|{rows.Sum(r => (double)Math.Round(r.Oq * r.Iprj, 2)):0.##}|" +
                 $"{rows.Sum(r => (double)Math.Truncate(r.Oq - r.Q)):0.##}|{rows.Sum(r => (double)Math.Round((r.Oq - r.Q) * r.Iprj, 2)):0.##}");
        return msgs;
    }

    /// <summary>场景 26 旧侧：E5 库存修改查询（fr_sekcxg row24：日期快捷「本月」区间 + tbxzb dpmusem；row25 益损）。</summary>
    public static List<string> UniversalKcxgLegacy(SqlConnection conn, string dept, string dpmusem, DateTime d1, DateTime d2)
    {
        var monthStart = new DateTime(d1.Year, d1.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var where = $"xgdate>='{monthStart:yyyy.MM.dd}' and xgdate<'{monthEnd.AddDays(1):yyyy.MM.dd}' and @p_dpmusem like '%'+rtrim(dpm)+'%'";
        var rows = conn.Query<(long Id, string Nno, string Na1, string Ty, decimal Xg, decimal Iprc, DateTime Xd, string Per, string Posi, string Dep)>(
            $"""
            SELECT id, rtrim(nno), rtrim(na1), rtrim(ty), xgamount, iprc, xgdate, rtrim(per), rtrim(posi), rtrim(depotno)
            FROM tbistoch WITH (NOLOCK) WHERE {where} ORDER BY id
            """,
            new { p_dpmusem = Ansi(dpmusem, 60) }).AsList();
        var msgs = rows.Select(r => $"KCXG|{r.Id}|{r.Nno}|{r.Na1}|{r.Ty}|{r.Xg:0.##}|{r.Iprc:0.##}|{r.Xd:yyyy-MM-dd}|{r.Per}|{r.Posi}|{r.Dep}").ToList();
        msgs.Add($"KCXGTOT|{rows.Sum(r => (double)r.Xg):0.##}|{rows.Sum(r => (double)r.Iprc):0.##}");
        return msgs;
    }

    /// <summary>
    /// 场景 26 旧侧：E6 商品出入查询（fr_seio row2.sp_io + lx，HOW=nno 编码维度）。
    /// 独立实现旧游标流程：类型行动态取 tbsysset type='xs'，APPEND FROM 同名列对齐（调拔/进库成本错位丢弃），汇总+合计行，order by cp。
    /// 返回（中间值消息, 最终行消息）——中间值断言防"两侧同错"（ADDSUB 教训）。
    /// </summary>
    public static (List<string> Src, List<string> Rows) UniversalIoLegacy(SqlConnection conn, string dept, string cp, string dpmusem, DateTime d1, DateTime d2, string dimTag)
    {
        var ft = $"outdate between '{d1:yyyy-MM-dd}' and '{d2.AddDays(1):yyyy-MM-dd}' and {dimTag}";
        var src = new List<string>();
        var msgs = new List<string>();
        var how = "nno";

        // 类型集（PASSTH tbsysset：where (type='xs' and name<>'报价') and dpm=?p_dpm）
        var types = conn.Query<string>(
            "SELECT rtrim(name) FROM tbsysset WITH (NOLOCK) WHERE (type='xs' and name<>'报价') and dpm=@dpm",
            new { dpm = Ansi(dept, 10) }).AsList();

        // 中间值断言消息（旧码 c_tmpy1 逐类型聚合行——回放通过≠算法正确的证据链）
        foreach (var lx in types)
        {
            var rows = conn.Query<(string How, decimal N, decimal Cost, decimal Amt, decimal Ml, decimal Mlr)>(
                $"""
                SELECT rtrim({how}), SUM(ckamount), SUM(ckamount * iprc), SUM(ckamount * oprct * (100 - yhl) / 100),
                       ISNULL(SUM(ckamount * oprct * (100 - yhl) / 100) - SUM(ckamount * iprc), 0),
                       ISNULL((SUM(ckamount * oprct * (100 - yhl) / 100) - SUM(ckamount * iprc)) / NULLIF(SUM(ckamount * iprc), 0) * 100, 0)
                FROM tbsada WITH (NOLOCK)
                WHERE type = @lx and cp = @cp and {ft}
                GROUP BY rtrim({how}), cp, type
                """,
                new { lx = Ansi(lx, 14), cp = Ansi(cp, 10) }).AsList();
            foreach (var r in rows)
                src.Add($"IO-SRC|{lx}|{r.How}|{r.N:0.##}|{r.Cost:0.##}|{r.Amt:0.##}|{r.Ml:0.##}|{r.Mlr:0.####}");
        }
        // 进库（tbistoed：cp=?p_cp 命中 cp 规则跳过 → 仅 dpmusem）
        var inRows = conn.Query<(string How, decimal N, decimal Amt)>(
            $"""
            SELECT rtrim({how}), SUM(jkamount), SUM(jkamount * oprc)
            FROM tbistoed WITH (NOLOCK)
            WHERE cp = @cp and indate between '{d1:yyyy-MM-dd}' and '{d2.AddDays(1):yyyy-MM-dd}' and {dimTag.Replace("outdate", "indate")}
              and @p_dpmusem like '%'+rtrim(dpm)+'%'
            GROUP BY rtrim({how}), cp
            """,
            new { cp = Ansi(cp, 10), p_dpmusem = Ansi(dpmusem, 60) }).AsList();
        foreach (var r in inRows) src.Add($"IO-SRC|进库|{r.How}|{r.N:0.##}||{r.Amt:0.##}");
        // 库存（tbisto：cp/dpmusem 两规则均追加）
        var stoRows = conn.Query<(string How, decimal N)>(
            $"""
            SELECT rtrim({how}), SUM(kcamount)
            FROM tbisto WITH (NOLOCK)
            WHERE kcbhdate between '{d1:yyyy-MM-dd}' and '{d2.AddDays(1):yyyy-MM-dd}' and {dimTag.Replace("outdate", "kcbhdate")}
              and cp = @cp and @p_dpmusem like '%'+rtrim(dpm)+'%'
            GROUP BY rtrim({how}), cp
            """,
            new { cp = Ansi(cp, 10), p_dpmusem = Ansi(dpmusem, 60) }).AsList();
        foreach (var r in stoRows) src.Add($"IO-SRC|库存|{r.How}|{r.N:0.##}");

        // 应用层拼合（APPEND FROM 同名匹配语义）
        var acc = new Dictionary<string, (decimal Sl, decimal Cb, decimal E, decimal Jn, decimal Je, decimal Kc, decimal Ml, decimal Mlr, decimal Ys, decimal Yc, decimal Ye)>();
        void Add(string key, int col, decimal v)
        {
            var cur = acc.TryGetValue(key, out var t) ? t : default;
            acc[key] = col switch
            {
                1 => cur with { Sl = cur.Sl + v }, 2 => cur with { Cb = cur.Cb + v }, 3 => cur with { E = cur.E + v },
                4 => cur with { Jn = cur.Jn + v }, 5 => cur with { Je = cur.Je + v }, 6 => cur with { Kc = cur.Kc + v },
                7 => cur with { Ml = cur.Ml + v }, 8 => cur with { Mlr = cur.Mlr + v },
                9 => cur with { Ys = cur.Ys + v }, 10 => cur with { Yc = cur.Yc + v }, 11 => cur with { Ye = cur.Ye + v },
                _ => cur,
            };
        }
        foreach (var lx in types)
        {
            var rows = conn.Query<(string How, decimal N, decimal Cost, decimal Amt, decimal Ml, decimal Mlr)>(
                $"""
                SELECT rtrim({how}), SUM(ckamount), SUM(ckamount * iprc), SUM(ckamount * oprct * (100 - yhl) / 100),
                       ISNULL(SUM(ckamount * oprct * (100 - yhl) / 100) - SUM(ckamount * iprc), 0),
                       ISNULL((SUM(ckamount * oprct * (100 - yhl) / 100) - SUM(ckamount * iprc)) / NULLIF(SUM(ckamount * iprc), 0) * 100, 0)
                FROM tbsada WITH (NOLOCK)
                WHERE type = @lx and cp = @cp and {ft}
                GROUP BY rtrim({how}), cp, type
                """,
                new { lx = Ansi(lx, 14), cp = Ansi(cp, 10) }).AsList();
            foreach (var r in rows)
            {
                switch (lx)
                {
                    case "销售":
                        Add(r.How, 1, r.N); Add(r.How, 2, r.Cost); Add(r.How, 3, r.Amt); Add(r.How, 7, r.Ml); Add(r.How, 8, r.Mlr);
                        break;
                    case "预售":
                        Add(r.How, 9, r.N); Add(r.How, 10, r.Cost); Add(r.How, 11, r.Amt); Add(r.How, 7, r.Ml); Add(r.How, 8, r.Mlr);
                        break;
                    default:
                        Add(r.How, 7, r.Ml); Add(r.How, 8, r.Mlr); // 调拨/历史：数量列名 拨/拔 错位恒 0（旧码 bug 照抄）
                        break;
                }
            }
        }
        foreach (var r in inRows) { Add(r.How, 4, r.N); Add(r.How, 5, r.Amt); }
        foreach (var r in stoRows) Add(r.How, 6, r.N);

        var rows2 = acc.Select(kv => (How: kv.Key, kv.Value)).ToList();
        rows2.Add((How: "[合计]", Value: (
            rows2.Sum(x => x.Value.Sl), rows2.Sum(x => x.Value.Cb), rows2.Sum(x => x.Value.E),
            rows2.Sum(x => x.Value.Jn), rows2.Sum(x => x.Value.Je), rows2.Sum(x => x.Value.Kc),
            rows2.Sum(x => x.Value.Ml), rows2.Sum(x => x.Value.Mlr),
            rows2.Sum(x => x.Value.Ys), rows2.Sum(x => x.Value.Yc), rows2.Sum(x => x.Value.Ye))));
        foreach (var r in rows2.OrderBy(x => "")) // 最终 select order by cp（合计 cp='' 排首，本查询全部行 cp 同值——稳定序按插入序）
            msgs.Add($"IO|{r.How}|{r.Value.Sl:0.##}|{r.Value.Cb:0.##}|{r.Value.E:0.##}|{r.Value.Jn:0.##}|{r.Value.Je:0.##}|{r.Value.Kc:0.##}|{r.Value.Ml:0.##}|" +
                     $"{(r.Value.Cb == 0 ? "" : (r.Value.Ml / r.Value.Cb * 100).ToString("0.00") + "%")}|{r.Value.Ys:0.##}|{r.Value.Yc:0.##}|{r.Value.Ye:0.##}|0|0|0");
        return (src, msgs);
    }

    /// <summary>场景 26 旧侧：E7 月销售数（fr_sertmo row54：nno 前缀筛选 + tbxzb dpmusem，ORDER BY dpm,nno）。</summary>
    public static List<string> UniversalRtmoLegacy(SqlConnection conn, string dept, string dpmusem)
    {
        var rows = conn.Query<(long Id, string Dpm, string Cp, string Nno, string Na1, string Ty, string Unit,
            decimal O1, decimal O2, decimal O3, decimal O4, decimal O5, decimal O6, decimal O7, decimal O8, decimal O9, decimal O10, decimal O11, decimal O12, decimal Avg)>(
            """
            SELECT id, rtrim(dpm), rtrim(cp), rtrim(nno), rtrim(na1), rtrim(ty), rtrim(unit),
                   oamou1, oamou2, oamou3, oamou4, oamou5, oamou6, oamou7, oamou8, oamou9, oamou10, oamou11, oamou12,
                   (oamou1 + oamou2 + oamou3 + oamou4 + oamou5 + oamou6 + oamou7 + oamou8 + oamou9 + oamou10 + oamou11 + oamou12) / 12
            FROM tbrtsamo WITH (NOLOCK)
            WHERE (nno between 'RN26A' and 'RN26Cz') and @p_dpmusem like '%'+rtrim(dpm)+'%'
            ORDER BY dpm, nno
            """,
            new { p_dpmusem = Ansi(dpmusem, 60) }).AsList();
        return rows.Select(r =>
            $"RTMO|{r.Id}|{r.Dpm}|{r.Cp}|{r.Nno}|{r.Na1}|{r.Ty}|{r.Unit}|{r.O1:0.##}|{r.O2:0.##}|{r.O3:0.##}|{r.O4:0.##}|{r.O5:0.##}|{r.O6:0.##}|{r.O7:0.##}|{r.O8:0.##}|{r.O9:0.##}|{r.O10:0.##}|{r.O11:0.##}|{r.O12:0.##}|{r.Avg:0.####}").ToList();
    }

    /// <summary>场景 26 旧侧：E8 营业额（fr_sesada row33 P_FGS 分支：取数+GROUP BY outdate,dpm 名+牟利率+合计行）。</summary>
    public static List<string> UniversalSesadaLegacy(SqlConnection conn, string dept, string dpmusem, DateTime d1, DateTime d2, string cldpm)
    {
        var hasDpm = cldpm != "*";
        var where = $"""
            (type='销售'or type='历史') and outdate>=@d1 and outdate<@d2 {(hasDpm ? "and dpm=@cldpm" : "")} and @p_dpmusem like '%'+rtrim(dpm)+'%'
            """;
        var dpmExpr = cldpm == "*"
            ? "(select dpm from tbdpm where dpmbh = tbsada.dpm)"
            : "(select top 1 dpm from tbdpm where dpmbh = @cldpm)";
        var rows = conn.Query<(DateTime Outdate, string Dpm, int N, decimal Am, decimal J, decimal R, decimal Se, decimal Sse, decimal Ml)>(
            $"""
            SELECT outdate, dpmname, COUNT(DISTINCT paper), SUM(ckamount),
                   ROUND(SUM(ckamount * iprj), 2), ROUND(SUM(ckamount * iprc), 2),
                   ROUND(SUM(ckamount * oprc * (1 - yhl / 100)), 2), ROUND(SUM(ckamount * oprct * (1 - yhl / 100)), 2),
                   ROUND(SUM(ckamount * oprct * (1 - yhl / 100)), 2) - ROUND(SUM(ckamount * iprj), 2)
            FROM (SELECT outdate, {dpmExpr} AS dpmname, paper, ckamount, iprj, iprc, oprct, yhl, oprc
                  FROM tbsada WITH (NOLOCK) WHERE {where}) x
            GROUP BY outdate, dpmname
            ORDER BY outdate
            """,
            new { d1 = d1.Date, d2 = d2.Date.AddDays(1), cldpm = Ansi(cldpm, 10), p_dpmusem = Ansi(dpmusem, 60) }).AsList();
        var msgs = rows.Select(r =>
        {
            var mll = r.Sse != 0 ? Math.Round(r.Ml / r.Sse * 100, 2) : 0;
            return $"SESADA|{r.Outdate:yyyy.MM.dd}|{r.Dpm?.Trim() ?? ""}|{r.N}|{r.Am:0.##}|{r.J:0.##}|{r.R:0.##}|{r.Se:0.##}|{r.Sse:0.##}|{r.Ml:0.##}|{mll:0.##}";
        }).ToList();
        var (N, Am, J, R, Se, Sse, Ml) = (rows.Sum(r => r.N), rows.Sum(r => r.Am), rows.Sum(r => r.J), rows.Sum(r => r.R), rows.Sum(r => r.Se), rows.Sum(r => r.Sse), rows.Sum(r => r.Ml));
        var totMll = Sse != 0 ? Ml / Sse * 100 : 0;
        msgs.Add($"SESADA|[合计]||{N}|{Am:0.##}|{J:0.##}|{R:0.##}|{Se:0.##}|{Sse:0.##}|{Ml:0.##}|{totMll:0.##}");
        return msgs;
    }

    /// <summary>场景 26 旧侧：E9 库存检测（fr_secheck row27：全局汇总 + 逐品差异，tbisto 扫描序）。</summary>
    public static List<string> UniversalCheckLegacy(SqlConnection conn, string dept, string cp, DateTime date)
    {
        var msgs = new List<string>();
        var p = new { mdate = date.Date, cp = Ansi(cp, 10), dpm = Ansi(dept, 10) };
        var savA = conn.ExecuteScalar<decimal?>("SELECT SUM(kcamount) FROM tbistosav WITH (NOLOCK) WHERE back_date=@mdate and cp=@cp and dpm=@dpm", p) ?? 0m;
        var hasBackup = conn.ExecuteScalar<int>("SELECT CASE WHEN EXISTS (SELECT 1 FROM tbistosav WITH (NOLOCK) WHERE back_date=@mdate and cp=@cp and dpm=@dpm) THEN 1 ELSE 0 END", p) > 0;
        if (!hasBackup)
        {
            msgs.Add("CHK|NOBACKUP");
            return msgs;
        }
        decimal Sum(string sql) => conn.ExecuteScalar<decimal?>(sql, p) ?? 0m;
        var istoA = Sum("SELECT SUM(kcamount) FROM tbisto WITH (NOLOCK) WHERE cp=@cp and dpm=@dpm");
        var saedA = Sum("SELECT SUM(ckamount) FROM tbsada WITH (NOLOCK) WHERE type='历史' and outdate>=@mdate and cp=@cp and dpm=@dpm");
        var sadaAll = Sum("SELECT SUM(ckamount) FROM tbsada WITH (NOLOCK) WHERE type='销售' and outdate>=@mdate and cp=@cp and dpm=@dpm");
        var sadaA = Sum("""
            SELECT SUM(ckamount) FROM tbsada WITH (NOLOCK)
            WHERE type='销售' and outdate>=@mdate and cp=@cp and dpm=@dpm
              and (left(note,5)!='预售转销售' or (left(note,5)='预售转销售' and convert(datetime,SUBSTRING(note,6,10))>=@mdate))
            """);
        var sabeA = Sum("SELECT SUM(ckamount) FROM tbsada WITH (NOLOCK) WHERE type='预售' and outdate>=@mdate and cp=@cp and dpm=@dpm");
        var sadbA = Sum("SELECT SUM(ckamount) FROM tbsada WITH (NOLOCK) WHERE type='调拨' and outdate>=@mdate and cp=@cp and dpm=@dpm");
        var xlsaA = Sum("SELECT SUM(ckamount) FROM tbxlsa WITH (NOLOCK) WHERE type='领料' and outdate>=@mdate and cp=@cp and dpm=@dpm");
        var istochA = Sum("SELECT SUM(xgamount) FROM tbistoch WITH (NOLOCK) WHERE xgdate>=@mdate and cp=@cp and dpm=@dpm");
        var saxgA = Sum("SELECT SUM(ckamount) FROM tbsaxg WITH (NOLOCK) WHERE editdate>=@mdate and outdate<@mdate and cp=@cp and dpm=@dpm");
        var istoedA = Sum("SELECT SUM(jkamount) FROM tbistoed WITH (NOLOCK) WHERE indate>=@mdate and cp=@cp and dpm=@dpm");
        var nowA = savA + istoedA - saedA - sadaA - sabeA - sadbA - xlsaA + istochA - saxgA;
        msgs.Add($"CHK-TOT|{savA:0.##}|{istoedA:0.##}|{saedA:0.##}|{sadaA:0.##}|{sadaAll - sadaA:0.##}|{sabeA:0.##}|{sadbA:0.##}|{xlrsa(xlsaA)}|{istochA:0.##}|{saxgA:0.##}|{nowA:0.##}|{istoA:0.##}|{istoA - nowA:0.##}");
        static string xlrsa(decimal v) => v.ToString("0.##");

        var istoRows = conn.Query<(long Id, string Nno, string Na1, string Ty, decimal Kc)>(
            "SELECT id, rtrim(nno), rtrim(na1), rtrim(ty), kcamount FROM tbisto WITH (NOLOCK) WHERE cp=@cp and dpm=@dpm ORDER BY id", p).AsList();
        Dictionary<string, decimal> G(string sql) => conn.Query<(string Nno, decimal S)>(sql, p).AsList().ToDictionary(x => x.Nno, x => x.S);
        var savB = G("SELECT rtrim(nno), SUM(kcamount) FROM tbistosav WITH (NOLOCK) WHERE back_date=@mdate and cp=@cp and dpm=@dpm GROUP BY rtrim(nno)");
        var saedB = G("SELECT rtrim(nno), SUM(ckamount) FROM tbsada WITH (NOLOCK) WHERE type='历史' and outdate>=@mdate and cp=@cp and dpm=@dpm GROUP BY rtrim(nno)");
        var sadaB = G("""
            SELECT rtrim(nno), SUM(ckamount) FROM tbsada WITH (NOLOCK)
            WHERE type='销售' and outdate>=@mdate and cp=@cp and dpm=@dpm
              and (left(note,5)!='预售转销售' or (left(note,5)='预售转销售' and convert(datetime,SUBSTRING(note,6,10))>=@mdate))
            GROUP BY rtrim(nno)
            """);
        var sabeB = G("SELECT rtrim(nno), SUM(ckamount) FROM tbsada WITH (NOLOCK) WHERE type='预售' and outdate>=@mdate and cp=@cp and dpm=@dpm GROUP BY rtrim(nno)");
        var sadbB = G("SELECT rtrim(nno), SUM(ckamount) FROM tbsada WITH (NOLOCK) WHERE type='调拨' and outdate>=@mdate and cp=@cp and dpm=@dpm GROUP BY rtrim(nno)");
        var xlsaB = G("SELECT rtrim(nno), SUM(ckamount) FROM tbxlsa WITH (NOLOCK) WHERE type='领料' and outdate>=@mdate and cp=@cp and dpm=@dpm GROUP BY rtrim(nno)");
        var istochB = G("SELECT rtrim(nno), SUM(xgamount) FROM tbistoch WITH (NOLOCK) WHERE xgdate>=@mdate and cp=@cp and dpm=@dpm GROUP BY rtrim(nno)");
        var saxgB = G("SELECT rtrim(nno), SUM(ckamount) FROM tbsaxg WITH (NOLOCK) WHERE editdate>=@mdate and outdate<@mdate and cp=@cp and dpm=@dpm GROUP BY rtrim(nno)");
        var istoedB = G("SELECT rtrim(nno), SUM(jkamount) FROM tbistoed WITH (NOLOCK) WHERE indate>=@mdate and cp=@cp and dpm=@dpm GROUP BY rtrim(nno)");
        var istoB = istoRows.GroupBy(r => r.Nno).ToDictionary(g => g.Key, g => g.Sum(x => x.Kc));
        foreach (var r in istoRows)
        {
            var savM = savB.GetValueOrDefault(r.Nno);
            var istodM = istoedB.GetValueOrDefault(r.Nno);
            var saedM = saedB.GetValueOrDefault(r.Nno);
            var sadaM = sadaB.GetValueOrDefault(r.Nno);
            var sabeM = sabeB.GetValueOrDefault(r.Nno);
            var sadbM = sadbB.GetValueOrDefault(r.Nno);
            var xlsaM = xlsaB.GetValueOrDefault(r.Nno);
            var istochM = istochB.GetValueOrDefault(r.Nno);
            var saxgM = saxgB.GetValueOrDefault(r.Nno);
            var istoM = istoB.GetValueOrDefault(r.Nno);
            var nowM = savM + istodM - saedM - sadaM - sabeM - sadbM - xlsaM + istochM - saxgM;
            if (nowM != istoM)
                msgs.Add($"CHK|{r.Nno}|{r.Na1}|{r.Ty}|{savM:0.##}|{sadaM + saedM + sabeM + sadbM + xlsaM:0.##}|{istodM:0.##}|{istochM:0.##}|{saxgM:0.##}|{istoM:0.##}");
        }
        return msgs;
    }

    /// <summary>场景 26 旧侧：EA 进出流水帐（fr_seis row51：结存/进库/修改/销售/领料五路 + 倒序滚动）。</summary>
    public static List<string> UniversalIsioLegacy(SqlConnection conn, string dept, string cp, string dpmusem, string nno, DateTime d1, DateTime d2)
    {
        var d2End = d2.Date.AddDays(1);
        var msgs = new List<string>();
        var istoP = new { cp = Ansi(cp, 10), nno = Ansi(nno, 18), dpm = Ansi(dept, 10), d1 = d1.Date, d2 = d2End, p_dpmusem = Ansi(dpmusem, 60) };
        var isto = conn.Query<(string Na1, string Ty)>(
            "SELECT rtrim(na1), rtrim(ty) FROM tbisto WITH (NOLOCK) WHERE cp=@cp and nno=@nno And Dpm = @dpm and @p_dpmusem like '%'+rtrim(dpm)+'%'",
            istoP).AsList();
        if (isto.Count == 0) { msgs.Add("ISIO|BADNNO"); return msgs; }

        decimal amount, iprcAmt, iprjAmt;
        if (d2End > DateTime.Today)
        {
            amount = conn.ExecuteScalar<decimal?>("SELECT SUM(kcamount) FROM tbisto WITH (NOLOCK) WHERE cp=@cp and nno=@nno And Dpm = @dpm and @p_dpmusem like '%'+rtrim(dpm)+'%'", istoP) ?? 0m;
            iprcAmt = conn.ExecuteScalar<decimal?>("SELECT SUM(iprc * kcamount) FROM tbisto WITH (NOLOCK) WHERE cp=@cp and nno=@nno And Dpm = @dpm and @p_dpmusem like '%'+rtrim(dpm)+'%'", istoP) ?? 0m;
            iprjAmt = conn.ExecuteScalar<decimal?>("SELECT SUM(iprj * kcamount) FROM tbisto WITH (NOLOCK) WHERE cp=@cp and nno=@nno And Dpm = @dpm and @p_dpmusem like '%'+rtrim(dpm)+'%'", istoP) ?? 0m;
        }
        else
        {
            const string savSql = """
                SELECT SUM({col}) FROM tbistosav WITH (NOLOCK)
                WHERE cp=@cp and nno=@nno And Dpm = @dpm and @p_dpmusem like '%'+rtrim(dpm)+'%'
                  and back_date=(select top 1 back_date from tbistosav where back_date>=@d1 and back_date<=@d2 order by back_date desc)
                """;
            amount = conn.ExecuteScalar<decimal?>(savSql.Replace("{col}", "kcamount"), istoP) ?? 0m;
            iprcAmt = conn.ExecuteScalar<decimal?>(savSql.Replace("{col}", "iprc * kcamount"), istoP) ?? 0m;
            iprjAmt = conn.ExecuteScalar<decimal?>(savSql.Replace("{col}", "iprj * kcamount"), istoP) ?? 0m;
        }

        var rows = new List<(string Per, string No, DateTime? Date, string Mark, decimal Jq, decimal Cq, decimal? Amt, string Note, string Posi, decimal? Iprc, string Dep, decimal? Iprj)>
        {
            (Per: "", No: "", Date: d1.Date, Mark: "结存", Jq: 0m, Cq: 0m, Amt: amount, Note: "", Posi: "", Iprc: null, Dep: "", Iprj: null),
        };
        foreach (var r in conn.Query<(decimal Jk, DateTime D, string Per, string Cno, string Gyn, string Posi, decimal Iprc, decimal Iprj, string Dep)>(
            """
            SELECT jkamount, indate, rtrim(per), rtrim(cno), rtrim(gyname), rtrim(posi), iprc, iprj, rtrim(depotno), id
            FROM tbistoed WITH (NOLOCK)
            WHERE cp=@cp and nno=@nno and indate>=@d1 and indate<@d2 And Dpm = @dpm and @p_dpmusem like '%'+rtrim(dpm)+'%'
            ORDER BY id
            """, istoP))
            rows.Add((Per: r.Per, No: r.Cno, Date: r.D, Mark: "进库", Jq: r.Jk, Cq: 0m, Amt: null, Note: r.Gyn, Posi: r.Posi, Iprc: r.Iprc, Dep: r.Dep, Iprj: r.Iprj));
        foreach (var r in conn.Query<(decimal Xg, DateTime D, string Per, string Posi, decimal Iprc)>(
            """
            SELECT xgamount, xgdate, rtrim(per), rtrim(posi), iprc, id FROM tbistoch WITH (NOLOCK)
            WHERE cp=@cp and nno=@nno and xgdate>=@d1 and xgdate<@d2 And Dpm = @dpm and @p_dpmusem like '%'+rtrim(dpm)+'%'
            ORDER BY id
            """, istoP))
            rows.Add((Per: r.Per, No: r.Posi, Date: r.D, Mark: "修改", Jq: r.Xg, Cq: 0m, Amt: null, Note: "", Posi: r.Posi, Iprc: r.Iprc, Dep: "", Iprj: 0m));
        foreach (var r in conn.Query<(decimal Ck, DateTime D, string Per, string Paper, string Type, string Gn, string Posi, decimal Iprc, decimal Iprj, string Dep)>(
            """
            SELECT ckamount, outdate, rtrim(per), rtrim(paper), rtrim(type), rtrim(gname), rtrim(posi), iprc, iprj, rtrim(depotno), id
            FROM tbsada WITH (NOLOCK)
            WHERE cp=@cp and nno=@nno and outdate>=@d1 and outdate<@d2 and type in ('销售','历史','预售','调拨') And Dpm = @dpm and @p_dpmusem like '%'+rtrim(dpm)+'%'
            ORDER BY id
            """, istoP))
            rows.Add((Per: r.Per, No: r.Paper, Date: r.D, Mark: r.Ck < 0 ? "退" : (r.Type.Length > 4 ? r.Type[..4] : r.Type), Jq: 0m, Cq: r.Ck, Amt: null, Note: r.Gn, Posi: r.Posi, Iprc: r.Iprc, Dep: r.Dep, Iprj: r.Iprj));
        foreach (var r in conn.Query<(decimal Ck, DateTime D, string Per, string Paper, string Type, string Gn, string Bz, string Gh, string Posi, decimal Iprc, decimal Iprj, string Dep)>(
            """
            SELECT ckamount, outdate, rtrim(per), rtrim(paper), rtrim(type), rtrim(gname), rtrim(bz), rtrim(gh), rtrim(posi), iprc, iprj, rtrim(depotno), id
            FROM tbxlsa WITH (NOLOCK)
            WHERE cp=@cp and nno=@nno and outdate>=@d1 and outdate<@d2 and type<>'维修报价' And Dpm = @dpm and @p_dpmusem like '%'+rtrim(dpm)+'%'
            ORDER BY id
            """, istoP))
            rows.Add((Per: r.Per, No: r.Paper, Date: r.D, Mark: r.Ck < 0 ? "退" : (r.Type.Length > 4 ? r.Type[..4] : r.Type), Jq: 0m, Cq: r.Ck, Amt: null, Note: $"{r.Gn},{r.Bz},{r.Gh}", Posi: r.Posi, Iprc: r.Iprc, Dep: r.Dep, Iprj: r.Iprj));

        var oN = amount;
        var mIprc = iprcAmt;
        var mIprj = iprjAmt;
        // 倒序滚动（row51 L79-89：GO BOTTOM 向上；AMOUNT=O_N；随后 O_N -= JQTY-CQTY、M_IPRJ/M_IPRC 同步）
        var calc = new (decimal Amt, decimal JcAmt, decimal WbAmt)[rows.Count];
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            var row = rows[i];
            calc[i] = (oN, mIprc, mIprj);
            var jq = row.Mark == "结存" ? 0m : row.Jq; // 期初结存行 JQTY 置 0（旧码 GO TOP 后 REPLACE JQTY WITH 0）
            oN = oN - jq + row.Cq;
            mIprj = mIprj - jq * (row.Iprj ?? 0) + row.Cq * (row.Iprj ?? 0);
            mIprc = mIprc - jq * (row.Iprc ?? 0) + row.Cq * (row.Iprc ?? 0);
        }
        // 显示序 = INDEX ON DATE 索引序（同日按物理插入序，正序输出）
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            msgs.Add($"ISIO|{row.Per}|{row.No}|{row.Date:yyyy-MM-dd}|{row.Mark}|{row.Jq:0.##}|{row.Cq:0.##}|{calc[i].Amt:0.##}|{row.Note}|{row.Iprc:0.##}|{calc[i].JcAmt:0.##}|{row.Dep}|{row.Iprj:0.##}|{calc[i].WbAmt:0.##}|{row.Posi}");
        }
        return msgs;
    }

    // ================= 场景 27/28：A 组属性定义族（第十二批） =================

    /// <summary>场景 27 种子：tbprnoty 回放行（RN27 系列）+ tbisto/tborder 目标行 + tbcode 品名缺行。</summary>
    public static void SeedAttrGroup(SqlConnection conn, string dept, string cp, DateTime date)
    {
        var d = date.Date;
        // tbprnoty 属性行 RN27A（已存在，供改码/通用码/传递链）——cp 条件先查防重复种子
        conn.Execute(
            """
            IF NOT EXISTS (SELECT 1 FROM tbprnoty WHERE rtrim(nno)='RN27A' AND rtrim(cp)=@cp)
                INSERT INTO tbprnoty (cp, nno, na1, ty, fa, dc, unit, iprj, code_ts, cxnno, xno, tno, uqty, note, note1, oprc_cp, oprc_min, nvol, suttle, eng)
                VALUES (@cp, 'RN27A', N'回放配件A', N'回放车型', N'回放产地', N'A1', N'个', 60, N'', N'', 0, 0, 0, N'回放备注', N'回放备注1', 95, 88, 5, 5.5, N'ENG27A')
            """,
            new { cp = Ansi(cp, 10) });
        // tbprnoty 属性行 RN27B（供组套组件）
        conn.Execute(
            """
            IF NOT EXISTS (SELECT 1 FROM tbprnoty WHERE rtrim(nno)='RN27B' AND rtrim(cp)=@cp)
                INSERT INTO tbprnoty (cp, nno, na1, ty, fa, dc, unit, iprj, cxnno, xno, tno)
                VALUES (@cp, 'RN27B', N'回放配件B', N'回放车型', N'回放产地', N'A2', N'个', 40, N'', 0, 0)
            """,
            new { cp = Ansi(cp, 10) });
        // tbisto：RN27A 批次（改码/属性传递目标）+ RN27B 批次（组套组件库存）
        foreach (var (nno, kc, iprc, oprc) in new[] { ("RN27A", 20m, 50m, 80m), ("RN27B", 10m, 40m, 70m) })
            conn.Execute(
                """
                IF NOT EXISTS (SELECT 1 FROM tbisto WHERE rtrim(nno)=@nno AND rtrim(cp)=@cp AND dpm=@dpm AND rtrim(depotno)='01')
                    INSERT INTO tbisto (dpm, cp, nno, na1, ty, fa, dc, unit, kcamount, iprj, iprc, oprc, oprb, posi, depotno, cxnno, xno, tno, note)
                    VALUES (@dpm, @cp, @nno, N'旧名称', N'旧车型', N'旧产地', N'Z9', N'箱', @kc, 55, @iprc, @oprc, 78, N'A1', '01', N'OLDCX', 999, 999, N'旧备注')
                """,
                new { dpm = Ansi(dept, 10), cp = Ansi(cp, 10), nno = Ansi(nno, 18), kc, iprc, oprc });
        // tborder 行（EditRec 目标，type<>历史）
        conn.Execute(
            """
            IF NOT EXISTS (SELECT 1 FROM tborder WHERE rtrim(nno)='RN27A' AND rtrim(cp)=@cp AND dpm=@dpm)
                INSERT INTO tborder (dpm, cp, nno, na1, ty, fa, dc, unit, note, cxnno, oqty, qty, iprj, orderdate, type, per)
                VALUES (@dpm, @cp, 'RN27A', N'旧名称', N'旧车型', N'旧产地', N'Z9', N'箱', N'旧备注', N'OLDCX', 3, 0, 55, @od, '', N'REPLAY')
            """,
            new { dpm = Ansi(dept, 10), cp = Ansi(cp, 10), od = d });
        // tbistosm 行（属性传递目标）
        conn.Execute(
            """
            IF NOT EXISTS (SELECT 1 FROM tbistosm WHERE rtrim(nno)='RN27A' AND rtrim(cp)=@cp AND dpm=@dpm)
                INSERT INTO tbistosm (dpm, cp, nno, na1, ty, fa, dc, unit, jkamount, iprj, iprc, oprc, depotno, cxnno, note)
                VALUES (@dpm, @cp, 'RN27A', N'旧名称', N'旧车型', N'旧产地', N'Z9', N'箱', 5, 55, 50, 80, '01', N'OLDCX', N'旧备注')
            """,
            new { dpm = Ansi(dept, 10), cp = Ansi(cp, 10) });
        // tbcomplex 回放税则号
        conn.Execute(
            """
            IF NOT EXISTS (SELECT 1 FROM tbcomplex WHERE rtrim(code_ts)='RN27TS01')
                INSERT INTO tbcomplex (code_ts, g_name, low_rate, high_rate) VALUES ('RN27TS01', N'回放税则品名', 0.05, 0.10)
            """,
            new { });
        // tbname0 清理回放名（幂等）
        conn.Execute("DELETE FROM tbname0 WHERE rtrim(name) LIKE N'回放配件%'");
        // tbprtj 清理回放套件（幂等）
        conn.Execute("DELETE FROM tbprtj WHERE rtrim(tj) LIKE 'T-RN27%'");
    }

    /// <summary>场景 27 旧侧：A1 属性库保存链原样重放（INSERT 24 列 / UPDATE+updatenno+EDITREC / max 码 / tbname0 同步）。</summary>
    public static List<string> AttrPrnotyLegacy(SqlConnection conn, string dept, string cp, DateTime date)
    {
        var msgs = new List<string>();
        var d = date.Date;
        // —— add：INSERT 24 列（tbprnoty 无 dpm 列不补；触发器 insert_cxnno 回填 cxnno/xno/tno）——
        conn.Execute(
            """
            INSERT INTO tbprnoty (cp, nno, na1, ty, fa, dc, unit, iprj, code_ts, eng, nnp, nnq, uqty, xno, tno, amjj, bmjj, nvol, suttle, note, note1, oprc_cp, oprc_min, cxnno)
            VALUES (@cp, 'RN27NEW', N'回放配件NEW', N'回放车型', N'回放产地', N'A3', N'个', 70, N'RN27TS01', N'ENGNEW', N'', N'', 2, 0, 0, 0, 0, 3, 3.5, N'新增备注', N'新增备注1', 92, 85, N'RN27NEWCX')
            """,
            new { cp = Ansi(cp, 10) });
        var newId = conn.ExecuteScalar<long>("SELECT id FROM tbprnoty WHERE rtrim(nno)='RN27NEW' AND rtrim(cp)=@cp", new { cp = Ansi(cp, 10) });
        var cx = conn.ExecuteScalar<string>("SELECT rtrim(cxnno) FROM tbprnoty WHERE id=@id", new { id = newId });
        var xno = conn.ExecuteScalar<int>("SELECT ISNULL(xno,0) FROM tbprnoty WHERE id=@id", new { id = newId });
        msgs.Add($"PRNOTY-ADD|{newId}|{cx}|{xno}");
        // tbname0 同步（表单层 DM）：select name → EOF insert（code/code1=DM）
        var exists = conn.ExecuteScalar<int>("SELECT CASE WHEN EXISTS (SELECT 1 FROM tbname0 WHERE rtrim(name)=N'回放配件NEW') THEN 1 ELSE 0 END");
        if (exists == 0)
        {
            // DM(1)/DM(2) 逐字（旧码客户端逻辑，独立实现：LEFT(qp,1)/LEFT(wbh,1)）
            var code = DmLegacy(conn, "回放配件NEW", 1);
            var code1 = DmLegacy(conn, "回放配件NEW", 2);
            conn.Execute(
                "INSERT INTO tbname0 (name, name1, code, code1) VALUES (N'回放配件NEW', N'回放配件NEW', @c, @c1)",
                new { c = code, c1 = code1 });
            msgs.Add($"NAME0-ADD|{code}|{code1}");
        }
        // —— edit 改码 RN27A → RN27A2：UPDATE + exec updatenno + EDITREC（WHERE=新码）——
        var editId = conn.ExecuteScalar<long>("SELECT id FROM tbprnoty WHERE rtrim(nno)='RN27A' AND rtrim(cp)=@cp", new { cp = Ansi(cp, 10) });
        conn.Execute(
            """
            UPDATE tbprnoty SET cp=@cp, nno='RN27A2', na1=N'回放配件A改', ty=N'回放车型改', fa=N'回放产改', dc=N'A4', unit=N'箱',
                   iprj=66, code_ts=N'RN27TS01', eng=N'ENGA2', nnp=N'', nnq=N'', uqty=1, xno=xno, tno=tno, amjj=0, bmjj=0,
                   nvol=4, suttle=4.4, note=N'改备注', note1=N'改备注1', oprc_cp=96, oprc_min=90
            WHERE id=@id
            """,
            new { cp = Ansi(cp, 10), id = editId });
        conn.Execute(
            "EXEC dbo.updatenno @p_cp, @old, @new, @fa, @xzfa",
            new { p_cp = Ansi(cp, 10), old = Ansi("RN27A", 18), @new = Ansi("RN27A2", 18), fa = Ansi("回放产改", 8), xzfa = false });
        // EDITREC（读回属性行 → update tbisto/tborder WHERE nno=行值；tborder 加 type<>'历史'）
        var cur = conn.QuerySingle<(string Nno, string Na1, string Ty, string Note, string Unit, string Dc, string Fa, int Xno, int Tno, string Cxnno)>(
            "SELECT rtrim(nno), rtrim(na1), rtrim(ty), rtrim(note), rtrim(unit), rtrim(dc), rtrim(fa), ISNULL(xno,0), ISNULL(tno,0), rtrim(cxnno) FROM tbprnoty WHERE id=@id", new { id = editId });
        conn.Execute(
            "UPDATE tbisto SET nno=@nno, na1=@na1, ty=@ty, note=@note, unit=@unit, dc=@dc, xno=@xno, tno=@tno, cxnno=@cxnno, fa=@fa WHERE nno=@nno",
            new { nno = cur.Nno, na1 = cur.Na1, ty = cur.Ty, note = cur.Note, unit = cur.Unit, dc = cur.Dc, fa = cur.Fa, xno = cur.Xno, tno = cur.Tno, cxnno = cur.Cxnno });
        conn.Execute(
            "UPDATE tborder SET nno=@nno, na1=@na1, ty=@ty, note=@note, unit=@unit, dc=@dc, cxnno=@cxnno, fa=@fa WHERE nno=@nno AND type<>'历史'",
            new { nno = cur.Nno, na1 = cur.Na1, ty = cur.Ty, note = cur.Note, unit = cur.Unit, dc = cur.Dc, fa = cur.Fa, cxnno = cur.Cxnno });
        var isto = conn.QuerySingle<(string Na1, string Nno)>(
            "SELECT rtrim(na1), rtrim(nno) FROM tbisto WHERE rtrim(nno)='RN27A2' AND rtrim(cp)=@cp AND dpm=@dpm", new { cp = Ansi(cp, 10), dpm = Ansi(dept, 10) });
        msgs.Add($"PRNOTY-EDIT|{isto.Nno}|{isto.Na1}");
        // tbname0 同步（row85 公共路径：add/edit 都走；DM 源=na1）
        var nmExists = conn.ExecuteScalar<int>("SELECT CASE WHEN EXISTS (SELECT 1 FROM tbname0 WHERE rtrim(name)=N'回放配件A改') THEN 1 ELSE 0 END");
        if (nmExists == 0)
        {
            var code = DmLegacy(conn, "回放配件A改", 1);
            var code1 = DmLegacy(conn, "回放配件A改", 2);
            conn.Execute("INSERT INTO tbname0 (name, name1, code, code1) VALUES (N'回放配件A改', N'回放配件A改', @c, @c1)", new { c = code, c1 = code1 });
        }
        // —— 通用码：max(xno)（PASSTH 自动加 cp）→ update xno WHERE id + EDITREC ——
        var maxXno = conn.ExecuteScalar<int?>("SELECT MAX(xno) FROM tbprnoty WHERE rtrim(cp)=@cp", new { cp = Ansi(cp, 10) }) ?? 0;
        var nextXno = maxXno + 1;
        conn.Execute("UPDATE tbprnoty SET xno=@v WHERE id=@id", new { v = nextXno, id = editId });
        var cur2 = conn.QuerySingle<(string Nno, string Na1, string Ty, string Note, string Unit, string Dc, string Fa, int Xno, int Tno, string Cxnno)>(
            "SELECT rtrim(nno), rtrim(na1), rtrim(ty), rtrim(note), rtrim(unit), rtrim(dc), rtrim(fa), ISNULL(xno,0), ISNULL(tno,0), rtrim(cxnno) FROM tbprnoty WHERE id=@id", new { id = editId });
        conn.Execute(
            "UPDATE tbisto SET nno=@nno, na1=@na1, ty=@ty, note=@note, unit=@unit, dc=@dc, xno=@xno, tno=@tno, cxnno=@cxnno, fa=@fa WHERE nno=@nno",
            new { nno = cur2.Nno, na1 = cur2.Na1, ty = cur2.Ty, note = cur2.Note, unit = cur2.Unit, dc = cur2.Dc, fa = cur2.Fa, xno = cur2.Xno, tno = cur2.Tno, cxnno = cur2.Cxnno });
        msgs.Add($"PRNOTY-XNO|{nextXno}");
        return msgs;
    }

    /// <summary>旧 DM 复刻：逐汉字查 tbcode（way=1 全拼首字母 / 2 五笔首字母）。</summary>
    public static string DmLegacy(SqlConnection conn, string text, int way)
    {
        var chars = text.Where(c => c > 127).Select(c => c.ToString()).ToList();
        if (chars.Count == 0) return "";
        var map = conn.Query<(string Chn, string Qp, string Wbh)>(
            "SELECT rtrim(chn), rtrim(qp), rtrim(wbh) FROM tbcode WHERE rtrim(chn) IN @chns",
            new { chns = chars }).AsList().ToDictionary(r => r.Chn, r => (way == 1 ? r.Qp : r.Wbh) is { Length: > 0 } s ? s[..1] : "");
        return string.Concat(chars.Select(c => map.GetValueOrDefault(c, "")));
    }

    /// <summary>场景 27 旧侧：A2 tbname0 CRUD（新增防重/修改/删除）+ AA tbdccoef + A7 low_rate + A8 字典/参数 findrecord。</summary>
    public static List<string> AttrBasicLegacy(SqlConnection conn, string dept, DateTime date)
    {
        var msgs = new List<string>();
        // A2 新增（防重：name=?）
        var dup = conn.ExecuteScalar<int>("SELECT CASE WHEN EXISTS (SELECT 1 FROM tbname0 WHERE rtrim(name)=N'回放名称R') THEN 1 ELSE 0 END");
        if (dup == 0)
        {
            var code = DmLegacy(conn, "回放名称R", 1);
            var code1 = DmLegacy(conn, "回放名称R", 2);
            conn.Execute("INSERT INTO tbname0 (name, name1, code, code1) VALUES (N'回放名称R', N'回放名称R', @c, @c1)", new { c = code, c1 = code1 });
            msgs.Add($"NAME0|{code}|{code1}");
        }
        // A2 修改（row24 M_FLDS=name,name1,code,code1 四列全更；code/code1=表单 TXTNAME1 联动 DM(name1)）
        conn.Execute("UPDATE tbname0 SET name1=N'回放别名R', code=@c, code1=@c1 WHERE rtrim(name)=N'回放名称R'",
            new { c = DmLegacy(conn, "回放别名R", 1), c1 = DmLegacy(conn, "回放别名R", 2) });
        msgs.Add("NAME0-EDIT");
        // AA 新增/修改/删除
        conn.Execute("IF NOT EXISTS (SELECT 1 FROM tbdccoef WHERE UPPER(rtrim(dc))='RN27') INSERT INTO tbdccoef (dc, coeffict) VALUES ('RN27', 1.5)");
        conn.Execute("UPDATE tbdccoef SET coeffict=2.5 WHERE rtrim(dc)='RN27'");
        msgs.Add("DCCOEF|2.5");
        // A7 low_rate
        conn.Execute("UPDATE tbcomplex SET low_rate=0.15 WHERE rtrim(code_ts)='RN27TS01'");
        msgs.Add("COMPLEX|0.15");
        // A8 字典（type='unit' 新增+修改+删除：硬删按 id）
        var dictId = conn.ExecuteScalar<int>("INSERT INTO tbsysset (name, type, valuen, valuec, dpm) VALUES (N'回放单位R', 'unit', 0, N'', @dpm); SELECT CAST(SCOPE_IDENTITY() AS int);", new { dpm = Ansi(dept, 10) });
        conn.Execute("UPDATE tbsysset SET name=N'回放单位R2', valuen=1, valuec=N'回放' WHERE id=@id", new { id = dictId });
        conn.Execute("DELETE FROM tbsysset WHERE id=@id", new { id = dictId });
        msgs.Add("DICT-OK");
        // A8 参数 findrecord（先补 xtype 无行→整行 INSERT）
        conn.Execute("INSERT INTO tbsysset (name, type, dpm) VALUES (N'回放参数R', 'system', @dpm)", new { dpm = Ansi(dept, 10) });
        var pr = conn.QuerySingleOrDefault<int?>("SELECT TOP 1 id FROM tbsysset WHERE name=N'回放参数R' AND type='system' AND dpm=@dpm", new { dpm = Ansi(dept, 10) });
        if (pr is null)
            conn.Execute("INSERT INTO tbsysset (name, type, dpm, xtype) VALUES (N'回放参数R', 'system', @dpm, '汽配')", new { dpm = Ansi(dept, 10) });
        else
            conn.Execute("UPDATE tbsysset SET xtype='汽配' WHERE id=@id", new { id = pr });
        conn.Execute("UPDATE tbsysset SET valuec=N'回放值' WHERE name=N'回放参数R' AND type='system' AND dpm=@dpm", new { dpm = Ansi(dept, 10) });
        msgs.Add("PARA-OK");
        return msgs;
    }

    /// <summary>场景 28 旧侧：A3 属性传递（row14 契约 SQL：勾选字段 na1/ty/dc/iprj，目标 isto/istosm/tborder/sada；价格 CASE；P_XZFA=.F. 无 fa JOIN）。</summary>
    public static List<string> AttrTransferLegacy(SqlConnection conn, string dept, string cp)
    {
        var msgs = new List<string>();
        foreach (var t in new[] { "tbisto", "tbistosm", "tborder", "tbsada" })
        {
            // 普通字段（na1/ty/dc 存在于全部目标表）+ 价格字段 iprj（CASE 保留现价）
            var sql = $"""
                UPDATE {t} SET na1=p.na1, ty=p.ty, dc=p.dc,
                       iprj=(CASE WHEN ISNULL(p.iprj,0)=0 THEN t.iprj ELSE p.iprj END)
                FROM {t} t LEFT JOIN tbprnoty p ON t.cp=p.cp AND t.nno=p.nno
                WHERE t.cp=p.cp AND t.nno=p.nno AND (NOT p.na1=t.na1 OR NOT p.ty=t.ty OR NOT p.dc=t.dc OR p.iprj<>t.iprj)
                """;
            var n = conn.Execute(sql);
            msgs.Add($"TRANSFER|{t}|{n}");
        }
        return msgs;
    }

    /// <summary>场景 28 旧侧：A4 组套/拆套（row79/91：FIFO 扣减+加权价入套件；拆套回库不带成本字段）。</summary>
    public static List<string> AttrKitLegacy(SqlConnection conn, string dept, string cp, DateTime date)
    {
        var msgs = new List<string>();
        // 套件头+组件定义（tbprtj）
        conn.Execute(
            "IF NOT EXISTS (SELECT 1 FROM tbprtj WHERE rtrim(tj)='T-RN27' AND (nno='' OR nno IS NULL)) INSERT INTO tbprtj (cp, tj, na1, ty, fa, dc, unit, note, iprj) VALUES (@cp, 'T-RN27', N'回放套件', N'回放车型', N'回放产地', N'A5', N'套', N'回放套件备注', 0)",
            new { cp = Ansi(cp, 10) });
        conn.Execute(
            "IF NOT EXISTS (SELECT 1 FROM tbprtj WHERE rtrim(tj)='T-RN27' AND rtrim(nno)='RN27B') INSERT INTO tbprtj (cp, tj, nno, na1, ty, fa, dc, unit, num, iprj) VALUES (@cp, 'T-RN27', 'RN27B', N'回放配件B', N'回放车型', N'回放产地', N'A2', N'个', 2, 40)",
            new { cp = Ansi(cp, 10) });
        // 套件价=Σ num×iprj → 头行回写
        var sum = conn.ExecuteScalar<decimal?>("SELECT SUM(num*iprj) FROM tbprtj WHERE rtrim(tj)='T-RN27' AND NOT (nno='' OR nno IS NULL) AND rtrim(cp)=@cp", new { cp = Ansi(cp, 10) }) ?? 0;
        conn.Execute("UPDATE tbprtj SET iprj=@s WHERE (nno='' OR nno IS NULL) AND rtrim(tj)='T-RN27' AND rtrim(cp)=@cp", new { s = sum, cp = Ansi(cp, 10) });
        msgs.Add($"KIT-DEF|{sum:0.##}");
        // —— 组套 3 套（row79 FIFO：RN27B 库存 10 → 扣 6，加权 40×6/6）——
        var rows = conn.Query<(long Id, decimal Kc, decimal Oprc, decimal Iprc, decimal Iprj)>(
            "SELECT id, kcamount, oprc, iprc, iprj FROM tbisto WHERE rtrim(nno)='RN27B' AND rtrim(cp)=@cp ORDER BY id", new { cp = Ansi(cp, 10) }).AsList();
        var need = 6m; // 3 套 × num 2
        decimal wO = 0, wI = 0, wJ = 0, taken = 0;
        foreach (var r in rows)
        {
            if (need <= 0) break;
            var take = Math.Min(need, r.Kc);
            if (take <= 0) continue;
            conn.Execute("UPDATE tbisto SET kcamount=kcamount-@t WHERE id=@id", new { t = take, r.Id });
            wO += take * r.Oprc; wI += take * r.Iprc; wJ += take * r.Iprj; taken += take;
            need -= take;
        }
        var head = conn.QuerySingleOrDefault<(long Id, decimal Kc)>(
            "SELECT id, kcamount FROM tbisto WHERE rtrim(nno)='T-RN27' AND rtrim(cp)=@cp", new { cp = Ansi(cp, 10) });
        if (head.Id == 0)
        {
            var meta = conn.QuerySingle<(string Na1, string Ty, string Fa, string Dc, string Note)>(
                "SELECT rtrim(na1), rtrim(ty), rtrim(fa), rtrim(dc), rtrim(note) FROM tbprtj WHERE rtrim(tj)='T-RN27' AND (nno='' OR nno IS NULL) AND rtrim(cp)=@cp", new { cp = Ansi(cp, 10) });
            conn.Execute(
                """
                INSERT INTO tbisto (dpm, cp, nno, na1, ty, fa, dc, note, iprj, oprc, iprc, kcamount, depotno)
                VALUES (@dpm, @cp, 'T-RN27', @na1, @ty, @fa, @dc, @note, @iprj, @oprc, @iprc, @kc, '01')
                """,
                new { dpm = Ansi(dept, 10), cp = Ansi(cp, 10), na1 = meta.Na1, ty = meta.Ty, fa = meta.Fa, dc = meta.Dc, note = meta.Note, iprj = taken > 0 ? wJ / taken : 0, oprc = taken > 0 ? wO / taken : 0, iprc = taken > 0 ? wI / taken : 0, kc = 3m });
            msgs.Add("KIT-ASM-INSERT");
        }
        else
        {
            conn.Execute("UPDATE tbisto SET kcamount=@k WHERE id=@id", new { k = head.Kc + 3m, head.Id });
            msgs.Add("KIT-ASM-UPDATE");
        }
        // —— 拆套 1 套（row91：扣套件 1×套数；组件回库 1×num 不带成本字段）——
        var hd = conn.QuerySingle<(long Id, decimal Kc, string Dep)>("SELECT id, kcamount, rtrim(depotno) FROM tbisto WHERE rtrim(nno)='T-RN27' AND rtrim(cp)=@cp", new { cp = Ansi(cp, 10) });
        conn.Execute("UPDATE tbisto SET kcamount=kcamount-@q WHERE id=@id", new { q = 1m, hd.Id });
        var lines = conn.Query<(string Nno, decimal Num)>("SELECT rtrim(nno), num FROM tbprtj WHERE rtrim(tj)='T-RN27' AND NOT (nno='' OR nno IS NULL) AND rtrim(cp)=@cp", new { cp = Ansi(cp, 10) }).AsList();
        foreach (var l in lines)
        {
            var add = 1m * l.Num;
            var stk = conn.QuerySingleOrDefault<(long Id, decimal Kc)>(
                "SELECT id, kcamount FROM tbisto WHERE rtrim(nno)=@nno AND rtrim(cp)=@cp AND rtrim(depotno)=@dep AND dpm=@dpm",
                new { nno = Ansi(l.Nno, 18), cp = Ansi(cp, 10), dep = Ansi(hd.Dep, 10), dpm = Ansi(dept, 10) });
            if (stk.Id == 0)
                conn.Execute(
                    "INSERT INTO tbisto (dpm, cp, nno, na1, ty, fa, dc, note, iprj, kcamount, depotno) SELECT dpm, cp, na1, ty, fa, dc, note, note, iprj, @add, depotno FROM tbprnoty WHERE rtrim(nno)=@nno AND rtrim(cp)=@cp",
                    new { add, nno = Ansi(l.Nno, 18), cp = Ansi(cp, 10) });
            else
                conn.Execute("UPDATE tbisto SET kcamount=kcamount+@a WHERE id=@id", new { a = add, stk.Id });
        }
        msgs.Add("KIT-DIS");
        // —— A1 row127 属性传递到库存（RN27B 行已存在 → UPDATE na1,ty,fa,unit,dc,xno,tno,cxnno,note）——
        conn.Execute(
            """
            UPDATE tbisto SET na1=p.na1, ty=p.ty, fa=p.fa, unit=p.unit, dc=p.dc, xno=p.xno, tno=p.tno, cxnno=p.cxnno, note=p.note
            FROM tbisto t JOIN tbprnoty p ON t.cp=p.cp AND t.nno=p.nno
            WHERE rtrim(t.nno)='RN27B' AND rtrim(t.cp)=@cp
            """,
            new { cp = Ansi(cp, 10) });
        return msgs;
    }

    /// <summary>场景 29 种子：tbistosm 回放行（fr_mdefi 模式②聚合源；基线 73 行保留——旧 PASSTH 无 WHERE 全表参与）。</summary>
    public static void SeedCustomFieldAggregate(SqlConnection conn, string dept, string cp)
    {
        var rows = new[]
        {
            new { Nno = "RN29A", Na1 = "回放聚合甲", Iprj = 10m, Iprc = 8m },
            new { Nno = "RN29A", Na1 = "回放聚合甲2", Iprj = 20m, Iprc = 15m },
            new { Nno = "RN29B", Na1 = "回放聚合乙", Iprj = 30m, Iprc = 24m },
        };
        foreach (var r in rows)
            conn.Execute(
                """
                INSERT INTO tbistosm (dpm, cp, nno, na1, ty, fa, dc, unit, jkamount, iprj, iprc, oprc, oprb, posi, indate, per, cno, xno,
                    gyno, gyname, pno, note, cxnno, fkfs, depotno, ro, mark, ywr, Freight)
                VALUES (@dpm, @cp, @nno, @na1, '回放车型', '回放产地', 'A1', '个', 1, @iprj, @iprc, 0, 0, '01', '2026-02-01', 'REPLAY', 'RN29CNO1', 0,
                    '', '', '', '', '', '', '01', 0, '', '', 0)
                """,
                new { dpm = Ansi(dept, 10), cp = Ansi(cp, 10), nno = Ansi(r.Nno, 18), na1 = Ansi(r.Na1, 60), iprj = r.Iprj, iprc = r.Iprc });
    }

    /// <summary>
    /// 场景 29 旧侧：fr_mdefi 模式②系统表聚合（FrmAccount.CmdFinish PASSTH 拼串，M_CPNNO=2）。
    /// 变体1 M_CG=.T.（全聚合）→ GROUP BY cp,nno；变体2 含原值（M_CG=.F.）→ 无 GROUP BY 全表行。
    /// 首条消息回显 SQL 串（两侧拼串契约 diff）。
    /// </summary>
    public static List<string> CustomFieldAggregateLegacy(SqlConnection conn)
    {
        var msgs = new List<string>();
        // 变体1：max + sum（数值种子取整数——SUM(float) 教训）
        var sql1 = "SELECT cp, nno, max(na1) as 回放聚合1, sum(iprj) as 回放聚合2 FROM tbistosm group by cp, nno";
        msgs.Add("CFSQL|" + sql1);
        var rows1 = conn.Query<(string Cp, string Nno, string A1, decimal A2)>(sql1).AsList();
        foreach (var r in rows1)
            msgs.Add($"CFAGG|{r.Cp.TrimEnd()}|{r.Nno.TrimEnd()}|{r.A1.TrimEnd()}|{r.A2:0.##}");
        // 变体2：原值（任一 Method=1 → M_CG=.F. 无 GROUP BY）
        var sql2 = "SELECT cp, nno, na1 as 回放原值1 FROM tbistosm";
        msgs.Add("CFSQL|" + sql2);
        var rows2 = conn.Query<(string Cp, string Nno, string V)>(sql2).AsList();
        foreach (var r in rows2)
            msgs.Add($"CFRAW|{r.Cp.TrimEnd()}|{r.Nno.TrimEnd()}|{r.V.TrimEnd()}");
        return msgs;
    }

    // ───────────────────────── C 组收尾（第十四批）─────────────────────────

    /// <summary>
    /// 场景 31 旧侧：销售转预售（fr_sale row53 M_PD=6 L100-103 UPDATE 路径原样重放）。
    /// 读原单（mark 非空 → 该单已确认）→ GETNEWDH('预售单',1) → UPDATE tbsada
    /// SET type/paper/ypaper/note/outdate WHERE type=旧type AND paper=旧号 → header 换号。UPDATE 不触发
    /// sale_in/sale_del（库存零变动）。note='销售转预售'（L100），outdate=P_RQ=当天。
    /// </summary>
    public static string SaleToPresale(SqlConnection conn, string paper, string dept) =>
        ConvertPresaleLegacy(conn, paper, dept, fromType: "销售", toType: "预售", note: "销售转预售");

    /// <summary>
    /// 场景 31 旧侧：预售转销售（row53 FLAG='预售' L130/145/147/148）：note='预售转销售'+DTOC(TTOD(原单outdate))
    /// （与新系统 yyyy-MM-dd 格式对齐），其余同 UPDATE 路径。
    /// </summary>
    public static string PresaleToSale(SqlConnection conn, string paper, string dept)
    {
        var oldOutdate = conn.ExecuteScalar<DateTime?>(
            "SELECT TOP 1 outdate FROM tbsada WITH (NOLOCK) WHERE paper = @paper AND type = N'预售'",
            new { paper = Ansi(paper.Trim(), 10) });
        return ConvertPresaleLegacy(conn, paper, dept, fromType: "预售", toType: "销售",
            note: $"预售转销售{oldOutdate?.ToString("yyyy-MM-dd") ?? ""}");
    }

    private static string ConvertPresaleLegacy(SqlConnection conn, string paper, string dept,
        string fromType, string toType, string note)
    {
        var oldPaper = paper.Trim();
        var row = conn.QuerySingleOrDefault<(string type, string mark, DateTime outdate)>(
            """
            SELECT TOP 1 RTRIM(type) AS type, RTRIM(ISNULL(mark, '')) AS mark, outdate
            FROM tbsada WITH (UPDLOCK) WHERE paper = @paper AND type = @type
            """,
            new { paper = Ansi(oldPaper, 10), type = Ansi(fromType) });
        if (row.type.Length == 0)
            throw new InvalidOperationException($"{fromType}单 {oldPaper} 不存在");
        if (!string.IsNullOrWhiteSpace(row.mark))
            throw new InvalidOperationException("该单已确认！");

        var newPaper = GetNewDh(conn, toType + "单", dept, 1);
        var updated = conn.Execute(
            """
            UPDATE tbsada
            SET type = @totype, paper = @newpaper, ypaper = @oldpaper, note = @note, outdate = @outdate
            WHERE type = @fromtype AND paper = @oldpaper
            """,
            new
            {
                totype = Ansi(toType), newpaper = Ansi(newPaper, 10), oldpaper = Ansi(oldPaper, 10),
                note = Ansi(note, 250), outdate = DateTime.Today, fromtype = Ansi(fromType),
            });
        if (updated == 0)
            throw new InvalidOperationException($"{fromType}单 {oldPaper} 转换失败（UPDATE 0 行）");

        // header 换号（row53 L103/L148）
        conn.Execute("UPDATE tbsada_header SET paper = @newpaper WHERE paper = @oldpaper",
            new { newpaper = Ansi(newPaper, 10), oldpaper = Ansi(oldPaper, 10) });
        return newPaper;
    }

    /// <summary>
    /// 场景 32 种子：库存分货订单（tborder type='历史'，gyname=部门名使 fr_kcfh 数据源命中；
    /// SeedOrderIn 的 gyname 硬编码空串不适用，故单列变体）。
    /// </summary>
    public static void SeedStockDistributeOrders(SqlConnection conn, string dept, string gyname,
        IReadOnlyList<(Batch part, decimal oqty, string gname, int item)> lines, string cno)
    {
        foreach (var (part, oqty, gname, item) in lines)
        {
            conn.Execute(
                """
                INSERT INTO tborder (cp, dpm, orno, cno, item, nno, na1, ty, fa, dc, unit, oqty, qty, iprj,
                                     getdate, itemc, note, orderdate, type, gyname, gname, fkfs, per, oprc)
                VALUES (@cp, @dpm, @orno, @cno, @item, @nno, @na1, @ty, @fa, @dc, @unit, @oqty, 0, @iprj,
                        @getdate, 0, '', @orderdate, N'历史', @gyname, @gname, N'现金', @per, @oprc)
                """,
                new
                {
                    cp = Ansi(part.Cp, 10), dpm = Ansi(dept, 10), orno = Ansi($"RO{item:D3}", 14),
                    cno = Ansi(cno, 15), item, nno = Ansi(part.Nno, 18), na1 = Ansi(part.Na1, 40),
                    ty = Ansi(part.Ty, 14), fa = Ansi(part.Fa, 8), dc = Ansi(part.Dc, 8),
                    unit = Ansi(part.Unit, 4), oqty, iprj = part.Iprj, getdate = DateTime.Today,
                    orderdate = new DateTime(2026, 1, 1), gyname = Ansi(gyname, 60), gname = Ansi(gname, 40),
                    per = Ansi(ReplayConfig.Operator, 10), oprc = part.Oprc,
                });
        }
    }

    /// <summary>
    /// 场景 32 旧侧：库存分货（fr_kcfh row23 游标循环复刻，与 StockDistributeRepository.DistributeAsync 逐段对照）：
    /// 订单聚合（fqty=Σtbsada(gname+ypaper+cno+nno)、kcamount=Σtbisto(dpm+cp+nno,posi≠废)）→ 已满足剔除 →
    /// 组内 fhamount=MIN(INT(kc/M_FHAMOUNT*(oqty-fqty)), oqty-fqty) → 模式2 剔 0 → 排序 CP+NNO+ORDERDATE+CNO →
    /// 逐订单批次（depotno_or 排序 + lastiprc + M_OPRCSTR 表达式）→ CKAMOUNT=MIN(库存, 模式1?缺口:FHAMOUNT) →
    /// INSERT tbsada 31 列 type='调拨' guno='其它' ypaper=cno（sale_in 触发器即时扣 tbisto）→ 游标扣减。
    /// 取号 DJLSH('调拨单',1) 应用层拼号，每部门一张单复用。
    /// </summary>
    public static List<string> LegacyStockDistribute(SqlConnection conn, string dept, string dpmname, int mode,
        string oprcField, decimal oprcFactor, int roundMode, IReadOnlyList<(string gname, string cno)> picked)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        var per = ReplayConfig.Operator;
        var pDpm = Ansi(dept, 10);
        var today = DateTime.Today;
        var fpcw = conn.ExecuteScalar<string?>(
            "SELECT valuec FROM tbsysset WITH (NOLOCK) WHERE type = N'system' AND name = N'废品仓位标识'") is string v
            && !string.IsNullOrWhiteSpace(v) ? v.Trim() : "废";
        // M_OPRCSTR = 基准列×系数（白名单收敛备案；取整方式同新系统三种）
        var baseExpr = $"i.{oprcField} * @oprcFactor";
        var oprcExpr = roundMode switch
        {
            2 => $"CAST(ROUND({baseExpr}, 0, 1) AS numeric(18,4))",
            3 => $"CAST(CEILING({baseExpr}) AS numeric(18,4))",
            _ => $"CAST({baseExpr} AS numeric(18,4))",
        };
        // tbsysset type='库号' 挑选条件（row69-74，未配置则空）
        // 契约：tbxzb.variable 为 VFP 表达式（如 dpm=?p_dpm），?p_xxx 参数化（语义同 UniversalQueryRepository.AppendXzb）
        var xzbRaw = conn.ExecuteScalar<string?>(
            "SELECT RTRIM(variable) FROM tbxzb WITH (NOLOCK) WHERE tablename = N'tbsysset'") ?? "";
        var xzb = System.Text.RegularExpressions.Regex.Replace(xzbRaw, @"\?p_(\w+)", "@xzb_$1");
        var xzbParams = new Dictionary<string, object?>();
        if (xzb.Contains("@xzb_dpm")) xzbParams["xzb_dpm"] = Ansi(dept.Trim(), 10);

        // L35-47 订单聚合
        var orders = new List<(decimal oqty, decimal qty, string gname, string cno, string cp, string nno,
            string na1, string ty, string fa, string unit, DateTime? orderdate, decimal fqty, decimal kcamount,
            decimal fhamount)>();
        foreach (var (gname, cno) in picked)
        {
            var rows = conn.Query<(decimal oqty, decimal qty, string gname, string cno, string cp, string nno,
                string na1, string ty, string fa, string unit, DateTime? orderdate, decimal fqty, decimal kcamount)>(
                """
                SELECT o.oqty, o.qty, ISNULL(RTRIM(o.gname), '') gname, RTRIM(o.cno) cno,
                       RTRIM(o.cp) cp, RTRIM(o.nno) nno, RTRIM(o.na1) na1, RTRIM(o.ty) ty, RTRIM(o.fa) fa,
                       RTRIM(o.unit) unit, o.orderdate,
                       fqty = ISNULL((SELECT SUM(s.ckamount) FROM tbsada s WITH (NOLOCK)
                                      WHERE s.gname = o.gname AND s.ypaper = o.cno AND s.nno = o.nno), 0),
                       kcamount = ISNULL((SELECT SUM(t.kcamount) FROM tbisto t WITH (NOLOCK)
                                          WHERE t.dpm = @pDpm AND t.cp = o.cp AND t.nno = o.nno
                                            AND t.posi <> @fpcw), 0)
                FROM tborder o WITH (UPDLOCK)
                WHERE o.type = N'历史' AND o.gyname = @dpmname
                  AND o.gname = @gname AND o.cno = @cno AND o.oqty - o.qty > 0
                """,
                new { pDpm, fpcw = Ansi(fpcw, 10), dpmname = Ansi(dpmname, 60),
                      gname = Ansi(gname, 40), cno = Ansi(cno, 15) }).AsList();
            foreach (var r in rows)
                orders.Add((r.oqty, r.qty, r.gname, r.cno, r.cp, r.nno, r.na1, r.ty, r.fa, r.unit,
                    r.orderdate, r.fqty, r.kcamount, 0));
        }
        if (orders.Count == 0)
            throw new InvalidOperationException("没有可分货的订单数据！");

        // L49 已满足剔除；L51-58 组内预计算；L59-61 模式2 剔 0；L62 排序
        orders = orders.Where(o => o.oqty > o.fqty).ToList();
        var groups = orders.GroupBy(o => (o.cp, o.nno)).ToDictionary(g => g.Key, g => g.Sum(x => x.oqty - x.fqty));
        for (var i = 0; i < orders.Count; i++)
        {
            var o = orders[i];
            var total = groups[(o.cp, o.nno)];
            var fhamount = Math.Min(Math.Floor(o.kcamount / total * (o.oqty - o.fqty)), o.oqty - o.fqty);
            orders[i] = (o.oqty, o.qty, o.gname, o.cno, o.cp, o.nno, o.na1, o.ty, o.fa, o.unit,
                o.orderdate, o.fqty, o.kcamount, fhamount);
        }
        if (mode == 2)
            orders = orders.Where(o => o.fhamount > 0).ToList();
        orders = orders.OrderBy(o => o.cp).ThenBy(o => o.nno)
            .ThenBy(o => o.orderdate ?? DateTime.MaxValue).ThenBy(o => o.cno).ToList();

        var papers = new List<string>();
        string? paper = null;   // T_PAPER 游标：单部门一张单复用
        for (var oi = 0; oi < orders.Count; oi++)
        {
            var o = orders[oi];
            // L83-89 批次（depotno_or + lastiprc + M_OPRCSTR）
            var batches = conn.Query<(int id, string depotno, string posi, decimal kcamount, decimal iprj,
                decimal iprc, decimal oprc, string dc, int tno, int xno, string cxnno, string note, string unit)>(
                $"""
                SELECT i.id, RTRIM(i.depotno) depotno, RTRIM(i.posi) posi, i.kcamount, i.iprj, i.iprc,
                       oprc = {oprcExpr}, RTRIM(i.dc) dc, i.tno, i.xno, RTRIM(i.cxnno) cxnno,
                       RTRIM(i.note) note, RTRIM(i.unit) unit,
                       lastiprc = ISNULL((SELECT TOP (1) e.iprc FROM tbistoed e WITH (NOLOCK)
                                          WHERE e.dpm = i.dpm AND e.cp = i.cp AND e.nno = i.nno
                                            AND e.indate = ISNULL((SELECT MAX(e2.indate) FROM tbistoed e2 WITH (NOLOCK)
                                                                   WHERE e2.dpm = i.dpm AND e2.cp = i.cp AND e2.nno = i.nno), '')), 0),
                       depotno_or = ISNULL((SELECT TOP (1) s.id FROM tbsysset s WITH (NOLOCK)
                                            WHERE s.type = N'库号' AND s.name = i.depotno AND ({xzb})),
                                           (SELECT MAX(s2.id) FROM tbsysset s2 WITH (NOLOCK)))
                FROM tbisto i WITH (UPDLOCK)
                WHERE i.dpm = @pDpm AND i.cp = @cp AND i.nno = @nno AND i.kcamount > 0 AND i.posi <> @fpcw
                ORDER BY depotno_or
                """,
                MergeWith(new { pDpm, cp = Ansi(o.cp, 10), nno = Ansi(o.nno, 18), fpcw = Ansi(fpcw, 10), oprcFactor }, xzbParams)).AsList();

            var bi = 0;
            while (bi < batches.Count && o.oqty > o.fqty)
            {
                var b = batches[bi];
                if (b.kcamount <= 0) { bi++; continue; }
                if (paper is null)
                {
                    // DJLSH('调拨单',1)：tbdjgs 应用层拼号（djjx+年2位+D6流水）
                    var dj = conn.QueryFirstOrDefault<(int id, string djjx, int lsh, string dyear)>(
                        "SELECT id, RTRIM(djjx), lsh, RTRIM(dyear) FROM tbdjgs WITH (UPDLOCK, ROWLOCK) WHERE djname = N'调拨单' AND dpm = @dpm",
                        new { dpm = Ansi(dept.Trim(), 10) });
                    if (dj.id == 0)
                        throw new InvalidOperationException("取单号失败：tbdjgs 无「调拨单」前缀行");
                    var year = DateTime.Now.Year.ToString();
                    var lsh = dj.dyear == year ? dj.lsh + 1 : 1;
                    conn.Execute("UPDATE tbdjgs SET dyear = @year, lsh = @lsh WHERE id = @id",
                        new { year = Ansi(year, 4), lsh, id = dj.id });
                    paper = dj.djjx + year[^2..] + lsh.ToString("D6");
                    papers.Add(paper);
                }

                var want = mode == 1 ? o.oqty - o.fqty : o.fhamount;
                var ckamount = Math.Min(b.kcamount, want);
                if (ckamount <= 0) break;

                // L127 INSERT tbsada 31 列（sale_in 触发器即时扣 tbisto）
                conn.Execute(
                    """
                    INSERT INTO tbsada
                        (dpm, gname, type, cp, nno, na1, ty, fa, dc, unit, xno, tno, cxnno, note,
                         pnno, pna1, pty, pfa, depotno, posi, iprj, iprc, oprc, oprct, ckamount,
                         paper, ypaper, outdate, guno, per, kcid)
                    VALUES
                        (@dpm, @gname, N'调拨', @cp, @nno, @na1, @ty, @fa, @dc, @unit, @xno, @tno, @cxnno, @note,
                         @pnno, @pna1, @pty, @pfa, @depotno, @posi, @iprj, @iprc, @oprc, @oprc, @ckamount,
                         @paper, @ypaper, @outdate, N'其它', @per, @kcid)
                    """,
                    new
                    {
                        dpm = pDpm, gname = Ansi(o.gname, 40), cp = Ansi(o.cp, 10), nno = Ansi(o.nno, 18),
                        na1 = Ansi(o.na1, 40), ty = Ansi(o.ty, 14), fa = Ansi(o.fa, 10), dc = Ansi(b.dc, 8),
                        unit = Ansi(b.unit, 4), b.xno, b.tno, cxnno = Ansi(b.cxnno, 18), note = Ansi(b.note, 100),
                        pnno = Ansi(o.nno, 18), pna1 = Ansi(o.na1, 40), pty = Ansi(o.ty, 14), pfa = Ansi(o.fa, 10),
                        depotno = Ansi(b.depotno, 10), posi = Ansi(b.posi, 10),
                        b.iprj, b.iprc, b.oprc, ckamount, paper = Ansi(paper, 15),
                        ypaper = Ansi(o.cno, 15), outdate = today, per = Ansi(per, 6), kcid = b.id,
                    });

                // L133-135 游标扣减
                o.fqty += ckamount;
                o.fhamount -= ckamount;
                orders[oi] = (o.oqty, o.qty, o.gname, o.cno, o.cp, o.nno, o.na1, o.ty, o.fa, o.unit,
                    o.orderdate, o.fqty, o.kcamount, o.fhamount);
                batches[bi] = (b.id, b.depotno, b.posi, b.kcamount - ckamount, b.iprj, b.iprc, b.oprc,
                    b.dc, b.tno, b.xno, b.cxnno, b.note, b.unit);
            }
        }
        return papers;
    }

    /// <summary>
    /// 场景 33 旧侧：船单分货（fr_isor row103 原样重放，与 OrderInRepository.DistributeAsync 逐段对照）：
    /// DELETE 既有 seano+'_1' → 逐行候选订单（gname 非空优先 UNION，ByOrderDate 按 orderdate ASC /
    /// ByDemand 按缺口 DESC）→ 全缺口+余量空单 / 顺序填 / 比例分配（末个有客户行吃余额）→
    /// INSERT tbisorsm 27 列 seano+'_1'（item 取订单行 item，空单行 0，note=''，indate=当天）。
    /// </summary>
    public static string OrderInDistribute(SqlConnection conn, string dept, string cp, string per,
        string seano, int mode)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        var src = conn.Query<(int id, string cp, string cno, int item, string nno, string na1, string ty,
            string fa, decimal jkamount, decimal iprj, decimal iprc, decimal oprc, decimal oprb, string posi,
            string depotno, string orno, string unit, string fkfs, string gyname, string dc, string box, string pno)>(
            "SELECT id, RTRIM(cp) cp, RTRIM(cno) cno, item, RTRIM(nno) nno, RTRIM(na1) na1, RTRIM(ty) ty, " +
            "RTRIM(fa) fa, jkamount, iprj, iprc, oprc, oprb, RTRIM(posi) posi, RTRIM(depotno) depotno, " +
            "RTRIM(orno) orno, RTRIM(unit) unit, RTRIM(fkfs) fkfs, RTRIM(gyname) gyname, RTRIM(dc) dc, " +
            "RTRIM(box) box, RTRIM(pno) pno FROM tbisorsm WITH (UPDLOCK) " +
            "WHERE seano = @seano AND cp = @cp AND dpm = @dpm ORDER BY id",
            new { seano = Ansi(seano, 15), cp = Ansi(cp, 10), dpm = Ansi(dept, 10) }).AsList();
        if (src.Count == 0)
            throw new InvalidOperationException("没有到货明细，不能分货！");

        var newSeano = seano.Trim() + "_1";
        // row103 L31：先清既有分货结果
        conn.Execute("DELETE FROM tbisorsm WHERE seano = @seano AND dpm = @dpm",
            new { seano = Ansi(newSeano, 15), dpm = Ansi(dept, 10) });

        foreach (var s in src)
        {
            // row103 L40-56 候选订单（gname 非空优先）
            var orderSql = mode == 1
                ? """
                  SELECT id, RTRIM(orno) orno, (oqty - qty) gap, ISNULL(gname, '') gname FROM (
                      SELECT TOP (100) PERCENT id, orno, (oqty - qty) gap, gname, oqty, qty, orderdate FROM tborder WITH (NOLOCK)
                      WHERE dpm = @dpm AND cp = @cp AND nno = @nno AND oqty <> 0 AND ISNULL(gname, '') <> '' AND oqty > qty
                        AND cno IN (SELECT cno FROM tbisorsm WHERE seano = @seano AND dpm = @dpm)
                      UNION ALL
                      SELECT TOP (100) PERCENT id, orno, (oqty - qty) gap, gname, oqty, qty, orderdate FROM tborder WITH (NOLOCK)
                      WHERE dpm = @dpm AND cp = @cp AND nno = @nno AND oqty <> 0 AND ISNULL(gname, '') = '' AND oqty > qty
                        AND cno IN (SELECT cno FROM tbisorsm WHERE seano = @seano AND dpm = @dpm)
                  ) a ORDER BY gname DESC, orderdate ASC
                  """
                : """
                  SELECT id, RTRIM(orno) orno, (oqty - qty) gap, ISNULL(gname, '') gname FROM (
                      SELECT TOP (100) PERCENT id, orno, (oqty - qty) gap, gname, oqty, qty FROM tborder WITH (NOLOCK)
                      WHERE dpm = @dpm AND cp = @cp AND nno = @nno AND oqty <> 0 AND ISNULL(gname, '') <> '' AND oqty > qty
                        AND cno IN (SELECT cno FROM tbisorsm WHERE seano = @seano AND dpm = @dpm)
                      UNION ALL
                      SELECT TOP (100) PERCENT id, orno, (oqty - qty) gap, gname, oqty, qty FROM tborder WITH (NOLOCK)
                      WHERE dpm = @dpm AND cp = @cp AND nno = @nno AND oqty <> 0 AND ISNULL(gname, '') = '' AND oqty > qty
                        AND cno IN (SELECT cno FROM tbisorsm WHERE seano = @seano AND dpm = @dpm)
                  ) a ORDER BY gname DESC, (oqty - qty) DESC
                  """;
            var candidates = conn.Query<(int id, string orno, decimal gap, string gname)>(
                orderSql,
                new { dpm = Ansi(dept, 10), cp = Ansi(s.cp, 10), nno = Ansi(s.nno, 18), seano = Ansi(seano.Trim(), 15) })
                .AsList();

            var remain = s.jkamount;
            if (candidates.Count == 0)
            {
                InsertDistributedLegacy(conn, s, newSeano, "", s.jkamount, 0, dept, per);
                continue;
            }
            var totalGap = candidates.Sum(c => c.gap);
            if (remain >= totalGap)
            {
                foreach (var c in candidates)
                {
                    InsertDistributedLegacy(conn, s, newSeano, c.orno, c.gap, c.id, dept, per);
                    remain -= c.gap;
                }
                if (remain > 0)
                    InsertDistributedLegacy(conn, s, newSeano, "", remain, 0, dept, per);
            }
            else if (mode == 1)
            {
                foreach (var c in candidates)
                {
                    if (remain <= 0) break;
                    var take = Math.Min(remain, c.gap);
                    InsertDistributedLegacy(conn, s, newSeano, c.orno, take, c.id, dept, per);
                    remain -= take;
                }
            }
            else
            {
                // 按需求比例：AA=全部缺口，AA1=有客户行缺口（L107-161）
                var namedGap = candidates.Where(c => c.gname != "").Sum(c => c.gap);
                if (remain > namedGap)
                {
                    foreach (var c in candidates)
                    {
                        if (remain <= 0) break;
                        var take = Math.Min(remain, c.gap);
                        InsertDistributedLegacy(conn, s, newSeano, c.orno, take, c.id, dept, per);
                        remain -= take;
                    }
                }
                else
                {
                    var named = candidates.Where(c => c.gname != "").ToList();
                    decimal allocated = 0;
                    for (var i = 0; i < named.Count; i++)
                    {
                        decimal take = i == named.Count - 1
                            ? remain - allocated
                            : Math.Round(remain * (named[i].gap / namedGap), 0, MidpointRounding.AwayFromZero);
                        if (take > 0)
                            InsertDistributedLegacy(conn, s, newSeano, named[i].orno, take, named[i].id, dept, per);
                        allocated += take;
                    }
                }
            }
        }
        return newSeano;
    }

    private static void InsertDistributedLegacy(SqlConnection conn,
        (int id, string cp, string cno, int item, string nno, string na1, string ty, string fa,
         decimal jkamount, decimal iprj, decimal iprc, decimal oprc, decimal oprb, string posi,
         string depotno, string orno, string unit, string fkfs, string gyname, string dc, string box,
         string pno) s, string newSeano, string orno, decimal jkamount, int orderItemId, string dpm, string per)
    {
        // item 取订单行 item（row103 L71）；空单行 item=0，note=''
        var item = orderItemId != 0
            ? conn.ExecuteScalar<int?>("SELECT item FROM tborder WHERE id = @id", new { id = orderItemId }) ?? 0
            : 0;
        conn.Execute(
            """
            INSERT INTO tbisorsm
                (pno, seano, box, cno, item, nno, na1, fa, ty, jkamount, fpamount, iprj, iprc, oprc, oprb,
                 posi, depotno, cp, fkfs, gyname, unit, dc, note, indate, per, orno, dpm)
            VALUES
                (@pno, @seano, @box, @cno, @item, @nno, @na1, @fa, @ty, @jkamount, @fpamount, @iprj, @iprc, @oprc, @oprb,
                 @posi, @depotno, @cp, @fkfs, @gyname, @unit, @dc, @note, @indate, @per, @orno, @dpm)
            """,
            new
            {
                pno = Ansi(s.pno, 12), seano = Ansi(newSeano, 15), box = Ansi(s.box, 15),
                cno = Ansi(s.cno, 15), item, nno = Ansi(s.nno, 18), na1 = Ansi(s.na1, 40),
                fa = Ansi(s.fa, 8), ty = Ansi(s.ty, 14), jkamount, fpamount = jkamount,
                s.iprj, s.iprc, s.oprc, s.oprb,
                posi = Ansi(s.posi, 10), depotno = Ansi(s.depotno, 10), cp = Ansi(s.cp, 10),
                fkfs = Ansi(s.fkfs, 10), gyname = Ansi(s.gyname, 60), unit = Ansi(s.unit, 4),
                dc = Ansi(s.dc, 8), note = Ansi("", 100), indate = DateTime.Today,
                per = Ansi(per, 6), orno = Ansi(orno, 14), dpm = Ansi(dpm, 10),
            });
    }

    /// <summary>
    /// 场景 34 旧侧：调拨入库确认（fr_dbrk row7 单条 + __.row68 挂帐段原样重放，与 DbrkRepository.ConfirmOneAsync 对照）：
    /// 仓位空拦截（P_RKPOSINULL）→ 负库存预演（P_WAY/P_XZFA）→ pty/pfa 回写 → exec dbrk @client,@paper,2,@per,@dpm
    /// → SELECT RTRIM(CNO) FROM c_rfh → guno∈{挂帐,托收,汇款} 时 tbysyf 12 列挂帐行（pt=ROUND(Σoprc*(100-yhl)/100*ckamount,2)，
    /// CB/TICK/YJB 继承同 gname+dpm 最近行）。
    /// </summary>
    public static string DbrkConfirm(SqlConnection conn, string dept, string per, string paper,
        string client, string dpmname)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        var dpm = Ansi(dept, 10);

        // row7 L26 单据行
        var lines = conn.Query<(int id, string cp, string nno, string na1, decimal iprc, string depotno,
            string posi, string fa, decimal ckamount, string guno)>(
            """
            SELECT id, RTRIM(cp) cp, RTRIM(nno) nno, RTRIM(na1) na1, iprc, RTRIM(depotno) depotno,
                   RTRIM(posi) posi, RTRIM(fa) fa, ckamount, RTRIM(guno) guno
            FROM tbsada WITH (UPDLOCK) WHERE paper = @paper AND type = N'调拨' ORDER BY id
            """,
            new { paper = Ansi(paper.Trim(), 10) }).AsList();
        if (lines.Count == 0)
            throw new InvalidOperationException($"调拨单 {paper.Trim()} 不存在或无明细行！");

        // row7 L8-15 仓位空拦截（P_RKPOSINULL）
        var rkposinull = conn.ExecuteScalar<bool>(
            "SELECT ISNULL(valuel, 0) FROM tbsysset WITH (NOLOCK) WHERE type = N'system' AND name = N'配件入库不许空仓位'");
        if (rkposinull && lines.Any(l => string.IsNullOrWhiteSpace(l.posi)))
        {
            var n = lines.Count(l => string.IsNullOrWhiteSpace(l.posi));
            throw new InvalidOperationException($"有 {n} 个编码的仓位为空，不允许入库！");
        }

        // row7 L26-63 负库存预演（P_WAY/P_XZFA 与登录装载同源）
        var pwayRow = conn.ExecuteScalar<decimal?>("SELECT valuen FROM tbsysset WHERE RTRIM(name) = N'均价' AND type = 'system'");
        var pway = pwayRow is null or < 1 or > 2 ? 2 : (int)pwayRow.Value;
        var xzfa = conn.ExecuteScalar<bool>("SELECT ISNULL(valuel, 0) FROM tbsysset WHERE RTRIM(name) = N'库存是否分产地' AND type = 'system'");
        var wayIp = pway == 1 ? " AND iprc = @iprc" : "";
        var xzfaSql = xzfa ? " AND fa = @fa" : "";
        var groups = lines.GroupBy(l => (l.cp, l.nno, pway == 1 ? l.iprc : 0m, l.posi, l.depotno, xzfa ? l.fa : ""));
        foreach (var g in groups)
        {
            var kc = conn.ExecuteScalar<decimal>(
                $"""
                SELECT ISNULL(SUM(kcamount), 0) FROM tbisto WITH (UPDLOCK)
                WHERE cp = @cp AND nno = @nno AND posi = @posi AND depotno = @depotno
                  AND dpm = @dpm{wayIp}{xzfaSql}
                """,
                new { cp = Ansi(g.Key.Item1, 10), nno = Ansi(g.Key.Item2, 18), posi = Ansi(g.Key.Item4, 10),
                      depotno = Ansi(g.Key.Item5, 10), dpm, iprc = g.Key.Item3, fa = Ansi(g.Key.Item6, 8) });
            var sum = g.Sum(x => x.ckamount);
            if (sum > 0 && kc + sum < 0)
                throw new InvalidOperationException($"{g.Key.Item2} 库存不足：预演后为负数，中止入库！");
        }

        // row7 L64-70 pty/pfa 回写
        foreach (var l in lines)
            conn.Execute("UPDATE tbsada SET pty = @posi, pfa = @depotno WHERE id = @id",
                new { posi = Ansi(l.posi, 10), depotno = Ansi(l.depotno, 10), id = l.id });

        // row7 L71 exec dbrk（SP 原样，@iprccount=2 恒定）→ c_rfh.CNO
        var newPaper = conn.ExecuteScalar<string>(
            "exec dbrk @client, @paper, 2, @per, @dpm; SELECT RTRIM(CNO) FROM c_rfh",
            new { client = Ansi(client, 50), paper = Ansi(paper.Trim(), 10), per = Ansi(per, 8), dpm },
            commandType: CommandType.Text);
        if (string.IsNullOrWhiteSpace(newPaper))
            throw new InvalidOperationException($"dbrk 未返回进库单号（单据 {paper.Trim()}）");

        // __.row68 L71-105 挂帐写 tbysyf
        var guno = lines.FirstOrDefault(l => l.guno is "挂帐" or "托收" or "汇款").guno;
        if (guno != null)
        {
            var pt = conn.ExecuteScalar<decimal>(
                """
                SELECT ISNULL(ROUND(SUM(oprc * (100 - ISNULL(yhl, 0)) / 100 * ckamount), 2), 0)
                FROM tbsada WHERE type = N'调拨' AND paper = @paper
                """,
                new { paper = Ansi(paper.Trim(), 10) });
            if (pt != 0)
            {
                var prior = conn.QueryFirstOrDefault<(decimal cb, decimal tick, decimal yjb)>(
                    "SELECT TOP 1 ISNULL(cb, 0), ISNULL(tick, 0), ISNULL(yjb, 0) FROM tbysyf WITH (UPDLOCK) " +
                    "WHERE gname = @gname AND dpm = @dpm ORDER BY datime DESC",
                    new { gname = Ansi(dpmname, 60), dpm });
                conn.Execute(
                    """
                    INSERT INTO tbysyf
                        (gname, datime, cno, ys, pno, fkfs, type, cb, yjb, tick, xm, dpm)
                    VALUES
                        (@gname, GETDATE(), @cno, @ys, @pno, @fkfs, N'调拨', @cb, @yjb, @tick, @fkfs, @dpm)
                    """,
                    new
                    {
                        gname = Ansi(dpmname, 60), cno = Ansi(paper.Trim(), 10), ys = pt,
                        pno = Ansi(conn.ExecuteScalar<string?>(
                            "SELECT MAX(RTRIM(pno)) FROM tbsada WHERE type = N'调拨' AND paper = @paper",
                            new { paper = Ansi(paper.Trim(), 10) }) ?? "", 10),
                        fkfs = Ansi(guno, 10), cb = prior.cb, yjb = prior.yjb, tick = prior.tick, dpm,
                    });
            }
        }
        return newPaper.Trim();
    }
}
