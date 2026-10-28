using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace QPGL.Desktop.Services;

/// <summary>
/// 在线更新（更新源：GitHub Releases，仓库 zhong5829/cyv8）。
/// 国内直连 GitHub 不稳定：API 与下载均先直连、失败自动改走可配置代理前缀
/// （ghproxy 类加速格式：{前缀}/{原始URL}，appsettings.json Update:ProxyPrefix 可自行更换）。
/// 升级 = 下载 Inno 安装包 → 退出本程序 → 运行安装包（AppId 固定覆盖安装，
/// appsettings.json / print.ini 由 onlyifdoesntexist 保护，升级不覆盖用户配置）。
/// </summary>
public static class UpdateService
{
    /// <summary>最新版本信息。</summary>
    public sealed record UpdateInfo(string Tag, Version Version, string AssetName, string DownloadUrl);

    public sealed record Config(string Repo, string ProxyPrefix, bool AutoCheck);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        // GitHub API 必须携带 User-Agent，否则 403
        c.DefaultRequestHeaders.UserAgent.ParseAdd("cyv8-updater");
        return c;
    }

    public static Config Options { get; } = LoadConfig();

    private static Config LoadConfig()
    {
        var repo = "zhong5829/cyv8";
        // gh-proxy.com 实测可用且支持 api.github.com 前缀代理；ghproxy.net 已失效（SSL 断连）
        var proxy = "https://gh-proxy.com";
        var autoCheck = true;
        try
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .Build();
            repo = config["Update:Repo"] ?? repo;
            proxy = config["Update:ProxyPrefix"] ?? proxy;
            var ac = config["Update:AutoCheck"];
            autoCheck = ac == null || !string.Equals(ac.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 配置缺失/损坏时用默认值，更新功能不应阻断启动
        }
        return new Config(repo, proxy, autoCheck);
    }

    /// <summary>当前程序版本（csproj Version / CI 由 tag 注入）。</summary>
    public static Version CurrentVersion { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>最近一次检查失败原因（成功或未检查为 null）。</summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// 查询最新 Release 的安装包资产。
    /// 返回 null：网络失败（LastError 有值）或已是最新版本（LastError 为 null）。
    /// </summary>
    public static async Task<UpdateInfo?> CheckLatestAsync(CancellationToken ct = default)
    {
        LastError = null;
        var api = $"https://api.github.com/repos/{Options.Repo}/releases/latest";
        string? json = null;
        foreach (var url in WithProxy(api))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                resp.EnsureSuccessStatusCode();
                json = await resp.Content.ReadAsStringAsync(ct);
                break;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                LastError = "连接超时（直连与代理均不可用）";
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }

        if (json == null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var version = ParseVersion(tag);
            if (version == null || version <= CurrentVersion) return null;

            UpdateInfo? found = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                var url = asset.GetProperty("browser_download_url").GetString();
                if (string.IsNullOrEmpty(url)) continue;
                // 优先取安装包命名（长远…_x.y.z_安装包.exe / *setup*.exe），否则任取首个 exe
                if (name.Contains("安装包") || name.Contains("setup", StringComparison.OrdinalIgnoreCase))
                    return new UpdateInfo(tag, version, name, url);
                found ??= new UpdateInfo(tag, version, name, url);
            }
            if (found != null) return found;
            LastError = "最新 Release 中未找到安装包资产";
            return null;
        }
        catch (Exception ex)
        {
            LastError = $"解析版本信息失败：{ex.Message}";
            return null;
        }
    }

    /// <summary>下载安装包到临时目录（带进度回调），返回本地文件路径。</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        var dest = Path.Combine(Path.GetTempPath(), info.AssetName);
        var channels = WithProxy(info.DownloadUrl).ToArray();
        Exception? last = null;

        for (var i = 0; i < channels.Length; i++)
        {
            try
            {
                using var resp = await Http.GetAsync(channels[i], HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None,
                    1 << 16, useAsync: true);

                var buf = new byte[1 << 16];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                    read += n;
                    if (total > 0) progress?.Report(read * 100.0 / total);
                }
                progress?.Report(100);
                return dest;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw new InvalidOperationException("下载失败，请检查网络或更换 appsettings.json 的 Update:ProxyPrefix 代理地址。", last);
    }

    /// <summary>运行 Inno 安装包（静默升级），调用方随后退出程序。</summary>
    public static void LaunchInstall(string setupPath)
    {
        Process.Start(new ProcessStartInfo(setupPath, "/SILENT") { UseShellExecute = true });
    }

    /// <summary>国内环境代理前缀优先（直连 api.github.com 被 SSL 阻断），代理失败再试直连兜底。</summary>
    private static IEnumerable<string> WithProxy(string url)
    {
        var p = Options.ProxyPrefix?.TrimEnd('/');
        if (!string.IsNullOrEmpty(p)) yield return $"{p}/{url}";
        yield return url;
    }

    /// <summary>解析 tag 为版本号（容忍 v 前缀与多余段）。</summary>
    private static Version? ParseVersion(string tag)
    {
        var s = tag.TrimStart('v', 'V');
        // 只取数字与点组成的前缀，滤掉 -beta 等后缀
        var end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
        s = s[..end].TrimEnd('.');
        return Version.TryParse(s, out var v) ? v : null;
    }
}
