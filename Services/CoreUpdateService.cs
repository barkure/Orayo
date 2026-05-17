using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Orayo.Services;

public static class CoreUpdateService
{
    private static readonly string EngineDir = Path.Combine(AppContext.BaseDirectory, "Assets", "engine");
    private static readonly string RulesDir = Path.Combine(AppContext.BaseDirectory, "Assets", "rules");
    private static readonly string XrayExePath = Path.Combine(EngineDir, "xray.exe");
    private const string XrayWindows64Url = "https://github.com/XTLS/Xray-core/releases/latest/download/Xray-windows-64.zip";
    private const string GeoipUrl = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat";
    private const string GeositeUrl = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat";
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(300);
    private static readonly Regex VersionRegex = new(@"Xray\s+(?<version>[0-9.]+).*?\)\s+(?<commit>[0-9a-f]{7,})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = DownloadTimeout
    };

    public static async Task<XrayVersionInfo> GetXrayVersionInfoAsync()
    {
        if (!File.Exists(XrayExePath))
        {
            return new XrayVersionInfo("未找到 xray.exe", string.Empty, null, "未找到 xray.exe");
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = XrayExePath,
            Arguments = "version",
            WorkingDirectory = EngineDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        if (process is null)
        {
            return new XrayVersionInfo("无法启动 xray.exe", string.Empty, null, "无法启动 xray.exe");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var output = (await outputTask).Trim();
        if (!string.IsNullOrWhiteSpace(output))
        {
            var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            var match = VersionRegex.Match(firstLine);
            if (match.Success)
            {
                var version = match.Groups["version"].Value;
                var commit = match.Groups["commit"].Value;
                return new XrayVersionInfo(version, commit, $"https://github.com/XTLS/Xray-core/releases/tag/v{version}", firstLine);
            }

            return new XrayVersionInfo(firstLine, string.Empty, null, firstLine);
        }

        var error = (await errorTask).Trim();
        return new XrayVersionInfo(string.IsNullOrWhiteSpace(error) ? "未知版本" : error, string.Empty, null, error);
    }

    public sealed class StagedXrayCoreUpdate : IDisposable
    {
        internal StagedXrayCoreUpdate(string tempDir, string xrayExePath)
        {
            TempDir = tempDir;
            XrayExePath = xrayExePath;
        }

        internal string TempDir { get; }
        internal string XrayExePath { get; }

        public void Dispose()
        {
            TryDeleteDirectory(TempDir);
        }
    }

    public static async Task<StagedXrayCoreUpdate> StageXrayCoreUpdateAsync()
    {
        Directory.CreateDirectory(EngineDir);
        var tempDir = Path.Combine(Path.GetTempPath(), "Orayo", "xray-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "xray.zip");

        try
        {
            await DownloadFileAsync(XrayWindows64Url, zipPath);
            ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);
            var extractedXray = Directory.GetFiles(tempDir, "xray.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(extractedXray))
            {
                throw new InvalidOperationException("Xray 更新包中未找到 xray.exe。");
            }

            return new StagedXrayCoreUpdate(tempDir, extractedXray);
        }
        catch
        {
            TryDeleteDirectory(tempDir);
            throw;
        }
    }

    public static void ApplyXrayCoreUpdate(StagedXrayCoreUpdate update)
    {
        Directory.CreateDirectory(EngineDir);
        ReplaceFile(update.XrayExePath, XrayExePath);
    }

    public static async Task UpdateGeofilesAsync()
    {
        Directory.CreateDirectory(RulesDir);
        await DownloadAndReplaceFileAsync(GeoipUrl, Path.Combine(RulesDir, "geoip.dat"));
        await DownloadAndReplaceFileAsync(GeositeUrl, Path.Combine(RulesDir, "geosite.dat"));
    }

    private static async Task DownloadFileAsync(string url, string path)
    {
        if (!HttpClient.DefaultRequestHeaders.UserAgent.ToString().Contains("Orayo", StringComparison.OrdinalIgnoreCase))
        {
            HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Orayo");
        }

        using var cts = new CancellationTokenSource(DownloadTimeout);
        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cts.Token);
        await using var output = File.Create(path);
        await input.CopyToAsync(output, cts.Token);
        if (output.Length == 0)
        {
            throw new InvalidOperationException("下载文件为空。");
        }
    }

    private static async Task DownloadAndReplaceFileAsync(string url, string targetPath)
    {
        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await DownloadFileAsync(url, tempPath);
            ReplaceFile(tempPath, targetPath);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void ReplaceFile(string sourcePath, string targetPath)
    {
        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(sourcePath, tempPath, overwrite: true);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

public sealed record XrayVersionInfo(string Version, string Commit, string? ReleaseUrl, string Raw);
