using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace QPGL.Desktop.Services.LabelPrint;

/// <summary>
/// 标签打印本地设置（单文件 labelprint.json）：自定义模板 + 已删除内置名单 + 模板→打印机绑定 + 上次打印机。
/// 存储位置 %LOCALAPPDATA%\QPGL\（与 LocalSettingsService 同目录约定，升级不覆盖、按 Windows 用户隔离）。
/// 首次运行自动迁移旧版 4 个分散文件（labeltemplates.json 等），迁移成功后删除旧文件。
/// </summary>
public static class LabelTemplateService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QPGL");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "labelprint.json");

    // 旧版分散文件（仅迁移用）
    private static readonly string LegacyTemplatesPath = Path.Combine(SettingsDir, "labeltemplates.json");
    private static readonly string LegacyRemovedPath = Path.Combine(SettingsDir, "removed_builtin_templates.json");
    private static readonly string LegacyPrinterMapPath = Path.Combine(SettingsDir, "labeltemplate_printers.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>单文件设置模型</summary>
    private sealed class LabelPrintSettings
    {
        public List<LabelTemplate> Customs { get; set; } = new();
        /// <summary>已删除内置模板的名称记录（内置模板代码内定义，删除需持久化排除名单）</summary>
        public HashSet<string> RemovedBuiltIns { get; set; } = new();
        /// <summary>模板→打印机 绑定映射（内置/自定义模板统一存此，打印时绑定优先）</summary>
        public Dictionary<string, string> PrinterBindings { get; set; } = new();
        /// <summary>上次使用的打印机（独立页/对话框打印机下拉的默认值）</summary>
        public string? LastPrinter { get; set; }
    }

    /// <summary>读取设置：新格式单文件优先；旧版格式（labelprint.json 裸字符串 / 3 个分散文件）自动迁移；全部缺失/损坏返回默认值（绝不阻断打印）</summary>
    private static LabelPrintSettings Load()
    {
        if (File.Exists(SettingsPath))
        {
            var fresh = TryDeserialize<LabelPrintSettings>(File.ReadAllText(SettingsPath));
            if (fresh != null) return fresh;

            // 旧格式 labelprint.json = 裸字符串（上次打印机），转为新格式
            var legacyLast = TryDeserialize<string>(File.ReadAllText(SettingsPath));
            var legacy = new LabelPrintSettings { LastPrinter = legacyLast };
            Save(legacy);
            return legacy;
        }

        // ── 旧版 3 个分散文件迁移 ──
        var settings = new LabelPrintSettings();
        var hasLegacy = false;
        try
        {
            if (File.Exists(LegacyTemplatesPath))
            {
                settings.Customs = JsonSerializer.Deserialize<List<LabelTemplate>>(File.ReadAllText(LegacyTemplatesPath), JsonOptions) ?? new();
                hasLegacy = true;
            }
            if (File.Exists(LegacyRemovedPath))
            {
                settings.RemovedBuiltIns = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(LegacyRemovedPath), JsonOptions) ?? new();
                hasLegacy = true;
            }
            if (File.Exists(LegacyPrinterMapPath))
            {
                settings.PrinterBindings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LegacyPrinterMapPath), JsonOptions) ?? new();
                hasLegacy = true;
            }
        }
        catch { }

        if (hasLegacy && Save(settings))
        {
            // 新文件写入成功后清理旧文件（失败不清理，下次再迁）
            foreach (var p in new[] { LegacyTemplatesPath, LegacyRemovedPath, LegacyPrinterMapPath })
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            }
        }
        return settings;
    }

    private static T? TryDeserialize<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch { return null; }
    }

    /// <summary>保存设置（写临时文件后原子替换，防中断损坏）；失败静默返回 false</summary>
    private static bool Save(LabelPrintSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            if (File.Exists(SettingsPath))
                File.Replace(tmp, SettingsPath, null);
            else
                File.Move(tmp, SettingsPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>内置模板（热敏标签常见规格）</summary>
    public static List<LabelTemplate> BuiltInTemplates() => new()
    {
        new LabelTemplate { Name = "40×30", LabelWidthMm = 40, LabelHeightMm = 30, ColsPerRow = 1, FontSizeCode = 13, FontSizeName = 10, FontSizeCarType = 8 },
        new LabelTemplate { Name = "50×30", LabelWidthMm = 50, LabelHeightMm = 30, ColsPerRow = 1, FontSizeCode = 14, FontSizeName = 11, FontSizeCarType = 9 },
        new LabelTemplate { Name = "50×40", LabelWidthMm = 50, LabelHeightMm = 40, ColsPerRow = 1, FontSizeCode = 14, FontSizeName = 11, FontSizeCarType = 9 },
        new LabelTemplate { Name = "60×40", LabelWidthMm = 60, LabelHeightMm = 40, ColsPerRow = 1, FontSizeCode = 14, FontSizeName = 11, FontSizeCarType = 9 },
        new LabelTemplate { Name = "70×50", LabelWidthMm = 70, LabelHeightMm = 50, ColsPerRow = 1, FontSizeCode = 15, FontSizeName = 12, FontSizeCarType = 10 },
        new LabelTemplate { Name = "100×60", LabelWidthMm = 100, LabelHeightMm = 60, ColsPerRow = 1, FontSizeCode = 18, FontSizeName = 14, FontSizeCarType = 11 },
    };

    /// <summary>全部模板 = 内置(已删除的内置剔除) + 自定义</summary>
    public static List<LabelTemplate> GetAll()
    {
        var settings = Load();
        var list = BuiltInTemplates().Where(t => !settings.RemovedBuiltIns.Contains(t.Name)).ToList();
        foreach (var c in settings.Customs)
        {
            c.IsBuiltIn = false;
            list.Add(c);
        }
        return list;
    }

    /// <summary>新增自定义模板</summary>
    public static bool AddCustom(LabelTemplate tpl)
    {
        // 按当前可见模板校验同名（已删除的内置模板允许重建同名自定义模板）
        if (GetAll().Any(t => t.Name == tpl.Name)) return false;
        var settings = Load();
        settings.Customs.Add(tpl);
        return Save(settings);
    }

    /// <summary>查询模板绑定的打印机名（未绑定返回 null）</summary>
    public static string? GetBoundPrinter(string templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName)) return null;
        var settings = Load();
        return settings.PrinterBindings.TryGetValue(templateName.Trim(), out var printer) ? printer : null;
    }

    /// <summary>保存模板→打印机绑定（覆盖旧绑定）</summary>
    public static void SavePrinterBinding(string templateName, string printerName)
    {
        var settings = Load();
        settings.PrinterBindings[templateName.Trim()] = printerName;
        Save(settings);
    }

    /// <summary>解除模板的打印机绑定</summary>
    public static void ClearPrinterBinding(string templateName)
    {
        var settings = Load();
        if (settings.PrinterBindings.Remove(templateName.Trim()))
            Save(settings);
    }

    /// <summary>删除模板：自定义模板直接移除；内置模板记录到删除名单（代码内有定义，需排除显示）</summary>
    public static bool DeleteTemplate(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = name.Trim();

        var settings = Load();
        var custom = settings.Customs.FirstOrDefault(t => t.Name == name);
        if (custom != null)
        {
            settings.Customs.Remove(custom);
        }
        else if (BuiltInTemplates().Any(t => t.Name == name))
        {
            settings.RemovedBuiltIns.Add(name);
        }
        else
        {
            return false;
        }

        // 删除模板时一并清除其打印机绑定
        settings.PrinterBindings.Remove(name);
        return Save(settings);
    }

    /// <summary>保存（覆盖）同名自定义模板；内置模板不可保存，返回 false</summary>
    public static bool SaveCustom(LabelTemplate tpl)
    {
        if (tpl == null || tpl.IsBuiltIn) return false;
        var settings = Load();
        int idx = settings.Customs.FindIndex(t => t.Name == tpl.Name);
        if (idx < 0) return false;   // 无同名自定义模板 → 被视为新建，走 AddCustom
        settings.Customs[idx] = tpl.Clone();
        return Save(settings);
    }

    // ── 上次使用的打印机（qp11 语义：打印机下拉默认取打印设置保存值；本系统无对应配置项，改为记住上次选择） ──

    /// <summary>读取上次使用的打印机名（无记录返回 null）</summary>
    public static string? LoadLastPrinter() => Load().LastPrinter;

    /// <summary>保存上次使用的打印机名（失败静默，不影响打印）</summary>
    public static void SaveLastPrinter(string printerName)
    {
        var settings = Load();
        settings.LastPrinter = printerName;
        Save(settings);
    }
}
