using System.Windows;
using System.Windows.Controls;
using QPGL.Desktop.Services;
using QPGL.Infrastructure.Parts;

namespace QPGL.Desktop.Views;

/// <summary>
/// 零件属性添加/修改窗（复刻旧 fr_prnoty frmAdd）：
/// 左列 零件编码/零件名/车型/产地/分类码/材料单位/含税价/税号/税率，
/// 右列 编码一/编码二/通用码/套件码/最小包装数/体积/净重/统一售价（旧码 AMJJ/BMJJ 隐藏不显示，最低售价本部署不显示），
/// 全宽 英文名/零件说明/备注，底部 K.确定 / C.取消。
/// 编辑基于传入行的副本，确定后由调用方写回保存，取消不改动原行。
/// </summary>
public partial class PartAttributeEditWindow : Window
{
    /// <summary>确定后的行（新增模式=新行，编辑模式=原行副本），调用方据此写回/入库。</summary>
    public PrnotyRow Row { get; }

    private List<ComplexRow> _tax = [];   // tbcomplex 全量（税号→税率）

    public PartAttributeEditWindow(PrnotyRow? source, string cp)
    {
        InitializeComponent();
        Row = source is null ? new PrnotyRow { Cp = cp } : CloneRow(source);
        Loaded += async (_, _) => await LoadDictsAsync();
        FillFields();
    }

    private static PrnotyRow CloneRow(PrnotyRow r) => new()
    {
        Id = r.Id, Cp = r.Cp, Nno = r.Nno, Na1 = r.Na1, Ty = r.Ty, Fa = r.Fa, Dc = r.Dc,
        Unit = r.Unit, Note = r.Note, Iprj = r.Iprj, Xno = r.Xno, Nnp = r.Nnp, Nnq = r.Nnq,
        Uqty = r.Uqty, Tno = r.Tno, Amjj = r.Amjj, Bmjj = r.Bmjj, Code_ts = r.Code_ts,
        Cxnno = r.Cxnno, Eng = r.Eng, Nvol = r.Nvol, Suttle = r.Suttle, Note1 = r.Note1,
        Oprc_cp = r.Oprc_cp, Oprc_min = r.Oprc_min,
    };

    /// <summary>把弹窗值写回 Row（税率只读不落库；oprc_min/amjj/bmjj 不在窗内，保留原值）。</summary>
    public void WriteBack()
    {
        Row.Nno = NnoBox.Text.Trim();
        Row.Na1 = Na1Box.Text.Trim();
        Row.Ty = TyBox.Text.Trim();
        Row.Fa = FaBox.Text.Trim();
        Row.Dc = DcBox.Text.Trim();
        Row.Unit = UnitBox.Text.Trim();
        Row.Iprj = PDec(IprjBox.Text);
        Row.Code_ts = CodeTsBox.Text.Trim();
        Row.Eng = EngBox.Text.Trim();
        Row.Note1 = Note1Box.Text;
        Row.Note = NoteBox.Text;
        Row.Nnp = NnpBox.Text.Trim();
        Row.Nnq = NnqBox.Text.Trim();
        Row.Xno = (int)PDec(XnoBox.Text);
        Row.Tno = (int)PDec(TnoBox.Text);
        Row.Uqty = PDec(UqtyBox.Text);
        Row.Nvol = NvolBox.Text.Trim();
        Row.Suttle = PDec(SuttleBox.Text);
        Row.Oprc_cp = PDecOrNull(OprcCpBox.Text);
    }

    private void FillFields()
    {
        NnoBox.Text = Row.Nno;
        Na1Box.Text = Row.Na1;
        TyBox.Text = Row.Ty;
        FaBox.Text = Row.Fa;
        DcBox.Text = Row.Dc;
        UnitBox.Text = Row.Unit;
        IprjBox.Text = Row.Iprj.ToString("0.00");
        CodeTsBox.Text = Row.Code_ts;
        EngBox.Text = Row.Eng;
        Note1Box.Text = Row.Note1;
        NoteBox.Text = Row.Note;
        NnpBox.Text = Row.Nnp;
        NnqBox.Text = Row.Nnq;
        XnoBox.Text = Row.Xno == 0 ? "" : Row.Xno.ToString();
        TnoBox.Text = Row.Tno == 0 ? "" : Row.Tno.ToString();
        UqtyBox.Text = Row.Uqty == 0 ? "" : Row.Uqty.ToString("0.##");
        NvolBox.Text = Row.Nvol;
        SuttleBox.Text = Row.Suttle == 0 ? "0.0000" : Row.Suttle.ToString("0.0000");
        OprcCpBox.Text = Row.Oprc_cp is null ? "" : Row.Oprc_cp.Value.ToString("0.00");
    }

    /// <summary>字典加载：产地=tbsysset type='prod'、分类码=type='dc'、材料单位=type='ljdl'（旧 PASSTH 行来源）；税号表=tbcomplex。</summary>
    private async Task LoadDictsAsync()
    {
        var dpm = AppSession.DeptNo;
        try
        {
            var prod = await Db.SysDefine.GetDictAsync(dpm, "prod");
            FaBox.ItemsSource = prod.Select(x => x.Name).ToList();
        }
        catch { /* 字典加载失败不阻断编辑 */ }
        try
        {
            var dc = await Db.SysDefine.GetDictAsync(dpm, "dc");
            DcBox.ItemsSource = dc.Select(x => x.Name).ToList();
        }
        catch { }
        try
        {
            var ljdl = await Db.SysDefine.GetDictAsync(dpm, "ljdl");
            UnitBox.ItemsSource = ljdl.Select(x => x.Name).ToList();
        }
        catch { }
        try
        {
            _tax = [.. await Db.Complex.GetAllAsync()];
        }
        catch { _tax = []; }
    }

    /// <summary>税号失焦：按税号从 tbcomplex 带出 low_rate（旧 txtCODE_TS.LostFocus 语义：无此税号则提示）。</summary>
    private void CodeTsBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var ts = CodeTsBox.Text.Trim();
        if (ts.Length == 0) { RateBox.Text = ""; return; }
        var hit = _tax.FirstOrDefault(x => x.Code_ts == ts);
        if (hit is null)
        {
            MessageBox.Show("无此税号！", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        RateBox.Text = hit.Low_rate.ToString("0.####");
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        // 旧 CMDOK 校验：零件编号与零件名必填
        if (NnoBox.Text.Trim().Length == 0 || Na1Box.Text.Trim().Length == 0)
        {
            MessageBox.Show("请输入零件编号和零件名！", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        WriteBack();
        DialogResult = true;
    }

    private static decimal PDec(string s) => decimal.TryParse(s?.Trim(), out var v) ? v : 0m;

    private static decimal? PDecOrNull(string s) => s?.Trim().Length > 0 && decimal.TryParse(s.Trim(), out var v) ? v : null;
}
