using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace QPGL.Desktop.Services;

/// <summary>
/// 厂牌选择记忆（旧 fr_cp WHO 表复刻，T_.row5 / __.row2 语义）：
/// 旧系统在本地自由表 c:\mtmp\who 按"操作员名"记 NCP=上次选中行号；
/// 勾选"记住选择"时 REPLACE NCP，未勾选时重置为 1（CP_M0=1）；打开时无记录则默认首行。
/// 新实现：本地 JSON 文件（%LOCALAPPDATA%\QPGL\who.json），键=操作员名，值=行号（1 起）。
/// </summary>
public static class BrandPreference
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QPGL", "who.json");

    private static Dictionary<string, int> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(FilePath))
                       ?? [];
        }
        catch { /* 旧 WHO 表损坏时同样回退默认首行 */ }
        return [];
    }

    /// <summary>打开 fr_cp 时定位行（旧 __.row2：无记录默认第 1 行）。</summary>
    public static int GetIndex(string operatorName)
    {
        var all = Load();
        return all.TryGetValue(operatorName, out var ncp) && ncp >= 1 ? ncp : 1;
    }

    /// <summary>确认时保存（旧 T_.row5：勾选→记所选行；未勾选→重置 1）。</summary>
    public static void SaveIndex(string operatorName, int listIndex)
    {
        try
        {
            var all = Load();
            all[operatorName] = listIndex < 1 ? 1 : listIndex;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all));
        }
        catch { /* 本地写失败不影响选择流程 */ }
    }
}
