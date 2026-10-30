using System.IO;
using System.Text.Json;

namespace QPGL.Desktop.Services;

/// <summary>
/// 本机用户偏好（%LOCALAPPDATA%\QPGL\settings.json）：
/// 与部署配置 appsettings.json 分离——升级不覆盖、按 Windows 用户隔离。
/// 只存非敏感项（如上次登录账号），读写失败一律静默，绝不阻断登录流程。
/// </summary>
public static class LocalSettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QPGL");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private sealed class SettingsModel
    {
        public string LastLoginOperator { get; set; } = "";
    }

    /// <summary>读取上次登录账号；文件不存在/损坏/无权限返回空串。</summary>
    public static string LoadLastOperator()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return "";
            var model = JsonSerializer.Deserialize<SettingsModel>(File.ReadAllText(SettingsPath));
            return model?.LastLoginOperator ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>保存上次登录账号（写临时文件后原子替换，防中断损坏）。</summary>
    public static void SaveLastOperator(string per)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var model = new SettingsModel { LastLoginOperator = per };
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(model));
            if (File.Exists(SettingsPath))
                File.Replace(tmp, SettingsPath, null);
            else
                File.Move(tmp, SettingsPath);
        }
        catch
        {
            // 保存失败不影响登录
        }
    }
}
