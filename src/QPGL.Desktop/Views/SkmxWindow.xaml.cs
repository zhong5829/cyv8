using System.Windows;
using QPGL.Infrastructure.Finance;

/// <summary>
/// 收/付款单明细窗（fr_skmx 复刻，只读）：表头（客户/单号/日期/方式/金额）+ tblsz 勾选行清单，
/// 双击/右键/关闭退出（旧 DblClick/RightClick RELEASE）。
/// </summary>
namespace QPGL.Desktop.Views;

public partial class SkmxWindow : Window
{
    public SkmxWindow(string type, SkmxDoc doc)
    {
        InitializeComponent();
        Title = "业务" + type + "单";
        DataContext = new
        {
            Doc = doc,
            Header = "业务" + type + "单明细",
            PayLabel = type + "方式：",
            PnoLabel = "票号：",
            AmountLabel = type + "金额：",
            JeHeader = type + "金额",
        };
    }
}
