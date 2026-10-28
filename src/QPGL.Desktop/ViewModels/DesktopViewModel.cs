using CommunityToolkit.Mvvm.Input;

namespace QPGL.Desktop.ViewModels;

/// <summary>工作台（qp11 DesktopControl 风格）。KPI 数据二期接报表 API。</summary>
public partial class DesktopViewModel(MainViewModel main)
{
    public IRelayCommand OpenPartnersCommand { get; } = main.OpenPartnersCommand;
    public IRelayCommand NotImplementedCommand { get; } = main.NotImplementedCommand;
}
