using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
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

    // 配件图片（2026-11-01 需求，旧 mypicture 类口径）：选择进缓冲、确定时落 tbImage
    private readonly List<(byte[] Bytes, string Ext, string Chksum)> _pendingImages = [];
    private byte[]? _existingImage;   // 窗口打开时已有的第一张图（仅预览，不重复落库）

    public PartAttributeEditWindow(PrnotyRow? source, string cp)
    {
        InitializeComponent();
        IntegerInput.Restrict(UqtyBox);   // 最小包装数只允许整数录入（2026-11-07 需求）
        Row = source is null ? new PrnotyRow { Cp = cp } : CloneRow(source);
        Loaded += async (_, _) => await LoadDictsAsync();
        Loaded += async (_, _) => await LoadPartImageAsync();
        PreviewKeyDown += Window_PreviewKeyDown;
        Loaded += (_, _) => { NnoBox.Focus(); NnoBox.SelectAll(); };   // 打开即聚焦首框（键盘流起点）
        FillFields();
        // 构造期赋值完成后再启用联想（FillFields 的预填不触发下拉）
        _na1Suppress = false;
        _tySuppress = false;
        _na1Debounce.Tick += async (_, _) => { _na1Debounce.Stop(); await LoadSuggestAsync(Na1Box, Na1List, Na1Popup, kw => Db.Prnoty.SuggestNamesAsync(kw)); };
        _tyDebounce.Tick += async (_, _) => { _tyDebounce.Stop(); await LoadSuggestAsync(TyBox, TyList, TyPopup, kw => Db.Prnoty.SuggestTysAsync(kw)); };
    }

    // ---- 零件名/车型联想（2026-11-02 需求）：tbname0/tbty0 拼音码匹配，↓↑选择，回车/单击填充 ----

    /// <summary>预填屏蔽标志：FillFields/填充候选时不触发联想（true=跳过）。</summary>
    private bool _na1Suppress = true;
    private bool _tySuppress = true;
    private readonly System.Windows.Threading.DispatcherTimer _na1Debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly System.Windows.Threading.DispatcherTimer _tyDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private void Na1Box_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_na1Suppress) return;
        _na1Debounce.Stop();
        _na1Debounce.Start();
    }

    private void TyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_tySuppress) return;
        _tyDebounce.Stop();
        _tyDebounce.Start();
    }

    private async System.Threading.Tasks.Task LoadSuggestAsync(TextBox box, ListBox list, Popup popup,
        Func<string, System.Threading.Tasks.Task<IReadOnlyList<string>>> query)
    {
        try
        {
            var kw = box.Text.Trim();
            IReadOnlyList<string> items = kw.Length == 0 ? [] : await query(kw);
            list.ItemsSource = items;
            if (items.Count > 0)
            {
                list.SelectedIndex = 0;   // 默认选第一项：直接回车即填充
                popup.IsOpen = true;
            }
            else
            {
                popup.IsOpen = false;
            }
        }
        catch
        {
            popup.IsOpen = false;   // 联想失败不阻断录入
        }
    }

    /// <summary>联想框共用键盘流：↓↑在候选间移动，回车填充选中项（不触发 K.确定），Esc 收起下拉。</summary>
    private void SuggestBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        var isNa1 = box == Na1Box;
        var list = isNa1 ? Na1List : TyList;
        var popup = isNa1 ? Na1Popup : TyPopup;
        if (!popup.IsOpen) return;   // 下拉未开 → 回车仍走 K.确定

        switch (e.Key)
        {
            case System.Windows.Input.Key.Down:
                list.SelectedIndex = Math.Min(list.SelectedIndex + 1, list.Items.Count - 1);
                list.ScrollIntoView(list.SelectedItem);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Up:
                list.SelectedIndex = Math.Max(list.SelectedIndex - 1, 0);
                list.ScrollIntoView(list.SelectedItem);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Enter when list.SelectedItem is string s:
                ApplySuggest(box, popup, s, isNa1);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Escape:
                popup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void Na1List_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Na1List.SelectedItem is string s) ApplySuggest(Na1Box, Na1Popup, s, isNa1: true);
    }

    private void TyList_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TyList.SelectedItem is string s) ApplySuggest(TyBox, TyPopup, s, isNa1: false);
    }

    /// <summary>填充候选值并收起下拉（填充期间屏蔽联想防抖）。</summary>
    private void ApplySuggest(TextBox box, Popup popup, string value, bool isNa1)
    {
        if (isNa1) _na1Suppress = true;
        else _tySuppress = true;
        box.Text = value;
        box.CaretIndex = box.Text.Length;
        if (isNa1) _na1Suppress = false;
        else _tySuppress = false;
        popup.IsOpen = false;
    }

    // ==== 键盘导航（2026-11-03 需求）：↓/↑/回车 在输入框间流转，末框回车=确定 ====

    /// <summary>
    /// 输入框流转顺序（按 Tab 顺序：左列 → 全宽行 → 右列）。
    /// 不入链：税率（只读带出）、备注（多行，Enter=换行/方向键=移光标，Tab 离开）。
    /// </summary>
    private Control[] FieldOrder =>
        [NnoBox, Na1Box, TyBox, FaBox, DcBox, UnitBox, IprjBox, CodeTsBox,
         EngBox, Note1Box, NoteBox, NnpBox, NnqBox, XnoBox, TnoBox, UqtyBox, NvolBox, SuttleBox, OprcCpBox];

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Handled) return;
        var key = e.Key;
        if (key is not (System.Windows.Input.Key.Down or System.Windows.Input.Key.Up or System.Windows.Input.Key.Enter)) return;

        // 多行备注框不参与导航（Enter=换行、方向键=移光标）
        if (ReferenceEquals(e.Source, NoteBox)) return;

        // 联想下拉打开时：键归联想框处理（SuggestBox_PreviewKeyDown 的 ↓↑选择/回车填充）
        if ((ReferenceEquals(e.Source, Na1Box) && Na1Popup.IsOpen) ||
            (ReferenceEquals(e.Source, TyBox) && TyPopup.IsOpen)) return;

        var fields = FieldOrder;
        var idx = Array.FindIndex(fields, f => f.IsKeyboardFocusWithin);
        if (idx < 0) return;

        // 可编辑 ComboBox 下拉展开时：键先由下拉自身处理（选项/收起），下一次回车才流转
        if (fields[idx] is ComboBox { IsDropDownOpen: true }) return;

        switch (key)
        {
            case System.Windows.Input.Key.Down:
                e.Handled = true;
                FocusField(fields, Math.Min(idx + 1, fields.Length - 1));
                break;
            case System.Windows.Input.Key.Up:
                e.Handled = true;
                FocusField(fields, Math.Max(idx - 1, 0));
                break;
            case System.Windows.Input.Key.Enter:
                e.Handled = true;
                if (idx == fields.Length - 1)
                    OkButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));   // 末框回车=确定
                else
                    FocusField(fields, idx + 1);
                break;
        }
    }

    /// <summary>流转到目标输入框（文本框聚焦即全选）。</summary>
    private static void FocusField(Control[] fields, int idx)
    {
        var f = fields[idx];
        f.Focus();
        if (f is TextBox tb) tb.SelectAll();
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

    private async void OkButton_Click(object sender, RoutedEventArgs e)
    {
        // 旧 CMDOK 校验：零件编号与零件名必填
        if (NnoBox.Text.Trim().Length == 0 || Na1Box.Text.Trim().Length == 0)
        {
            MessageBox.Show("请输入零件编号和零件名！", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (UqtyBox.Text.Trim().Length > 0 && !IntegerInput.IsInteger(UqtyBox.Text, out _))
        {
            MessageBox.Show("最小包装数必须是整数（不能带小数）！", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        WriteBack();
        // 添加的图片随「确定」写入 tbImage（旧 saveimg 随表单保存落库同语义；失败仅提示不阻断属性保存）
        if (_pendingImages.Count > 0)
        {
            try
            {
                var nno = NnoBox.Text.Trim();
                foreach (var (bytes, ext, chk) in _pendingImages)
                    await Db.Parts.SavePartImageAsync(nno, bytes, $"bmp\\{nno.Replace(" ", "")}_{chk}{ext}", chk);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"图片保存失败：{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        DialogResult = true;
    }

    // ==== 配件图片（2026-11-01 需求，旧 mypicture addimg/saveimg 口径）====

    /// <summary>窗口打开时预览已有第一张图（tbImage，开单页同源）。</summary>
    private async Task LoadPartImageAsync()
    {
        var nno = NnoBox.Text.Trim();
        if (nno.Length == 0) return;
        try
        {
            var bytes = await Db.Parts.GetPartImageAsync(nno);
            if (bytes is null) return;
            _existingImage = bytes;
            ShowThumb(bytes);
            ImgHint.Text = "已有图片";
        }
        catch { /* 预览失败不阻断编辑 */ }
    }

    /// <summary>添加图片：选 jpg/bmp/png，超上限先转 PNG 重压（旧 addimg IMG2IMG 口径），仍超拒绝；确定时统一落库。</summary>
    private async void AddImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "添加配件图片",
            Filter = "有效图片文件(*.jpg,*.bmp,*.png)|*.jpg;*.jpeg;*.bmp;*.png",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var bytes = await File.ReadAllBytesAsync(dlg.FileName);
            var max = await Db.Parts.GetMaxImageSizeAsync();
            if (max != 0 && bytes.Length > max)
            {
                var compressed = TryCompressToPng(bytes);
                if (compressed is null || compressed.Length >= max)
                {
                    MessageBox.Show($"{dlg.FileName} 文件太大!{Environment.NewLine}图片文件最大不能超过 {max / 1024}KB",
                        Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                bytes = compressed;
            }
            var chk = Checksum(bytes);
            if (_existingImage is not null && chk == Checksum(_existingImage))
            {
                ImgHint.Text = "该图片与已有图片相同";
                return;
            }
            if (_pendingImages.Any(p => p.Chksum == chk)) return;   // 同一文件重复添加忽略
            _pendingImages.Add((bytes, Path.GetExtension(dlg.FileName).ToLowerInvariant(), chk));
            ShowThumb(bytes);
            ImgHint.Text = $"已选 {_pendingImages.Count} 张新图片，点「确定」保存";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"读取图片失败：{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>缩略图单击看大图（优先最新添加的，否则已有图）。</summary>
    private void PartImage_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var bytes = _pendingImages.Count > 0 ? _pendingImages[^1].Bytes : _existingImage;
        if (bytes is null) return;
        new PartImageViewerWindow(NnoBox.Text.Trim(), bytes).ShowDialog();
    }

    private void ShowThumb(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            PartImageThumb.Source = bmp;
        }
        catch { PartImageThumb.Source = null; }
    }

    /// <summary>超限图片转 PNG 重压（旧 IMG2IMG→PNG 口径）；解码失败返回 null。</summary>
    private static byte[]? TryCompressToPng(byte[] source)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(source);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    /// <summary>内容指纹（FNV-1a 32 位十进制串，用于 tbImage.chksum 去重；fn 命名同旧格式 {nno}_{chksum}.{ext}）。</summary>
    private static string Checksum(byte[] bytes)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var b in bytes) { h ^= b; h *= 16777619; }
            return h.ToString();
        }
    }

    private static decimal PDec(string s) => decimal.TryParse(s?.Trim(), out var v) ? v : 0m;

    private static decimal? PDecOrNull(string s) => s?.Trim().Length > 0 && decimal.TryParse(s.Trim(), out var v) ? v : null;
}
